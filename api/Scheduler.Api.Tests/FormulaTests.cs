using Scheduler.Solver;
using Xunit;

namespace Scheduler.Api.Tests
{
    // The expression language rules, formulas and selectors are written in
    public class FormulaTests
    {
        private static readonly Dictionary<string, object?> Values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["qty"] = 500,
            ["cycle_secs"] = 120,
            ["item.compound"] = "SIL-50",
            ["resource.cell"] = "A",
            ["item.cell"] = "a",
            ["tooling.size"] = 3,
            ["resource.max_tool_size"] = 4,
            ["missing"] = null
        };

        private static object? Run(string text)
        {
            return Formula.Parse(text).Evaluate(name => Values.GetValueOrDefault(name));
        }

        [Theory]
        [InlineData("qty * cycle_secs / 60", 1000.0)]
        [InlineData("1 + 2 * 3", 7.0)]
        [InlineData("(1 + 2) * 3", 9.0)]
        [InlineData("-qty + 10", -490.0)]
        [InlineData("ceil(7 / 2)", 4.0)]
        [InlineData("floor(7 / 2)", 3.0)]
        [InlineData("max(1, tooling.size, 2)", 3.0)]
        [InlineData("min(qty, 20)", 20.0)]
        [InlineData("if(qty > 100, 30, 15)", 30.0)]
        [InlineData("isnull(missing, 5)", 5.0)]
        [InlineData("10 % 4", 2.0)]
        public void Numbers(string text, double expected)
        {
            Assert.Equal(expected, Formula.ToNumber(Run(text)));
        }

        [Theory]
        [InlineData("item.compound = 'SIL-50'", true)]
        [InlineData("item.compound = 'sil-50'", true)]
        [InlineData("item.compound != 'SIL-50'", false)]
        [InlineData("item.compound <> 'NBR-70'", true)]
        [InlineData("item.cell = resource.cell", true)]
        [InlineData("tooling.size <= resource.max_tool_size", true)]
        [InlineData("tooling.size > resource.max_tool_size", false)]
        [InlineData("qty > 100 and item.compound = 'SIL-50'", true)]
        [InlineData("qty > 1000 or item.compound = 'X'", false)]
        [InlineData("not (qty > 1000)", true)]
        [InlineData("missing > 3", false)]
        [InlineData("missing = null", true)]
        public void Conditions(string text, bool expected)
        {
            Assert.Equal(expected, Formula.ToBool(Run(text)));
        }

        [Fact]
        public void Text_Joins()
        {
            Assert.Equal("SIL-50/A", Run("item.compound + '/' + resource.cell"));
        }
        [Fact]
        public void Names_AreListed()
        {
            Assert.Equal(new[] { "qty", "cycle_secs" }, Formula.Parse("qty * cycle_secs / 60").GetNames());
            Assert.Equal(new[] { "Platen Size" }, Formula.Parse("[Platen Size] * 2").GetNames());
        }

        [Theory]
        [InlineData("")]
        [InlineData("qty *")]
        [InlineData("(qty")]
        [InlineData("unknown(1)")]
        [InlineData("'open")]
        [InlineData("1 2")]
        [InlineData("drop table x")]
        public void Bad_Expressions_AreRejected(string text)
        {
            Assert.False(Formula.TryParse(text, out _, out string error));
            Assert.NotEmpty(error);
        }
    }
}
