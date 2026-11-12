using Microsoft.AspNetCore.Mvc;
using Scheduler.Api.Services;
using Scheduler.Solver.Rules;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Scheduler.Api.Controllers
{
    // A x B matrices and lists, e.g. which compounds suit which press, edited as a pivot grid or a list
    // values can be read live from a source, typed in, or both, a typed in value wins
    [Route("api/profiles/{profileId}/relationships")]
    [ApiController]
    public class RelationshipsController : BaseController
    {
        private readonly ISourceSecrets _secrets;

        public RelationshipsController(IConfiguration configuration, ISourceSecrets secrets) : base(configuration)
        {
            _secrets = secrets;
        }
        public class RelationshipDetails
        {
            public string Name { get; set; } = "";
            public string EntityTypeAId { get; set; } = "";
            public string EntityTypeBId { get; set; } = "";
            public string ValueScaleId { get; set; } = "BOOLEAN";
            public string? DefaultValue { get; set; }
            public bool Symmetric { get; set; }
            public string Editor { get; set; } = "Matrix";
            public string? Drives { get; set; }
            public string? SourceId { get; set; }
            public string? SourceAColumn { get; set; }
            public string? SourceBColumn { get; set; }
            public string? SourceValueColumn { get; set; }
        }
        // Typed in values to set, a blank value clears the pair back to the source or default
        public class ValuesDetails
        {
            public JsonElement Values { get; set; }
            public bool Replace { get; set; }
        }

        [HttpGet]
        public IActionResult GetRelationships(string profileId)
        {
            return RunSP("GetRelationships", profileId);
        }
        [HttpGet("{relationshipId}")]
        public IActionResult GetRelationship(string profileId, string relationshipId)
        {
            return RunSP("GetRelationship", profileId, relationshipId);
        }
        // Everything the matrix editor shows: the rows of A and B, and each pair's value and where it came from
        // { relationship, a : [{ key, label }], b : [...], values : [{ a, b, value, typed }], warnings }
        [HttpGet("{relationshipId}/grid")]
        public async Task<IActionResult> GetGrid(string profileId, string relationshipId)
        {
            try
            {
                string relationshipJson = PullOne("GetRelationship", profileId, relationshipId);
                JsonObject relationship = JsonNode.Parse(relationshipJson)!.AsObject();
                ProfileLoader loader = new ProfileLoader(_dbConnSettings, _secrets);
                ProfileModel model = await loader.Load(profileId);
                RelationshipModel merged = model.Relationships.First(x => x.Id == relationshipId);
                HashSet<string> typed = new HashSet<string>(((JsonArray)relationship["values"]!).Select(x => x!["aKey"]!.GetValue<string>() + "\u0001" + x["bKey"]!.GetValue<string>()),
                    StringComparer.OrdinalIgnoreCase);
                relationship.Remove("values");
                JsonObject grid = new JsonObject
                {
                    ["relationship"] = relationship,
                    ["a"] = GetKeys(model, merged.EntityA),
                    ["b"] = GetKeys(model, merged.EntityB),
                    ["values"] = new JsonArray(merged.Values.Select(x => (JsonNode)new JsonObject
                    {
                        ["a"] = x.A,
                        ["b"] = x.B,
                        ["value"] = x.Value,
                        ["typed"] = typed.Contains(x.A + "\u0001" + x.B)
                    }).ToArray()),
                    ["warnings"] = new JsonArray(loader.Warnings.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray())
                };
                return OkJson(grid.ToJsonString());
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }
        [HttpPost]
        public IActionResult AddRelationship(string profileId, [FromBody] RelationshipDetails relationship)
        {
            return Save(profileId, null, relationship);
        }
        [HttpPut("{relationshipId}")]
        public IActionResult SaveRelationship(string profileId, string relationshipId, [FromBody] RelationshipDetails relationship)
        {
            return Save(profileId, relationshipId, relationship);
        }
        [HttpPut("{relationshipId}/values")]
        public IActionResult SaveValues(string profileId, string relationshipId, [FromBody] ValuesDetails values)
        {
            return RunSP("SaveRelationshipValues", profileId, relationshipId, values.Values.ValueKind == JsonValueKind.Array ? values.Values.GetRawText() : "[]", values.Replace);
        }
        // Copies the source's values in as typed in values, so they can be changed without the source
        [HttpPost("{relationshipId}/import")]
        public async Task<IActionResult> Import(string profileId, string relationshipId)
        {
            try
            {
                ProfileLoader loader = new ProfileLoader(_dbConnSettings, _secrets);
                ProfileModel model = await loader.Load(profileId);
                RelationshipModel merged = model.Relationships.First(x => x.Id == relationshipId);
                if (loader.Warnings.Count > 0)
                    throw new Exception(string.Join("; ", loader.Warnings));
                JsonArray values = new JsonArray(merged.Values.Select(x => (JsonNode)new JsonObject { ["aKey"] = x.A, ["bKey"] = x.B, ["value"] = x.Value }).ToArray());
                return RunSP("SaveRelationshipValues", profileId, relationshipId, values.ToJsonString(), true);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }
        [HttpDelete("{relationshipId}")]
        public IActionResult DeleteRelationship(string profileId, string relationshipId)
        {
            return RunSP("DeleteRelationship", profileId, relationshipId);
        }

        private IActionResult Save(string profileId, string? relationshipId, RelationshipDetails r)
        {
            return RunSP("SaveRelationship", profileId, relationshipId, r.Name, r.EntityTypeAId, r.EntityTypeBId, r.ValueScaleId, r.DefaultValue, r.Symmetric, r.Editor,
                r.Drives, r.SourceId, r.SourceAColumn, r.SourceBColumn, r.SourceValueColumn);
        }
        private string PullOne(string procedure, params object?[] args)
        {
            using (SQLConnector conn = SQLConnector.GetConnection(_dbConnSettings))
                return PullSPResult(conn, procedure, args) ?? "{}";
        }
        private static JsonArray GetKeys(ProfileModel model, string entityName)
        {
            EntityModel? entity = model.GetEntity(entityName);
            JsonArray keys = new JsonArray();
            if (entity == null)
                return keys;
            AttributeModel? id = entity.GetByRole("Id");
            AttributeModel? name = entity.GetByRole("Name");
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object?> row in entity.Rows)
            {
                string? key = id == null ? null : Solver.Formula.ToText(row.GetValueOrDefault(id.Name))?.Trim();
                if (string.IsNullOrEmpty(key) || !seen.Add(key))
                    continue;
                keys.Add(new JsonObject { ["key"] = key, ["label"] = name == null ? key : Solver.Formula.ToText(row.GetValueOrDefault(name.Name)) ?? key });
            }
            return keys;
        }
    }
}
