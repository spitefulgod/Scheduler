using Microsoft.AspNetCore.Mvc;
using Scheduler.Api.Services;
using System.Text.Json;

namespace Scheduler.Api.Controllers
{
    // Working time: weekly shifts and exceptions, a resource's calendar takes on its parent's, e.g. the site's shutdown
    [Route("api/profiles/{profileId}/calendars")]
    [ApiController]
    [ProfileAccess(Access.ReadModel, Access.EditModel)]
    public class CalendarsController : BaseController
    {
        public CalendarsController(IConfiguration configuration) : base(configuration)
        {
        }
        public class CalendarDetails
        {
            public string Name { get; set; } = "";
            public string? ParentCalendarId { get; set; }
            public string? ResourceEntityTypeId { get; set; }
            public string? ResourceKey { get; set; }
            public JsonElement Shifts { get; set; }
            public JsonElement Exceptions { get; set; }
        }

        [HttpGet]
        public IActionResult GetCalendars(string profileId)
        {
            return RunSP("GetCalendars", profileId);
        }
        [HttpPost]
        public IActionResult AddCalendar(string profileId, [FromBody] CalendarDetails calendar)
        {
            return Save(profileId, null, calendar);
        }
        [HttpPut("{calendarId}")]
        public IActionResult SaveCalendar(string profileId, string calendarId, [FromBody] CalendarDetails calendar)
        {
            return Save(profileId, calendarId, calendar);
        }
        [HttpDelete("{calendarId}")]
        public IActionResult DeleteCalendar(string profileId, string calendarId)
        {
            return RunSP("DeleteCalendar", profileId, calendarId);
        }

        private IActionResult Save(string profileId, string? calendarId, CalendarDetails c)
        {
            return RunSP("SaveCalendar", profileId, calendarId, c.Name, c.ParentCalendarId, c.ResourceEntityTypeId, c.ResourceKey,
                c.Shifts.ValueKind == JsonValueKind.Array ? c.Shifts.GetRawText() : "[]", c.Exceptions.ValueKind == JsonValueKind.Array ? c.Exceptions.GetRawText() : "[]");
        }
    }
}
