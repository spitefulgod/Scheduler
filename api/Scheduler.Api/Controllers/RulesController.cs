using Microsoft.AspNetCore.Mvc;
using Scheduler.Api.Services;
using Scheduler.Solver.Rules;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Scheduler.Api.Controllers
{
    // A profile's rules, each a block from the catalogue with who it's for and its parameters, and the check that runs them without solving
    [Route("api/profiles/{profileId}")]
    [ApiController]
    [ProfileAccess(Access.ReadModel, Access.EditModel)]
    public class RulesController : BaseController
    {
        private readonly ISourceSecrets _secrets;

        public RulesController(IConfiguration configuration, ISourceSecrets secrets) : base(configuration)
        {
            _secrets = secrets;
        }
        public class RuleDetails
        {
            public string Name { get; set; } = "";
            public string RuleTypeId { get; set; } = "";
            public bool Enabled { get; set; } = true;
            public string Strength { get; set; } = "Hard";
            public int Weight { get; set; }
            public JsonElement Blocks { get; set; }
            public int? SortOrder { get; set; }
        }
        public class ValueScaleDetails
        {
            public string Name { get; set; } = "";
            public JsonElement Values { get; set; }
        }

        [HttpGet("rules")]
        public IActionResult GetRules(string profileId)
        {
            return RunSP("GetRules", profileId);
        }
        [HttpPost("rules")]
        public IActionResult AddRule(string profileId, [FromBody] RuleDetails rule)
        {
            return Save(profileId, null, rule);
        }
        [HttpPut("rules/{ruleId}")]
        public IActionResult SaveRule(string profileId, string ruleId, [FromBody] RuleDetails rule)
        {
            return Save(profileId, ruleId, rule);
        }
        [HttpDelete("rules/{ruleId}")]
        public IActionResult DeleteRule(string profileId, string ruleId)
        {
            return RunSP("DeleteRule", profileId, ruleId);
        }

        // Reads the data and runs the rule engine without solving: what it found wrong, and how many items each rule touches
        [HttpPost("rules/check")]
        [ProfileAccess(Access.ReadModel)]
        public async Task<IActionResult> Check(string profileId)
        {
            try
            {
                ProfileLoader loader = new ProfileLoader(_dbConnSettings, _secrets);
                ProfileModel model = await loader.Load(profileId);
                RunContext context = new RunContext { HorizonStart = DateTime.Now, HorizonDays = SolveService.GetInt(loader.Settings, "horizonDays") ?? 28 };
                EngineResult engine = RuleEngine.Build(model, context);
                JsonArray diagnostics = new JsonArray();
                foreach (string warning in loader.Warnings)
                    diagnostics.Add(new JsonObject { ["level"] = "warning", ["message"] = warning });
                foreach (Diagnostic diagnostic in engine.Diagnostics)
                    diagnostics.Add(new JsonObject { ["level"] = diagnostic.Level, ["ruleId"] = diagnostic.RuleId, ["ruleName"] = diagnostic.RuleName, ["itemKey"] = diagnostic.ItemKey, ["message"] = diagnostic.Message });
                foreach (Solver.WorkItem item in engine.Problem.Items.Where(x => x.Modes.Count == 0 && x.Quantity > 0).Take(50))
                    diagnostics.Add(new JsonObject { ["level"] = "warning", ["itemKey"] = item.Key, ["message"] = $"{item.Key} can't be planned: {item.NoModeReason}" });
                JsonObject rules = new JsonObject();
                foreach (KeyValuePair<string, RuleStat> stat in engine.RuleStats)
                    rules[stat.Key] = new JsonObject { ["items"] = stat.Value.Items, ["modesRemoved"] = stat.Value.ModesRemoved };
                JsonObject result = new JsonObject
                {
                    ["itemEntity"] = engine.ItemEntity,
                    ["resourceEntity"] = engine.ResourceEntity,
                    ["toolingEntity"] = engine.ToolingEntity,
                    ["items"] = engine.Problem.Items.Count,
                    ["resources"] = engine.Problem.Resources.Count,
                    ["pools"] = engine.Problem.Pools.Count,
                    ["modes"] = engine.Problem.Items.Sum(x => x.Modes.Count),
                    ["noMode"] = engine.Problem.Items.Count(x => x.Modes.Count == 0 && x.Quantity > 0),
                    ["separations"] = engine.Problem.Separations.Count,
                    ["precedences"] = engine.Problem.Precedences.Count,
                    ["diagnostics"] = diagnostics,
                    ["rules"] = rules
                };
                return OkJson(result.ToJsonString());
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        // The profile's own value scales, the built in ones can't be changed
        [HttpPost("value-scales")]
        public IActionResult AddValueScale(string profileId, [FromBody] ValueScaleDetails scale)
        {
            return RunSP("SaveValueScale", profileId, null, scale.Name, scale.Values.ValueKind == JsonValueKind.Array ? scale.Values.GetRawText() : "[]");
        }
        [HttpPut("value-scales/{valueScaleId}")]
        public IActionResult SaveValueScale(string profileId, string valueScaleId, [FromBody] ValueScaleDetails scale)
        {
            return RunSP("SaveValueScale", profileId, valueScaleId, scale.Name, scale.Values.ValueKind == JsonValueKind.Array ? scale.Values.GetRawText() : "[]");
        }
        [HttpDelete("value-scales/{valueScaleId}")]
        public IActionResult DeleteValueScale(string profileId, string valueScaleId)
        {
            return RunSP("DeleteValueScale", profileId, valueScaleId);
        }

        private IActionResult Save(string profileId, string? ruleId, RuleDetails rule)
        {
            return RunSP("SaveRule", profileId, ruleId, rule.Name, rule.RuleTypeId, rule.Enabled, rule.Strength, rule.Weight,
                rule.Blocks.ValueKind == JsonValueKind.Object ? rule.Blocks.GetRawText() : "{}", rule.SortOrder);
        }
    }
}
