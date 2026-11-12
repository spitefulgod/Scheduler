using System.Globalization;
using System.Text;

namespace Scheduler.Solver
{
    // A small, safe expression language for rule parameters, duration formulas and selectors, never SQL
    // numbers, 'text', true / false, names such as qty, item.compound, resource.platen_slots or tooling.size
    // + - * / %, = != <> < <= > >=, and or not, ( ), and the functions min max abs ceil floor round if isnull
    // e.g. qty * cycle_secs / 60, item.compound = 'SIL-50' and item.qty > 500, if(tooling.cavities > 4, 30, 15)
    public class Formula
    {
        private readonly Node _root;
        public string Text { get; }

        private Formula(string text, Node root)
        {
            Text = text;
            _root = root;
        }

        // Reads an expression, the message says where it went wrong when it can't
        public static Formula Parse(string text)
        {
            Parser parser = new Parser(text ?? "");
            Node root = parser.ParseExpression();
            parser.ExpectEnd();
            return new Formula(text ?? "", root);
        }
        public static bool TryParse(string text, out Formula? formula, out string error)
        {
            try
            {
                formula = Parse(text);
                error = "";
                return true;
            }
            catch (Exception ex)
            {
                formula = null;
                error = ex.Message;
                return false;
            }
        }

        // Works the expression out, resolve gives a name's value, e.g. item.qty, null when it has none
        public object? Evaluate(Func<string, object?> resolve)
        {
            return _root.Evaluate(resolve);
        }
        public double? EvaluateNumber(Func<string, object?> resolve)
        {
            return ToNumber(Evaluate(resolve));
        }
        public bool EvaluateBool(Func<string, object?> resolve)
        {
            return ToBool(Evaluate(resolve));
        }
        // The names the expression reads, e.g. item.qty
        public IEnumerable<string> GetNames()
        {
            List<string> names = new List<string>();
            _root.CollectNames(names);
            return names.Distinct(StringComparer.OrdinalIgnoreCase);
        }

        public static double? ToNumber(object? value)
        {
            switch (value)
            {
                case null:
                    return null;
                case double d:
                    return d;
                case bool b:
                    return b ? 1 : 0;
                case DateTime dt:
                    return dt.Ticks / (double)TimeSpan.TicksPerMinute;
                case IConvertible convertible when value is not string:
                    return convertible.ToDouble(CultureInfo.InvariantCulture);
                case string s:
                    return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : null;
                default:
                    return null;
            }
        }
        public static bool ToBool(object? value)
        {
            switch (value)
            {
                case null:
                    return false;
                case bool b:
                    return b;
                case string s:
                    return s.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Equals("y", StringComparison.OrdinalIgnoreCase)
                        || s.Equals("yes", StringComparison.OrdinalIgnoreCase) || s == "1";
                default:
                    return (ToNumber(value) ?? 0) != 0;
            }
        }
        public static string? ToText(object? value)
        {
            switch (value)
            {
                case null:
                    return null;
                case double d:
                    return d.ToString(CultureInfo.InvariantCulture);
                case DateTime dt:
                    return dt.ToString("s", CultureInfo.InvariantCulture);
                case IFormattable formattable:
                    return formattable.ToString(null, CultureInfo.InvariantCulture);
                default:
                    return value.ToString();
            }
        }
        // Equal as numbers when both are, otherwise as text ignoring case
        public static bool AreEqual(object? a, object? b)
        {
            if (a == null || b == null)
                return a == null && b == null;
            double? x = a is string ? null : ToNumber(a);
            double? y = b is string ? null : ToNumber(b);
            if (x.HasValue && y.HasValue)
                return Math.Abs(x.Value - y.Value) < 1e-9;
            if (x.HasValue || y.HasValue)
            {
                double? xs = ToNumber(a), ys = ToNumber(b);
                if (xs.HasValue && ys.HasValue)
                    return Math.Abs(xs.Value - ys.Value) < 1e-9;
            }
            return string.Equals(ToText(a)?.Trim(), ToText(b)?.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        private static int Compare(object? a, object? b)
        {
            double? x = ToNumber(a), y = ToNumber(b);
            if (x.HasValue && y.HasValue)
                return x.Value.CompareTo(y.Value);
            return string.Compare(ToText(a), ToText(b), StringComparison.OrdinalIgnoreCase);
        }

        private abstract class Node
        {
            public abstract object? Evaluate(Func<string, object?> resolve);
            public virtual void CollectNames(List<string> names)
            {
            }
        }
        private class ConstantNode : Node
        {
            private readonly object? _value;
            public ConstantNode(object? value)
            {
                _value = value;
            }
            public override object? Evaluate(Func<string, object?> resolve)
            {
                return _value;
            }
        }
        private class NameNode : Node
        {
            private readonly string _name;
            public NameNode(string name)
            {
                _name = name;
            }
            public override object? Evaluate(Func<string, object?> resolve)
            {
                return resolve(_name);
            }
            public override void CollectNames(List<string> names)
            {
                names.Add(_name);
            }
        }
        private class UnaryNode : Node
        {
            private readonly string _op;
            private readonly Node _operand;
            public UnaryNode(string op, Node operand)
            {
                _op = op;
                _operand = operand;
            }
            public override object? Evaluate(Func<string, object?> resolve)
            {
                object? value = _operand.Evaluate(resolve);
                if (_op == "not")
                    return !ToBool(value);
                double? number = ToNumber(value);
                return number.HasValue ? -number.Value : null;
            }
            public override void CollectNames(List<string> names)
            {
                _operand.CollectNames(names);
            }
        }
        private class BinaryNode : Node
        {
            private readonly string _op;
            private readonly Node _left;
            private readonly Node _right;
            public BinaryNode(string op, Node left, Node right)
            {
                _op = op;
                _left = left;
                _right = right;
            }
            public override object? Evaluate(Func<string, object?> resolve)
            {
                if (_op == "and")
                    return ToBool(_left.Evaluate(resolve)) && ToBool(_right.Evaluate(resolve));
                if (_op == "or")
                    return ToBool(_left.Evaluate(resolve)) || ToBool(_right.Evaluate(resolve));
                object? a = _left.Evaluate(resolve);
                object? b = _right.Evaluate(resolve);
                switch (_op)
                {
                    case "=":
                        return AreEqual(a, b);
                    case "!=":
                        return !AreEqual(a, b);
                    case "<":
                        return a != null && b != null && Compare(a, b) < 0;
                    case "<=":
                        return a != null && b != null && Compare(a, b) <= 0;
                    case ">":
                        return a != null && b != null && Compare(a, b) > 0;
                    case ">=":
                        return a != null && b != null && Compare(a, b) >= 0;
                }
                // text joins with + when either side isn't a number
                if (_op == "+" && (a is string || b is string) && (ToNumber(a) == null || ToNumber(b) == null))
                    return (ToText(a) ?? "") + (ToText(b) ?? "");
                double? x = ToNumber(a), y = ToNumber(b);
                if (!x.HasValue || !y.HasValue)
                    return null;
                switch (_op)
                {
                    case "+":
                        return x.Value + y.Value;
                    case "-":
                        return x.Value - y.Value;
                    case "*":
                        return x.Value * y.Value;
                    case "/":
                        return y.Value == 0 ? null : x.Value / y.Value;
                    case "%":
                        return y.Value == 0 ? null : x.Value % y.Value;
                }
                throw new Exception($"Unknown operator {_op}");
            }
            public override void CollectNames(List<string> names)
            {
                _left.CollectNames(names);
                _right.CollectNames(names);
            }
        }
        private class CallNode : Node
        {
            private readonly string _name;
            private readonly List<Node> _args;
            public CallNode(string name, List<Node> args)
            {
                _name = name.ToLowerInvariant();
                _args = args;
                int[] counts = _name switch
                {
                    "min" or "max" => new int[] { -1 },
                    "abs" or "ceil" or "floor" or "round" => new int[] { 1 },
                    "if" => new int[] { 3 },
                    "isnull" => new int[] { 2 },
                    _ => throw new Exception($"Unknown function {name}, the functions are min, max, abs, ceil, floor, round, if and isnull")
                };
                if (counts[0] >= 0 && args.Count != counts[0])
                    throw new Exception($"{name} takes {counts[0]} value{(counts[0] == 1 ? "" : "s")}");
                if (counts[0] < 0 && args.Count == 0)
                    throw new Exception($"{name} needs at least one value");
            }
            public override object? Evaluate(Func<string, object?> resolve)
            {
                switch (_name)
                {
                    case "if":
                        return ToBool(_args[0].Evaluate(resolve)) ? _args[1].Evaluate(resolve) : _args[2].Evaluate(resolve);
                    case "isnull":
                        return _args[0].Evaluate(resolve) ?? _args[1].Evaluate(resolve);
                }
                List<double> values = new List<double>();
                foreach (Node arg in _args)
                {
                    double? value = ToNumber(arg.Evaluate(resolve));
                    if (!value.HasValue)
                        return null;
                    values.Add(value.Value);
                }
                switch (_name)
                {
                    case "min":
                        return values.Min();
                    case "max":
                        return values.Max();
                    case "abs":
                        return Math.Abs(values[0]);
                    case "ceil":
                        return Math.Ceiling(values[0] - 1e-9);
                    case "floor":
                        return Math.Floor(values[0] + 1e-9);
                    default:
                        return Math.Round(values[0], MidpointRounding.AwayFromZero);
                }
            }
            public override void CollectNames(List<string> names)
            {
                foreach (Node arg in _args)
                    arg.CollectNames(names);
            }
        }

        // Recursive descent, or < and < comparison < + - < * / % < unary < value
        private class Parser
        {
            private readonly string _text;
            private int _pos;

            public Parser(string text)
            {
                _text = text;
            }

            public void ExpectEnd()
            {
                SkipSpace();
                if (_pos < _text.Length)
                    throw Error($"Didn't expect '{_text.Substring(_pos)}'");
            }
            public Node ParseExpression()
            {
                SkipSpace();
                if (_pos >= _text.Length)
                    throw new Exception("The expression is empty");
                return ParseOr();
            }
            private Node ParseOr()
            {
                Node left = ParseAnd();
                while (TakeWord("or"))
                    left = new BinaryNode("or", left, ParseAnd());
                return left;
            }
            private Node ParseAnd()
            {
                Node left = ParseNot();
                while (TakeWord("and"))
                    left = new BinaryNode("and", left, ParseNot());
                return left;
            }
            private Node ParseNot()
            {
                if (TakeWord("not"))
                    return new UnaryNode("not", ParseNot());
                return ParseComparison();
            }
            private Node ParseComparison()
            {
                Node left = ParseAdditive();
                SkipSpace();
                foreach (string op in new string[] { "<=", ">=", "!=", "<>", "==", "=", "<", ">" })
                    if (Take(op))
                        return new BinaryNode(op == "<>" ? "!=" : op == "==" ? "=" : op, left, ParseAdditive());
                return left;
            }
            private Node ParseAdditive()
            {
                Node left = ParseMultiplicative();
                while (true)
                {
                    SkipSpace();
                    if (Take("+"))
                        left = new BinaryNode("+", left, ParseMultiplicative());
                    else if (Take("-"))
                        left = new BinaryNode("-", left, ParseMultiplicative());
                    else
                        return left;
                }
            }
            private Node ParseMultiplicative()
            {
                Node left = ParseUnary();
                while (true)
                {
                    SkipSpace();
                    if (Take("*"))
                        left = new BinaryNode("*", left, ParseUnary());
                    else if (Take("/"))
                        left = new BinaryNode("/", left, ParseUnary());
                    else if (Take("%"))
                        left = new BinaryNode("%", left, ParseUnary());
                    else
                        return left;
                }
            }
            private Node ParseUnary()
            {
                SkipSpace();
                if (Take("-"))
                    return new UnaryNode("-", ParseUnary());
                return ParseValue();
            }
            private Node ParseValue()
            {
                SkipSpace();
                if (_pos >= _text.Length)
                    throw Error("Expected a value");
                char c = _text[_pos];
                if (c == '(')
                {
                    _pos++;
                    Node inner = ParseOr();
                    SkipSpace();
                    if (!Take(")"))
                        throw Error("Expected )");
                    return inner;
                }
                if (c == '\'' || c == '"')
                    return new ConstantNode(ReadString(c));
                if (char.IsDigit(c) || (c == '.' && _pos + 1 < _text.Length && char.IsDigit(_text[_pos + 1])))
                    return new ConstantNode(ReadNumber());
                if (char.IsLetter(c) || c == '_' || c == '[')
                {
                    string name = ReadName();
                    if (name.Equals("true", StringComparison.OrdinalIgnoreCase))
                        return new ConstantNode(true);
                    if (name.Equals("false", StringComparison.OrdinalIgnoreCase))
                        return new ConstantNode(false);
                    if (name.Equals("null", StringComparison.OrdinalIgnoreCase))
                        return new ConstantNode(null);
                    SkipSpace();
                    if (Take("("))
                    {
                        List<Node> args = new List<Node>();
                        SkipSpace();
                        if (!Take(")"))
                        {
                            do
                                args.Add(ParseOr());
                            while (TakeComma());
                            SkipSpace();
                            if (!Take(")"))
                                throw Error("Expected )");
                        }
                        return new CallNode(name, args);
                    }
                    return new NameNode(name);
                }
                throw Error($"Didn't expect '{c}'");
            }
            private bool TakeComma()
            {
                SkipSpace();
                return Take(",");
            }
            private string ReadString(char quote)
            {
                _pos++;
                StringBuilder text = new StringBuilder();
                while (_pos < _text.Length)
                {
                    char c = _text[_pos++];
                    if (c == quote)
                    {
                        // two quotes are one quote
                        if (_pos < _text.Length && _text[_pos] == quote)
                        {
                            text.Append(quote);
                            _pos++;
                            continue;
                        }
                        return text.ToString();
                    }
                    text.Append(c);
                }
                throw Error("The text isn't closed");
            }
            private double ReadNumber()
            {
                int start = _pos;
                while (_pos < _text.Length && (char.IsDigit(_text[_pos]) || _text[_pos] == '.'))
                    _pos++;
                return double.Parse(_text.Substring(start, _pos - start), CultureInfo.InvariantCulture);
            }
            // A name, with dots for item.compound, or [any text] for column names with spaces
            private string ReadName()
            {
                StringBuilder name = new StringBuilder();
                while (_pos < _text.Length)
                {
                    char c = _text[_pos];
                    if (c == '[')
                    {
                        int close = _text.IndexOf(']', _pos);
                        if (close < 0)
                            throw Error("Expected ]");
                        name.Append(_text, _pos + 1, close - _pos - 1);
                        _pos = close + 1;
                    }
                    else if (char.IsLetterOrDigit(c) || c == '_' || c == '.')
                    {
                        name.Append(c);
                        _pos++;
                    }
                    else
                        break;
                }
                return name.ToString();
            }
            private bool TakeWord(string word)
            {
                SkipSpace();
                if (_pos + word.Length > _text.Length || string.Compare(_text, _pos, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) != 0)
                    return false;
                int after = _pos + word.Length;
                if (after < _text.Length && (char.IsLetterOrDigit(_text[after]) || _text[after] == '_' || _text[after] == '.'))
                    return false;
                _pos = after;
                return true;
            }
            private bool Take(string symbol)
            {
                if (_pos + symbol.Length > _text.Length || string.CompareOrdinal(_text, _pos, symbol, 0, symbol.Length) != 0)
                    return false;
                _pos += symbol.Length;
                return true;
            }
            private void SkipSpace()
            {
                while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos]))
                    _pos++;
            }
            private Exception Error(string message)
            {
                return new Exception($"{message} at position {_pos + 1} of '{_text}'");
            }
        }
    }
}
