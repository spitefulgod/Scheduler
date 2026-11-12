using Microsoft.AspNetCore.Mvc;
using Scheduler.Api.Services;
using System.Text.Json.Nodes;

namespace Scheduler.Api.Controllers
{
    // Profiles, each one is a scheduler set up for one process, e.g. Press Scheduling
    [Route("api/profiles")]
    [ApiController]
    [ProfileAccess(Access.ViewSchedule, Access.EditModel)]
    public class ProfilesController : BaseController
    {
        private readonly UserAccessService _access;

        public ProfilesController(IConfiguration configuration, UserAccessService access) : base(configuration)
        {
            _access = access;
        }
        // What the client sends to add or change one
        public class ProfileDetails
        {
            public string Name { get; set; } = "";
            public string? Description { get; set; }
            public int TimeUnitMinutes { get; set; } = 15;
        }

        // The profiles the user has a role in, every one for a site owner
        [HttpGet]
        [ProfileAccess(Access.SignedIn)]
        public IActionResult GetProfiles()
        {
            try
            {
                UserAccess access = _access.Get(HttpContext);
                JsonArray profiles = JsonNode.Parse(SchedulerDb.Run(_dbConnSettings, "GetProfiles") ?? "[]")!.AsArray();
                JsonArray allowed = new JsonArray();
                foreach (JsonNode? profile in profiles.ToList())
                    if (profile != null && access.Can(Access.ViewSchedule, profile["id"]!.GetValue<string>()))
                    {
                        profiles.Remove(profile);
                        allowed.Add(profile);
                    }
                return OkJson(allowed.ToJsonString());
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }
        [HttpGet("{profileId}")]
        public IActionResult GetProfile(string profileId)
        {
            return RunSP("GetProfile", profileId);
        }
        // Only a site owner adds profiles, they then give out its roles
        [HttpPost]
        [ProfileAccess(Access.Site)]
        public IActionResult AddProfile([FromBody] ProfileDetails profile)
        {
            return RunSP("SaveProfile", null, profile.Name, profile.Description, profile.TimeUnitMinutes);
        }
        [HttpPut("{profileId}")]
        public IActionResult SaveProfile(string profileId, [FromBody] ProfileDetails profile)
        {
            return RunSP("SaveProfile", profileId, profile.Name, profile.Description, profile.TimeUnitMinutes);
        }
        // The navigator tree, connections, sources, entities, relationships, calendars, rules and runs
        // a solver only gets the runs, the rest is the model they can't see
        [HttpGet("{profileId}/navigator")]
        public IActionResult GetNavigator(string profileId)
        {
            UserAccess access = _access.Get(HttpContext);
            if (access.Can(Access.ReadModel, profileId))
                return RunSP("GetNavigator", profileId);
            try
            {
                JsonArray folders = JsonNode.Parse(SchedulerDb.Run(_dbConnSettings, "GetNavigator", profileId) ?? "[]")!.AsArray();
                JsonArray allowed = new JsonArray();
                foreach (JsonNode? folder in folders.ToList())
                    if (folder != null && folder["id"]?.GetValue<string>() == "runs")
                    {
                        folders.Remove(folder);
                        allowed.Add(folder);
                    }
                return OkJson(allowed.ToJsonString());
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }
    }
}
