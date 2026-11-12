using System.Globalization;

namespace Scheduler.Solver.Rules
{
    // Turns calendars into the times a resource can't be used across the horizon
    public static class CalendarBuilder
    {
        // The down periods of a calendar between from and to, merged and in order
        public static List<(DateTime start, DateTime end, string reason)> GetDownTime(ProfileModel model, CalendarModel calendar, DateTime from, DateTime to)
        {
            List<CalendarShiftModel> shifts = GetShifts(model, calendar);
            List<CalendarExceptionModel> exceptions = GetExceptions(model, calendar);

            // working time from the weekly pattern, no shifts at all means it's always working
            List<(DateTime start, DateTime end)> working = new List<(DateTime, DateTime)>();
            if (shifts.Count == 0)
                working.Add((from, to));
            else
                for (DateTime day = from.Date.AddDays(-1); day < to; day = day.AddDays(1))
                    foreach (CalendarShiftModel shift in shifts.Where(x => x.DayOfWeek == (int)day.DayOfWeek))
                    {
                        DateTime start = day + ParseTime(shift.StartTime);
                        DateTime end = day + ParseTime(shift.EndTime);
                        if (end <= start)
                            end = end.AddDays(1);
                        working.Add((start, end));
                    }
            foreach (CalendarExceptionModel exception in exceptions.Where(x => string.Equals(x.Kind, "Available", StringComparison.OrdinalIgnoreCase)))
                working.Add((exception.StartTime, exception.EndTime));
            working = Merge(working);

            // the gaps between working times are down, then the down exceptions on top
            List<(DateTime start, DateTime end, string reason)> down = new List<(DateTime, DateTime, string)>();
            DateTime at = from;
            foreach ((DateTime start, DateTime end) in working)
            {
                if (end <= from)
                    continue;
                if (start > at)
                    down.Add((at, start < to ? start : to, "Not a working time"));
                if (end > at)
                    at = end;
                if (at >= to)
                    break;
            }
            if (at < to)
                down.Add((at, to, "Not a working time"));
            foreach (CalendarExceptionModel exception in exceptions.Where(x => !string.Equals(x.Kind, "Available", StringComparison.OrdinalIgnoreCase)))
                if (exception.EndTime > from && exception.StartTime < to)
                    down.Add((exception.StartTime < from ? from : exception.StartTime, exception.EndTime > to ? to : exception.EndTime, exception.Reason ?? "Down"));
            return down.Where(x => x.end > x.start).OrderBy(x => x.start).ToList();
        }

        // The resource's own calendar, otherwise the one calendar with no parent and no resource, e.g. Site
        public static CalendarModel? FindFor(ProfileModel model, string? entityName, string key)
        {
            CalendarModel? own = model.Calendars.FirstOrDefault(x => string.Equals(x.ResourceKey, key, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrEmpty(x.ResourceEntity) || string.Equals(x.ResourceEntity, entityName, StringComparison.OrdinalIgnoreCase)));
            if (own != null)
                return own;
            List<CalendarModel> roots = model.Calendars.Where(x => string.IsNullOrEmpty(x.ParentId) && string.IsNullOrEmpty(x.ResourceKey)).ToList();
            return roots.Count == 1 ? roots[0] : null;
        }

        private static List<CalendarShiftModel> GetShifts(ProfileModel model, CalendarModel calendar)
        {
            HashSet<string> seen = new HashSet<string>();
            CalendarModel? at = calendar;
            while (at != null && seen.Add(at.Id))
            {
                if (at.Shifts.Count > 0)
                    return at.Shifts;
                at = model.Calendars.FirstOrDefault(x => x.Id == at.ParentId);
            }
            return new List<CalendarShiftModel>();
        }
        private static List<CalendarExceptionModel> GetExceptions(ProfileModel model, CalendarModel calendar)
        {
            List<CalendarExceptionModel> exceptions = new List<CalendarExceptionModel>();
            HashSet<string> seen = new HashSet<string>();
            CalendarModel? at = calendar;
            while (at != null && seen.Add(at.Id))
            {
                exceptions.AddRange(at.Exceptions);
                at = model.Calendars.FirstOrDefault(x => x.Id == at.ParentId);
            }
            return exceptions;
        }
        private static TimeSpan ParseTime(string text)
        {
            if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out TimeSpan time))
                return time;
            return TimeSpan.Zero;
        }
        private static List<(DateTime start, DateTime end)> Merge(List<(DateTime start, DateTime end)> ranges)
        {
            List<(DateTime start, DateTime end)> merged = new List<(DateTime, DateTime)>();
            foreach ((DateTime start, DateTime end) in ranges.Where(x => x.end > x.start).OrderBy(x => x.start))
                if (merged.Count > 0 && start <= merged[^1].end)
                    merged[^1] = (merged[^1].start, end > merged[^1].end ? end : merged[^1].end);
                else
                    merged.Add((start, end));
            return merged;
        }
    }
}
