using Microsoft.AspNetCore.Mvc;

namespace Scheduler.Api.Controllers
{
    // The built in lists, rule blocks and value scales, and the status bar's details
    [Route("api")]
    [ApiController]
    public class CatalogueController : BaseController
    {
        public CatalogueController(IConfiguration configuration) : base(configuration)
        {
        }

        [HttpGet("rule-types")]
        public IActionResult GetRuleTypes()
        {
            return RunSP("GetRuleTypes");
        }
        // The built in scales and the profile's own when one is given
        [HttpGet("value-scales")]
        public IActionResult GetValueScales([FromQuery] string? profileId = null)
        {
            return RunSP("GetValueScales", profileId);
        }
        // Which server and database the API is using
        [HttpGet("status")]
        public IActionResult GetStatus()
        {
            return RunSP("GetStatus");
        }
    }
}
