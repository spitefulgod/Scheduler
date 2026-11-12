using Microsoft.AspNetCore.Mvc;
using Scheduler.Api.Services;
using Scheduler.Solver.Rules;
using System.Text.Json;

namespace Scheduler.Api.Controllers
{
    // A profile's entity types, e.g. Job, Press, Tool, and their attributes mapped onto source columns with a role each
    [Route("api/profiles/{profileId}/entities")]
    [ApiController]
    [ProfileAccess(Access.ReadModel, Access.EditModel)]
    public class EntitiesController : BaseController
    {
        private readonly ISourceSecrets _secrets;

        public EntitiesController(IConfiguration configuration, ISourceSecrets secrets) : base(configuration)
        {
            _secrets = secrets;
        }
        // What the client sends to add or change one
        public class EntityDetails
        {
            public string Name { get; set; } = "";
            public string Kind { get; set; } = "Item";
            public string? Description { get; set; }
            public string? SourceId { get; set; }
            public string? DurationFormula { get; set; }
        }

        [HttpGet]
        public IActionResult GetEntities(string profileId)
        {
            return RunSP("GetEntities", profileId);
        }
        [HttpGet("{entityTypeId}")]
        public IActionResult GetEntity(string profileId, string entityTypeId)
        {
            return RunSP("GetEntity", profileId, entityTypeId);
        }
        [HttpPost]
        public IActionResult AddEntity(string profileId, [FromBody] EntityDetails entity)
        {
            return RunSP("SaveEntity", profileId, null, entity.Name, entity.Kind, entity.Description, entity.SourceId, entity.DurationFormula);
        }
        [HttpPut("{entityTypeId}")]
        public IActionResult SaveEntity(string profileId, string entityTypeId, [FromBody] EntityDetails entity)
        {
            return RunSP("SaveEntity", profileId, entityTypeId, entity.Name, entity.Kind, entity.Description, entity.SourceId, entity.DurationFormula);
        }
        // The whole list of attributes, [{ id, name, dataType, role, sourceColumn, referencesEntityTypeId, sortOrder }]
        [HttpPut("{entityTypeId}/attributes")]
        public IActionResult SaveAttributes(string profileId, string entityTypeId, [FromBody] JsonElement attributes)
        {
            return RunSP("SaveAttributes", profileId, entityTypeId, attributes.GetRawText());
        }
        [HttpDelete("{entityTypeId}")]
        public IActionResult DeleteEntity(string profileId, string entityTypeId)
        {
            return RunSP("DeleteEntity", profileId, entityTypeId);
        }
        // The entity's rows as the rule engine will see them, read through its mapping, { attributes, rows, warnings }
        [HttpGet("{entityTypeId}/rows")]
        public async Task<IActionResult> GetRows(string profileId, string entityTypeId, [FromQuery] int top = 200)
        {
            try
            {
                ProfileLoader loader = new ProfileLoader(_dbConnSettings, _secrets);
                ProfileModel model = await loader.Load(profileId);
                EntityModel entity = model.Entities.FirstOrDefault(x => x.Id == entityTypeId) ?? throw new Exception("Entity not found");
                object result = new
                {
                    attributes = entity.Attributes.Select(x => x.Name),
                    total = entity.Rows.Count,
                    rows = entity.Rows.Take(Math.Clamp(top, 1, 1000)).Select(x => entity.Attributes.Select(a => x.GetValueOrDefault(a.Name))),
                    warnings = loader.Warnings
                };
                return OkJson(JsonSerializer.Serialize(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }
    }
}
