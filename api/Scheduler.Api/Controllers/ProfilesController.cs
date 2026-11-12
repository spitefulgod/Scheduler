using Microsoft.AspNetCore.Mvc;

namespace Scheduler.Api.Controllers
{
    // Profiles, each one is a scheduler set up for one process, e.g. Press Scheduling
    [Route("api/profiles")]
    [ApiController]
    public class ProfilesController : BaseController
    {
        public ProfilesController(IConfiguration configuration) : base(configuration)
        {
        }
        // What the client sends to add or change one
        public class ProfileDetails
        {
            public string Name { get; set; } = "";
            public string? Description { get; set; }
            public int TimeUnitMinutes { get; set; } = 15;
        }

        [HttpGet]
        public IActionResult GetProfiles()
        {
            return RunSP("GetProfiles");
        }
        [HttpGet("{profileId}")]
        public IActionResult GetProfile(string profileId)
        {
            return RunSP("GetProfile", profileId);
        }
        [HttpPost]
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
        [HttpGet("{profileId}/navigator")]
        public IActionResult GetNavigator(string profileId)
        {
            return RunSP("GetNavigator", profileId);
        }
    }
}
