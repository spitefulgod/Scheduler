using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Scheduler.Api.Services
{
    // A table or view a source query is allowed to read
    public class SourceObjectName
    {
        public string Schema { get; set; } = "";
        public string Name { get; set; } = "";

        public SourceObjectName(string schema, string name)
        {
            Schema = schema;
            Name = name;
        }
        public override string ToString()
        {
            return Schema + "." + Name;
        }
    }

    // Whether a statement can be run against a source, and why not when it can't
    public class GuardResult
    {
        public bool IsAllowed { get; set; }
        public string? Reason { get; set; }

        public static GuardResult Ok()
        {
            return new GuardResult { IsAllowed = true };
        }
        public static GuardResult Reject(string reason)
        {
            return new GuardResult { IsAllowed = false, Reason = reason };
        }
    }

    // Sources are only ever read, so everything sent to one is checked here first
    // it has to be one SELECT that only reads the tables and views it's allowed to, anything else is turned down before it gets to the server
    // e.g. INSERT, UPDATE, EXEC, SELECT INTO, more than one statement, OPENROWSET, linked servers, other databases, sequences
    public static class ReadOnlySqlGuard
    {
        public static GuardResult Validate(string sql, IReadOnlyCollection<SourceObjectName> allowedObjects, string? currentDatabase = null)
        {
            if (string.IsNullOrWhiteSpace(sql))
                return GuardResult.Reject("Query is empty.");

            TSql160Parser parser = new TSql160Parser(true);
            TSqlFragment fragment;
            IList<ParseError> errors;
            using (StringReader reader = new StringReader(sql))
                fragment = parser.Parse(reader, out errors);

            if (errors.Count > 0)
                return GuardResult.Reject($"Query could not be parsed: {errors[0].Message} (line {errors[0].Line}).");

            TSqlScript? script = fragment as TSqlScript;
            if (script == null || script.Batches.Count != 1 || script.Batches[0].Statements.Count != 1)
                return GuardResult.Reject("Exactly one SELECT statement is allowed.");

            // only a plain SELECT, the statements built on it (e.g. SelectStatementSnippet) are turned down too
            SelectStatement? select = script.Batches[0].Statements[0] as SelectStatement;
            if (select == null || select.GetType() != typeof(SelectStatement))
                return GuardResult.Reject("Only SELECT statements are allowed against source connections.");

            if (select.Into != null)
                return GuardResult.Reject("SELECT ... INTO is not allowed, it creates a table.");

            Visitor visitor = new Visitor(allowedObjects, currentDatabase);
            select.Accept(visitor);
            return visitor.Failure ?? GuardResult.Ok();
        }

        // Walks the statement and notes the first thing it finds that isn't allowed
        private class Visitor : TSqlFragmentVisitor
        {
            private readonly IReadOnlyCollection<SourceObjectName> _allowed;
            private readonly string? _currentDatabase;
            private readonly HashSet<string> _cteNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public GuardResult? Failure { get; private set; }

            public Visitor(IReadOnlyCollection<SourceObjectName> allowed, string? currentDatabase)
            {
                _allowed = allowed;
                _currentDatabase = currentDatabase;
            }
            private void Fail(string reason)
            {
                if (Failure == null)
                    Failure = GuardResult.Reject(reason);
            }

            public override void ExplicitVisit(SelectStatement node)
            {
                // the CTE names first, so using one isn't taken as a table
                if (node.WithCtesAndXmlNamespaces?.CommonTableExpressions != null)
                    foreach (CommonTableExpression cte in node.WithCtesAndXmlNamespaces.CommonTableExpressions)
                        _cteNames.Add(cte.ExpressionName.Value);
                base.ExplicitVisit(node);
            }
            public override void Visit(NamedTableReference node)
            {
                SchemaObjectName name = node.SchemaObject;
                if (name.ServerIdentifier != null)
                {
                    Fail("Linked-server (four-part) names are not allowed.");
                    return;
                }
                if (name.DatabaseIdentifier != null && !string.Equals(name.DatabaseIdentifier.Value, _currentDatabase, StringComparison.OrdinalIgnoreCase))
                {
                    Fail($"Reading from another database ('{name.DatabaseIdentifier.Value}') is not allowed.");
                    return;
                }
                if (name.SchemaIdentifier == null && _cteNames.Contains(name.BaseIdentifier.Value))
                    return;

                string schema = name.SchemaIdentifier?.Value ?? "dbo";
                string objectName = name.BaseIdentifier.Value;
                bool allowed = _allowed.Any(x => string.Equals(x.Schema, schema, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(x.Name, objectName, StringComparison.OrdinalIgnoreCase));
                if (!allowed)
                    Fail($"'{schema}.{objectName}' is not a configured table or view for this connection.");
            }
            public override void Visit(OpenRowsetTableReference node) { Fail("OPENROWSET is not allowed."); }
            public override void Visit(BulkOpenRowset node) { Fail("OPENROWSET(BULK) is not allowed."); }
            public override void Visit(OpenQueryTableReference node) { Fail("OPENQUERY is not allowed."); }
            public override void Visit(AdHocTableReference node) { Fail("OPENDATASOURCE is not allowed."); }
            public override void Visit(OpenXmlTableReference node) { Fail("OPENXML is not allowed."); }
            public override void Visit(SchemaObjectFunctionTableReference node) { Fail("Table-valued functions are not allowed."); }
            public override void Visit(BuiltInFunctionTableReference node) { Fail("Table-valued functions are not allowed."); }
            public override void Visit(GlobalFunctionTableReference node) { Fail("Table-valued functions are not allowed."); }
            public override void Visit(VariableTableReference node) { Fail("Table variables are not allowed."); }
            public override void Visit(NextValueForExpression node) { Fail("NEXT VALUE FOR changes a sequence and is not allowed."); }
        }
    }
}
