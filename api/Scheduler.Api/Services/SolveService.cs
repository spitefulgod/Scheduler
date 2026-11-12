using Scheduler.Solver;
using Scheduler.Solver.Rules;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Scheduler.Api.Services
{
    // What the client asks a run for
    public class SolveRequest
    {
        // Solve, WhatIf or Emergency
        public string Kind { get; set; } = "Solve";
        public DateTime? HorizonStart { get; set; }
        public int? HorizonDays { get; set; }
        public int? TimeLimitSeconds { get; set; }
        public bool? AllowUnscheduled { get; set; }
        // What-if moves: put this item on this resource, at this time when given
        public List<ItemPin> Pins { get; set; } = new List<ItemPin>();
        // Resources down, e.g. a breakdown, locked work on them in that time is released and planned again
        public List<ResourceDown> Downs { get; set; } = new List<ResourceDown>();
        // Where released work may go in an emergency, blank for anywhere the rules allow
        public List<string> Alternatives { get; set; } = new List<string>();
        // Items whose locks are lifted for this run
        public List<string> Unlock { get; set; } = new List<string>();
    }

    // Runs solves in the background, one at a time per profile, a run can be stopped and gives back its best plan so far
    // a run reads the profile and its sources, builds the rule engine's problem with the published plan's locks, solves it,
    // then saves the plan with how it differs from the published one
    public class SolveService
    {
        private readonly string _dbConnSettings;
        private readonly ISourceSecrets _secrets;
        private readonly ILogger<SolveService> _logger;
        private readonly ConcurrentDictionary<string, CpSatScheduler> _running = new ConcurrentDictionary<string, CpSatScheduler>();

        public SolveService(IConfiguration configuration, ISourceSecrets secrets, ILogger<SolveService> logger)
        {
            _dbConnSettings = configuration["SchedulerDBConn"]!.ToString();
            _secrets = secrets;
            _logger = logger;
            // a run that was going when the API last stopped never finishes, so it can't block new ones
            try
            {
                SchedulerDb.Run(_dbConnSettings, "FailUnfinishedSolveRuns");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Couldn't fail the runs left unfinished");
            }
        }

        // Saves the run and starts it, the client follows it with GetSolveRun
        public string Start(string profileId, SolveRequest request)
        {
            JsonElement profile = SchedulerDb.RunElement(_dbConnSettings, "GetProfile", profileId);
            JsonElement settings = profile.TryGetProperty("settings", out JsonElement found) && found.ValueKind == JsonValueKind.Object ? found : default;
            int unit = profile.TryGetProperty("timeUnitMinutes", out JsonElement unitElement) ? unitElement.GetInt32() : 15;

            DateTime start = request.HorizonStart ?? GetDate(settings, "horizonStart") ?? DateTime.Now;
            start = new DateTime(start.Ticks - start.Ticks % TimeSpan.FromMinutes(unit).Ticks, DateTimeKind.Unspecified);
            int days = Math.Clamp(request.HorizonDays ?? GetInt(settings, "horizonDays") ?? 28, 1, 366);
            int limit = Math.Clamp(request.TimeLimitSeconds ?? GetInt(settings, "timeLimitSeconds") ?? 30, 1, 3600);
            request.HorizonStart = start;
            request.HorizonDays = days;
            request.TimeLimitSeconds = limit;
            request.AllowUnscheduled ??= GetBool(settings, "allowUnscheduled") ?? true;
            if (!new[] { "Solve", "WhatIf", "Emergency" }.Contains(request.Kind))
                throw new Exception("The kind needs to be Solve, WhatIf or Emergency");

            string published = SchedulerDb.Run(_dbConnSettings, "GetSolveRun", profileId, null) ?? "{}";
            string? parentId = null;
            using (JsonDocument document = JsonDocument.Parse(published))
                if (document.RootElement.TryGetProperty("id", out JsonElement id))
                    parentId = id.GetString();

            string settingsJson = JsonSerializer.Serialize(request, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            JsonElement run = SchedulerDb.RunElement(_dbConnSettings, "CreateSolveRun", profileId, request.Kind, parentId, settingsJson, limit, start, start.AddDays(days));
            string runId = run.GetProperty("id").GetString()!;
            CpSatScheduler scheduler = new CpSatScheduler();
            _running[runId] = scheduler;
            _ = Task.Run(() => Execute(profileId, runId, request, published, scheduler));
            return runId;
        }

        public bool Stop(string runId)
        {
            if (!_running.TryGetValue(runId, out CpSatScheduler? scheduler))
                return false;
            scheduler.Stop();
            return true;
        }

        private async Task Execute(string profileId, string runId, SolveRequest request, string publishedJson, CpSatScheduler scheduler)
        {
            try
            {
                SchedulerDb.Run(_dbConnSettings, "SetSolveRunStatus", runId, "Running", "Reading the data");
                ProfileLoader loader = new ProfileLoader(_dbConnSettings, _secrets);
                ProfileModel model = await loader.Load(profileId);
                List<PlanRow> published = PlanRow.ReadAll(publishedJson);
                JsonElement locks = SchedulerDb.RunElement(_dbConnSettings, "GetPlanLocks", profileId);
                RunContext context = PlanBuilder.BuildContext(request, published, locks, loader.Settings);

                EngineResult engine = RuleEngine.Build(model, context);
                List<Diagnostic> diagnostics = loader.Warnings.Select(x => new Diagnostic { Level = "warning", Message = x }).Concat(engine.Diagnostics).ToList();
                if (engine.HasErrors)
                {
                    Save(runId, "Invalid", null, "The rules have errors, see the checks", null, engine, diagnostics, published, context, new ScheduleResult(), loader.Settings);
                    return;
                }
                SchedulerDb.Run(_dbConnSettings, "SetSolveRunStatus", runId, "Running", $"Solving {engine.Problem.Items.Count} items on {engine.Problem.Resources.Count} resources");
                ScheduleResult result = scheduler.Solve(engine.Problem, new SolveOptions { TimeLimitSeconds = request.TimeLimitSeconds ?? 30 });
                JsonArray? conflicts = result.Conflicts.Count == 0 ? null
                    : new JsonArray(result.Conflicts.Select(x => (JsonNode)new JsonObject { ["ruleId"] = x.RuleId, ["name"] = x.Name }).ToArray());
                Save(runId, result.Status, result.Objective, result.Message, conflicts, engine, diagnostics, published, context, result, loader.Settings);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Solve run {RunId} failed", runId);
                try
                {
                    SchedulerDb.Run(_dbConnSettings, "SaveSolveResult", runId, "Failed", null, ex.Message, null, null, null, null, null, null, "[]");
                }
                catch (Exception saveError)
                {
                    _logger.LogError(saveError, "Couldn't save the failure of run {RunId}", runId);
                }
            }
            finally
            {
                _running.TryRemove(runId, out _);
            }
        }

        private void Save(string runId, string status, double? objective, string message, JsonArray? conflicts, EngineResult engine, List<Diagnostic> diagnostics,
            List<PlanRow> published, RunContext context, ScheduleResult result, JsonElement settings)
        {
            List<PlanRow> rows = PlanBuilder.ToRows(engine, context, result, published);
            JsonObject details = PlanBuilder.GetDetails(engine, context, settings, result);
            JsonArray unscheduled = PlanBuilder.GetUnscheduled(engine, result);
            JsonObject changes = PlanBuilder.Compare(published, rows, unscheduled, engine.Problem.BucketMinutes);
            JsonObject stats = new JsonObject
            {
                ["items"] = engine.Problem.Items.Count,
                ["planned"] = rows.Where(x => !x.Fixed).Select(x => x.ItemKey).Distinct().Count(),
                ["locked"] = context.Locked.Count,
                ["unscheduled"] = unscheduled.Count,
                ["late"] = rows.Where(x => x.LateMinutes > 0).Select(x => x.ItemKey).Distinct().Count(),
                ["lateHours"] = Math.Round(rows.GroupBy(x => x.ItemKey).Sum(x => x.Max(y => y.LateMinutes)) / 60.0, 1),
                ["wallSeconds"] = Math.Round(result.WallSeconds, 2),
                ["bestBound"] = result.BestBound
            };
            foreach (KeyValuePair<string, int> stat in result.Stats)
                stats[stat.Key] = stat.Value;
            JsonArray diagnosticsJson = new JsonArray(diagnostics.Select(x => (JsonNode)new JsonObject
            {
                ["level"] = x.Level,
                ["ruleId"] = x.RuleId,
                ["ruleName"] = x.RuleName,
                ["itemKey"] = x.ItemKey,
                ["message"] = x.Message
            }).ToArray());
            SchedulerDb.Run(_dbConnSettings, "SaveSolveResult", runId, status, objective, message, conflicts?.ToJsonString(), details.ToJsonString(),
                diagnosticsJson.ToJsonString(), unscheduled.ToJsonString(), changes.ToJsonString(), stats.ToJsonString(), PlanRow.ToJson(rows));
        }

        private static DateTime? GetDate(JsonElement settings, string name)
        {
            if (settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                && DateTime.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date))
                return date;
            return null;
        }
        public static int? GetInt(JsonElement settings, string name)
        {
            if (settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty(name, out JsonElement value))
            {
                if (value.ValueKind == JsonValueKind.Number)
                    return (int)value.GetDouble();
                if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out int parsed))
                    return parsed;
            }
            return null;
        }
        public static bool? GetBool(JsonElement settings, string name)
        {
            if (settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty(name, out JsonElement value))
            {
                if (value.ValueKind == JsonValueKind.True)
                    return true;
                if (value.ValueKind == JsonValueKind.False)
                    return false;
            }
            return null;
        }
        public static string? GetText(JsonElement settings, string name)
        {
            if (settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
            return null;
        }
    }
}
