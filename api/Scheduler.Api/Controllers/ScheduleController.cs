using Microsoft.AspNetCore.Mvc;
using Scheduler.Api.Services;
using System.Text.Json;

namespace Scheduler.Api.Controllers
{
    // Solving and the plan: runs (solve, what-if, emergency), publishing or discarding them, and the locks on the published plan
    [Route("api/profiles/{profileId}/schedule")]
    [ApiController]
    [ProfileAccess(Access.ViewSchedule, Access.Solve)]
    public class ScheduleController : BaseController
    {
        private readonly SolveService _solver;

        public ScheduleController(IConfiguration configuration, SolveService solver) : base(configuration)
        {
            _solver = solver;
        }
        public class LockDetails
        {
            // Window or Item
            public string Kind { get; set; } = "Window";
            public DateTime? LockedUntil { get; set; }
            public string? ResourceKey { get; set; }
            public string? ItemKey { get; set; }
            public string? Reason { get; set; }
        }

        // Starts a run, it carries on in the background, follow it with GET runs/{runId}
        [HttpPost("solve")]
        public IActionResult Solve(string profileId, [FromBody] SolveRequest request)
        {
            try
            {
                string runId = _solver.Start(profileId, request);
                return RunSP("GetSolveRun", profileId, runId);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }
        [HttpGet("runs")]
        public IActionResult GetRuns(string profileId)
        {
            return RunSP("GetSolveRuns", profileId, 50);
        }
        [HttpGet("runs/{runId}")]
        public IActionResult GetRun(string profileId, string runId)
        {
            return RunSP("GetSolveRun", profileId, runId);
        }
        // The published plan, {} when there isn't one yet
        [HttpGet("plan")]
        public IActionResult GetPlan(string profileId)
        {
            return RunSP("GetSolveRun", profileId, null);
        }
        // Asks a running solve to stop, it keeps the best plan it found
        [HttpPost("runs/{runId}/stop")]
        public IActionResult Stop(string profileId, string runId)
        {
            _solver.Stop(runId);
            return RunSP("GetSolveRun", profileId, runId);
        }
        // Accept: the run becomes the plan
        [HttpPost("runs/{runId}/publish")]
        public IActionResult Publish(string profileId, string runId)
        {
            return RunSP("PublishSolveRun", profileId, runId);
        }
        // Abort: the run is thrown away, the plan stays as it was
        [HttpPost("runs/{runId}/discard")]
        public IActionResult Discard(string profileId, string runId)
        {
            return RunSP("DiscardSolveRun", profileId, runId);
        }

        [HttpGet("locks")]
        public IActionResult GetLocks(string profileId)
        {
            return RunSP("GetPlanLocks", profileId);
        }
        [HttpPost("locks")]
        public IActionResult AddLock(string profileId, [FromBody] LockDetails planLock)
        {
            return RunSP("SavePlanLock", profileId, planLock.Kind, planLock.LockedUntil, planLock.ResourceKey, planLock.ItemKey, planLock.Reason);
        }
        [HttpDelete("locks/{lockId}")]
        public IActionResult DeleteLock(string profileId, string lockId)
        {
            return RunSP("DeletePlanLock", profileId, lockId);
        }

        // The item attribute names, for colouring and labelling the Gantt, without the rest of the model a solver can't see
        [HttpGet("attributes")]
        public IActionResult GetAttributes(string profileId)
        {
            try
            {
                List<string> names = new List<string>();
                JsonElement entities = SchedulerDb.RunElement(_dbConnSettings, "GetEntities", profileId);
                foreach (JsonElement entity in entities.EnumerateArray())
                    if (entity.GetProperty("kind").GetString() == "Item")
                    {
                        // the first item entity, the one that's scheduled
                        if (entity.TryGetProperty("attributes", out JsonElement attributes) && attributes.ValueKind == JsonValueKind.Array)
                            foreach (JsonElement attribute in attributes.EnumerateArray())
                                names.Add(attribute.GetProperty("name").GetString() ?? "");
                        break;
                    }
                return Ok(names);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        // The profile's solve settings, { horizonDays, timeLimitSeconds, allowUnscheduled, lockHours, colourBy, labelBy, horizonStart }
        [HttpPut("settings")]
        public IActionResult SaveSettings(string profileId, [FromBody] JsonElement settings)
        {
            return RunSP("SaveProfileSettings", profileId, settings.ValueKind == JsonValueKind.Object ? settings.GetRawText() : "{}");
        }
    }
}
