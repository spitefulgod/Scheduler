using Scheduler.Solver;
using Scheduler.Solver.Rules;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Scheduler.Api.Services
{
    // One row of a plan, a segment of an item on a resource, as kept in ScheduleAssignments
    public class PlanRow
    {
        public string ItemKey { get; set; } = "";
        public string? ItemLabel { get; set; }
        public int Segment { get; set; }
        public string ResourceKey { get; set; } = "";
        public string? PoolKey { get; set; }
        public int ToolCount { get; set; } = 1;
        public long Cycles { get; set; }
        public long Quantity { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public int SetupMinutes { get; set; }
        public DateTime? DueTime { get; set; }
        public int LateMinutes { get; set; }
        public int Cost { get; set; }
        public List<string>? CostReasons { get; set; }
        public JsonArray? Inspections { get; set; }
        public Dictionary<string, string?>? Attributes { get; set; }
        public bool Fixed { get; set; }

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        // The assignments of a GetSolveRun result
        public static List<PlanRow> ReadAll(string? runJson)
        {
            using (JsonDocument document = JsonDocument.Parse(string.IsNullOrEmpty(runJson) ? "{}" : runJson))
            {
                if (!document.RootElement.TryGetProperty("assignments", out JsonElement assignments) || assignments.ValueKind != JsonValueKind.Array)
                    return new List<PlanRow>();
                return assignments.Deserialize<List<PlanRow>>(JsonOptions) ?? new List<PlanRow>();
            }
        }
        public static string ToJson(List<PlanRow> rows)
        {
            return JsonSerializer.Serialize(rows, JsonOptions);
        }
    }

    // Turns the published plan and its locks into what a run must keep, and a run's result into plan rows and changes
    public static class PlanBuilder
    {
        // Locked work: anything already finished or started, a lock window (all resources or one), an item lock,
        // and the profile's lockHours from the start of the horizon, less what a what-if or an emergency releases
        public static RunContext BuildContext(SolveRequest request, List<PlanRow> published, JsonElement locks, JsonElement settings)
        {
            DateTime start = request.HorizonStart ?? DateTime.Now;
            RunContext context = new RunContext
            {
                HorizonStart = start,
                HorizonDays = request.HorizonDays ?? 28,
                AllowUnscheduled = request.AllowUnscheduled ?? true,
                Pins = request.Pins ?? new List<ItemPin>(),
                Downs = request.Downs ?? new List<ResourceDown>()
            };
            List<(string? resourceKey, DateTime until)> windows = new List<(string?, DateTime)>();
            HashSet<string> lockedItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (locks.ValueKind == JsonValueKind.Array)
                foreach (JsonElement planLock in locks.EnumerateArray())
                {
                    string kind = planLock.GetProperty("kind").GetString() ?? "";
                    if (kind == "Window" && planLock.TryGetProperty("lockedUntil", out JsonElement until) && until.ValueKind == JsonValueKind.String)
                        windows.Add((planLock.TryGetProperty("resourceKey", out JsonElement resource) && resource.ValueKind == JsonValueKind.String ? resource.GetString() : null,
                            DateTime.Parse(until.GetString()!, CultureInfo.InvariantCulture)));
                    else if (kind == "Item" && planLock.TryGetProperty("itemKey", out JsonElement item) && item.ValueKind == JsonValueKind.String)
                        lockedItems.Add(item.GetString()!);
                }
            int lockHours = SolveService.GetInt(settings, "lockHours") ?? 0;
            if (lockHours > 0)
                windows.Add((null, start.AddHours(lockHours)));

            HashSet<string> released = new HashSet<string>(request.Unlock ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (ItemPin pin in context.Pins)
                released.Add(pin.ItemKey);

            foreach (PlanRow row in published)
            {
                bool past = row.EndTime <= start;
                bool started = row.StartTime < start;
                bool locked = past || started || lockedItems.Contains(row.ItemKey)
                    || windows.Any(x => (string.IsNullOrEmpty(x.resourceKey) || string.Equals(x.resourceKey, row.ResourceKey, StringComparison.OrdinalIgnoreCase)) && row.StartTime < x.until);

                // a released item keeps only what's already been done
                if (released.Contains(row.ItemKey) && !past)
                {
                    if (started)
                        context.Locked.Add(Truncate(row, start));
                    continue;
                }
                // a breakdown releases the work it hits, what ran before it stays, the rest goes to the alternatives
                ResourceDown? down = context.Downs.FirstOrDefault(x => string.Equals(x.ResourceKey, row.ResourceKey, StringComparison.OrdinalIgnoreCase)
                    && row.StartTime < x.To && x.From < row.EndTime);
                if (down != null && !past)
                {
                    DateTime keepUntil = down.From > start ? down.From : start;
                    if (row.StartTime < keepUntil && locked)
                        context.Locked.Add(Truncate(row, keepUntil));
                    if (request.Alternatives != null && request.Alternatives.Count > 0)
                        context.RestrictTo[row.ItemKey] = request.Alternatives.ToList();
                    continue;
                }
                if (locked)
                    context.Locked.Add(new LockedBooking
                    {
                        ItemKey = row.ItemKey,
                        ResourceKey = row.ResourceKey,
                        PoolKey = row.PoolKey,
                        Count = row.ToolCount,
                        Start = row.StartTime,
                        End = row.EndTime,
                        Quantity = row.Quantity
                    });
            }

            foreach (IGrouping<string, PlanRow> item in published.Where(x => !x.Fixed).GroupBy(x => x.ItemKey, StringComparer.OrdinalIgnoreCase))
            {
                PlanRow first = item.OrderBy(x => x.StartTime).First();
                context.Baseline[item.Key] = new BaselineTime { ResourceKey = first.ResourceKey, Start = first.StartTime };
            }
            return context;
        }
        // The part of a row before a time, with the share of its quantity made by then
        private static LockedBooking Truncate(PlanRow row, DateTime at)
        {
            double share = row.EndTime > row.StartTime ? Math.Clamp((at - row.StartTime).TotalMinutes / (row.EndTime - row.StartTime).TotalMinutes, 0, 1) : 1;
            return new LockedBooking
            {
                ItemKey = row.ItemKey,
                ResourceKey = row.ResourceKey,
                PoolKey = row.PoolKey,
                Count = row.ToolCount,
                Start = row.StartTime,
                End = at < row.EndTime ? at : row.EndTime,
                Quantity = (long)Math.Floor(row.Quantity * share)
            };
        }

        // The run's plan rows, locked work keeps its own times
        public static List<PlanRow> ToRows(EngineResult engine, RunContext context, ScheduleResult result, List<PlanRow> published)
        {
            SchedulingProblem problem = engine.Problem;
            Dictionary<string, WorkItem> items = problem.Items.ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, PlanRow> publishedByItem = new Dictionary<string, PlanRow>(StringComparer.OrdinalIgnoreCase);
            foreach (PlanRow row in published)
                publishedByItem.TryAdd(row.ItemKey, row);
            List<PlanRow> rows = new List<PlanRow>();
            int fixedIndex = 0;
            foreach (SegmentAssignment assignment in result.Assignments)
            {
                items.TryGetValue(assignment.ItemKey, out WorkItem? item);
                PlanRow? before = publishedByItem.GetValueOrDefault(assignment.ItemKey);
                PlanRow row = new PlanRow
                {
                    ItemKey = assignment.ItemKey,
                    ItemLabel = item?.Label ?? before?.ItemLabel ?? assignment.ItemKey,
                    Segment = assignment.Segment,
                    ResourceKey = assignment.ResourceKey,
                    PoolKey = assignment.PoolKey,
                    ToolCount = assignment.Count,
                    Cycles = assignment.Cycles,
                    Quantity = assignment.Quantity,
                    StartTime = problem.ToTime(assignment.StartBucket),
                    EndTime = problem.ToTime(assignment.EndBucket),
                    SetupMinutes = assignment.SetupMinutes,
                    DueTime = item?.DueBucket.HasValue == true ? problem.ToTime(item.DueBucket!.Value) : before?.DueTime,
                    LateMinutes = assignment.LateBuckets * problem.BucketMinutes,
                    Cost = assignment.Cost,
                    CostReasons = assignment.CostReasons.Count > 0 ? assignment.CostReasons : null,
                    Attributes = item?.Attributes ?? before?.Attributes,
                    Fixed = assignment.Fixed
                };
                if (assignment.Fixed && fixedIndex < context.Locked.Count)
                {
                    LockedBooking locked = context.Locked[fixedIndex++];
                    row.StartTime = locked.Start;
                    row.EndTime = locked.End;
                    row.Quantity = locked.Quantity;
                    row.Segment = -1;
                    row.LateMinutes = row.DueTime.HasValue && locked.End > row.DueTime.Value ? (int)Math.Ceiling((locked.End - row.DueTime.Value).TotalMinutes) : 0;
                    PlanRow? same = published.FirstOrDefault(x => x.ItemKey == locked.ItemKey && x.StartTime == locked.Start && x.ResourceKey == locked.ResourceKey);
                    if (same != null)
                    {
                        row.Cycles = same.Cycles;
                        row.SetupMinutes = same.SetupMinutes;
                        row.Inspections = same.Inspections;
                    }
                }
                else if (assignment.Inspections.Count > 0)
                    row.Inspections = new JsonArray(assignment.Inspections.Select(x => (JsonNode)new JsonObject
                    {
                        ["kind"] = x.Kind,
                        ["at"] = row.StartTime.AddMinutes(x.OffsetMinutes).ToString("s", CultureInfo.InvariantCulture),
                        ["minutes"] = x.Minutes
                    }).ToArray());
                rows.Add(row);
            }
            return rows;
        }

        // The resources, their down times and the combinations they're in, for the Gantt
        public static JsonObject GetDetails(EngineResult engine, RunContext context, JsonElement settings, ScheduleResult? result = null)
        {
            SchedulingProblem problem = engine.Problem;
            JsonArray resources = new JsonArray();
            foreach (WorkResource resource in problem.Resources)
            {
                JsonArray down = new JsonArray();
                foreach (BucketRange range in resource.Unavailable.OrderBy(x => x.Start))
                    down.Add(new JsonObject
                    {
                        ["start"] = problem.ToTime(Math.Max(0, range.Start)).ToString("s", CultureInfo.InvariantCulture),
                        ["end"] = problem.ToTime(Math.Min(problem.HorizonBuckets, range.End)).ToString("s", CultureInfo.InvariantCulture),
                        ["reason"] = range.Reason
                    });
                JsonObject attributes = new JsonObject();
                foreach (KeyValuePair<string, string?> value in resource.Attributes)
                    attributes[value.Key] = value.Value;
                resources.Add(new JsonObject
                {
                    ["key"] = resource.Key,
                    ["label"] = resource.Label,
                    ["attributes"] = attributes,
                    ["capacities"] = new JsonObject(resource.Capacities.Select(x => new KeyValuePair<string, JsonNode?>(x.Key, x.Value))),
                    ["unavailable"] = down,
                    ["configurations"] = new JsonArray((result?.Configurations ?? new List<ConfigurationChoice>())
                        .Where(x => x.ResourceKey == resource.Key)
                        .Select(x => (JsonNode)new JsonObject
                        {
                            ["start"] = problem.ToTime(x.StartBucket).ToString("s", CultureInfo.InvariantCulture),
                            ["end"] = problem.ToTime(x.EndBucket).ToString("s", CultureInfo.InvariantCulture),
                            ["key"] = x.Key,
                            ["label"] = x.Label,
                            ["ruleName"] = problem.RuleNames.GetValueOrDefault(x.RuleId)
                        }).ToArray())
                });
            }
            JsonArray pools = new JsonArray(problem.Pools.Select(x => (JsonNode)new JsonObject { ["key"] = x.Key, ["label"] = x.Label, ["copies"] = x.Copies }).ToArray());
            return new JsonObject
            {
                ["itemEntity"] = engine.ItemEntity,
                ["resourceEntity"] = engine.ResourceEntity,
                ["toolingEntity"] = engine.ToolingEntity,
                ["bucketMinutes"] = problem.BucketMinutes,
                ["horizonStart"] = problem.HorizonStart.ToString("s", CultureInfo.InvariantCulture),
                ["horizonEnd"] = problem.ToTime(problem.HorizonBuckets).ToString("s", CultureInfo.InvariantCulture),
                ["colourBy"] = SolveService.GetText(settings, "colourBy"),
                ["labelBy"] = SolveService.GetText(settings, "labelBy"),
                ["resources"] = resources,
                ["pools"] = pools,
                ["lockedCount"] = context.Locked.Count,
                ["downs"] = new JsonArray(context.Downs.Select(x => (JsonNode)new JsonObject
                {
                    ["resourceKey"] = x.ResourceKey,
                    ["from"] = x.From.ToString("s", CultureInfo.InvariantCulture),
                    ["to"] = x.To.ToString("s", CultureInfo.InvariantCulture),
                    ["reason"] = x.Reason
                }).ToArray())
            };
        }

        public static JsonArray GetUnscheduled(EngineResult engine, ScheduleResult result)
        {
            Dictionary<string, WorkItem> items = engine.Problem.Items.ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
            return new JsonArray(result.Unscheduled.Select(x =>
            {
                items.TryGetValue(x.ItemKey, out WorkItem? item);
                return (JsonNode)new JsonObject
                {
                    ["itemKey"] = x.ItemKey,
                    ["label"] = item?.Label ?? x.ItemKey,
                    ["reason"] = x.Reason,
                    ["quantity"] = item?.Quantity,
                    ["due"] = item?.DueBucket.HasValue == true ? engine.Problem.ToTime(item.DueBucket!.Value).ToString("s", CultureInfo.InvariantCulture) : null
                };
            }).ToArray());
        }

        // How a run differs from the published plan, item by item, with a summary for the accept or discard decision
        public static JsonObject Compare(List<PlanRow> before, List<PlanRow> after, JsonArray unscheduled, int bucketMinutes)
        {
            JsonObject summary = new JsonObject { ["hasBaseline"] = before.Count > 0 };
            JsonArray changes = new JsonArray();
            if (before.Count == 0)
                return new JsonObject { ["summary"] = summary, ["items"] = changes };

            Dictionary<string, List<PlanRow>> was = before.GroupBy(x => x.ItemKey, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.ToList(), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<PlanRow>> now = after.GroupBy(x => x.ItemKey, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.ToList(), StringComparer.OrdinalIgnoreCase);
            int moved = 0, retimed = 0, added = 0, dropped = 0;
            foreach (string key in was.Keys.Union(now.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
            {
                List<PlanRow>? a = was.GetValueOrDefault(key);
                List<PlanRow>? b = now.GetValueOrDefault(key);
                string kind;
                if (a == null)
                {
                    kind = "Added";
                    added++;
                }
                else if (b == null)
                {
                    kind = "Dropped";
                    dropped++;
                }
                else
                {
                    string resourcesBefore = string.Join(", ", a.Select(x => x.ResourceKey).Distinct().OrderBy(x => x));
                    string resourcesAfter = string.Join(", ", b.Select(x => x.ResourceKey).Distinct().OrderBy(x => x));
                    double shift = (b.Min(x => x.StartTime) - a.Min(x => x.StartTime)).TotalMinutes;
                    double endShift = (b.Max(x => x.EndTime) - a.Max(x => x.EndTime)).TotalMinutes;
                    if (resourcesBefore != resourcesAfter)
                    {
                        kind = "Moved";
                        moved++;
                    }
                    else if (Math.Abs(shift) >= bucketMinutes || Math.Abs(endShift) >= bucketMinutes)
                    {
                        kind = shift > 0 ? "Later" : "Earlier";
                        retimed++;
                    }
                    else
                        continue;
                }
                PlanRow? first = (b ?? a)!.OrderBy(x => x.StartTime).First();
                changes.Add(new JsonObject
                {
                    ["itemKey"] = key,
                    ["label"] = first.ItemLabel,
                    ["kind"] = kind,
                    ["beforeResource"] = a == null ? null : string.Join(", ", a.Select(x => x.ResourceKey).Distinct()),
                    ["afterResource"] = b == null ? null : string.Join(", ", b.Select(x => x.ResourceKey).Distinct()),
                    ["beforeStart"] = a?.Min(x => x.StartTime).ToString("s", CultureInfo.InvariantCulture),
                    ["afterStart"] = b?.Min(x => x.StartTime).ToString("s", CultureInfo.InvariantCulture),
                    ["beforeEnd"] = a?.Max(x => x.EndTime).ToString("s", CultureInfo.InvariantCulture),
                    ["afterEnd"] = b?.Max(x => x.EndTime).ToString("s", CultureInfo.InvariantCulture),
                    ["shiftMinutes"] = a != null && b != null ? (int)(b.Min(x => x.StartTime) - a.Min(x => x.StartTime)).TotalMinutes : null,
                    ["beforeLateMinutes"] = a?.Max(x => x.LateMinutes),
                    ["afterLateMinutes"] = b?.Max(x => x.LateMinutes)
                });
            }
            summary["moved"] = moved;
            summary["retimed"] = retimed;
            summary["added"] = added;
            summary["dropped"] = dropped;
            summary["lateBefore"] = was.Values.Count(x => x.Max(y => y.LateMinutes) > 0);
            summary["lateAfter"] = now.Values.Count(x => x.Max(y => y.LateMinutes) > 0);
            summary["lateHoursBefore"] = Math.Round(was.Values.Sum(x => x.Max(y => y.LateMinutes)) / 60.0, 1);
            summary["lateHoursAfter"] = Math.Round(now.Values.Sum(x => x.Max(y => y.LateMinutes)) / 60.0, 1);
            summary["unscheduledAfter"] = unscheduled.Count;
            return new JsonObject { ["summary"] = summary, ["items"] = changes };
        }
    }
}
