using Google.OrTools.Sat;
using System.Diagnostics;

namespace Scheduler.Solver
{
    public class SolveOptions
    {
        public double TimeLimitSeconds { get; set; } = 30;
        public int Workers { get; set; } = 8;
        public int RandomSeed { get; set; } = 1;
        // When no plan is possible, solve again to find the hard rules that clash
        public bool ExplainInfeasible { get; set; } = true;
    }

    // Builds a CP-SAT model from a SchedulingProblem and solves it
    // each item has up to MaxSegments segments, each segment picks exactly one mode (resource, pool and rate) with optional intervals,
    // and how many cycles it runs and how many copies it uses, so its length follows from the mode it picks
    public class CpSatScheduler
    {
        private readonly object _lock = new object();
        private CpSolver? _running;
        private bool _stopped;

        // Asks a running solve to stop, it gives back the best plan it has so far
        public void Stop()
        {
            lock (_lock)
            {
                _stopped = true;
                _running?.StopSearch();
            }
        }

        public ScheduleResult Solve(SchedulingProblem problem, SolveOptions options)
        {
            Stopwatch watch = Stopwatch.StartNew();
            ModelBuild build = new ModelBuild(problem, false);
            ScheduleResult result = new ScheduleResult();
            try
            {
                build.Build();
            }
            catch (Exception ex)
            {
                result.Status = "Invalid";
                result.Message = ex.Message;
                return result;
            }
            result.Stats = build.Stats;
            string validation = build.Model.Validate();
            if (!string.IsNullOrEmpty(validation))
            {
                result.Status = "Invalid";
                result.Message = "The model isn't valid: " + validation;
                return result;
            }

            CpSolver solver = new CpSolver();
            solver.StringParameters = $"max_time_in_seconds:{Math.Max(1, options.TimeLimitSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture)} num_workers:{Math.Max(1, options.Workers)} random_seed:{options.RandomSeed}"
                // SCHEDULER_SOLVER_LOG=1 writes CP-SAT's search log to the console, for tuning
                + (Environment.GetEnvironmentVariable("SCHEDULER_SOLVER_LOG") == "1" ? " log_search_progress:true" : "")
                // SCHEDULER_SOLVER_FIXHINT=1 fixes everything to the first plan, an infeasible result means the first plan breaks a rule
                + (Environment.GetEnvironmentVariable("SCHEDULER_SOLVER_FIXHINT") == "1" ? " fix_variables_to_their_hinted_value:true" : "");
            lock (_lock)
            {
                if (_stopped)
                {
                    result.Status = "Stopped";
                    result.Message = "Stopped before it started";
                    return result;
                }
                _running = solver;
            }
            CpSolverStatus status;
            try
            {
                status = solver.Solve(build.Model);
            }
            finally
            {
                lock (_lock)
                    _running = null;
            }

            result.WallSeconds = watch.Elapsed.TotalSeconds;
            if (status == CpSolverStatus.Optimal || status == CpSolverStatus.Feasible)
            {
                result.Status = status == CpSolverStatus.Optimal ? "Optimal" : (_stopped ? "Stopped" : "Feasible");
                result.Objective = solver.ObjectiveValue;
                result.BestBound = solver.BestObjectiveBound;
                build.Extract(solver, result);
                result.Message = status == CpSolverStatus.Optimal
                    ? "The best plan for these rules"
                    : "A good plan, the time limit came before it could be proved the best";
                return result;
            }
            if (status == CpSolverStatus.Infeasible)
            {
                result.Status = "Infeasible";
                result.Message = "No plan can keep every hard rule";
                if (options.ExplainInfeasible)
                    result.Conflicts = Explain(problem, options);
                if (result.Conflicts.Count > 0)
                    result.Message += ", these rules clash: " + string.Join(", ", result.Conflicts.Select(x => x.Name));
                // the locked work is still the plan
                build.ExtractFixed(result);
                return result;
            }
            if (status == CpSolverStatus.ModelInvalid)
            {
                result.Status = "Invalid";
                result.Message = "The model isn't valid: " + solver.SolutionInfo();
                return result;
            }
            result.Status = _stopped ? "Stopped" : "Failed";
            result.Message = _stopped ? "Stopped before a plan was found" : "No plan was found in the time allowed, try a longer time limit";
            build.ExtractFixed(result);
            return result;
        }

        // Solves again with an assumption on each hard rule, CP-SAT names a set of them that can't all hold
        private List<ConflictingRule> Explain(SchedulingProblem problem, SolveOptions options)
        {
            List<ConflictingRule> conflicts = new List<ConflictingRule>();
            try
            {
                ModelBuild build = new ModelBuild(problem, true);
                build.Build();
                if (build.Assumptions.Count == 0)
                    return conflicts;
                build.Model.AddAssumptions(build.Assumptions.Values);
                CpSolver solver = new CpSolver();
                solver.StringParameters = $"max_time_in_seconds:{Math.Clamp(options.TimeLimitSeconds / 2, 2, 20).ToString(System.Globalization.CultureInfo.InvariantCulture)} num_workers:1";
                if (solver.Solve(build.Model) != CpSolverStatus.Infeasible)
                    return conflicts;
                HashSet<int> core = new HashSet<int>(solver.SufficientAssumptionsForInfeasibility());
                foreach (KeyValuePair<string, BoolVar> assumption in build.Assumptions)
                    if (core.Contains(assumption.Value.GetIndex()))
                        conflicts.Add(new ConflictingRule { RuleId = assumption.Key, Name = build.GetRuleName(assumption.Key) });
            }
            catch
            {
                // an explanation is a nice to have, the plan's status already says it failed
            }
            return conflicts;
        }

        // One model build, kept apart so the explain pass can build its own
        private class ModelBuild
        {
            private readonly SchedulingProblem _problem;
            private readonly bool _explain;
            private readonly int _bucketSeconds;
            private readonly int _horizon;
            private readonly List<ItemVars> _items = new List<ItemVars>();
            private readonly Dictionary<string, ItemVars> _itemsByKey = new Dictionary<string, ItemVars>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, List<(IntervalVar interval, List<(string dimension, LinearExpr demand)> demands)>> _resourceUse = new Dictionary<string, List<(IntervalVar, List<(string, LinearExpr)>)>>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, List<(IntervalVar interval, LinearExpr demand)>> _poolUse = new Dictionary<string, List<(IntervalVar, LinearExpr)>>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, BoolVar> _presence = new Dictionary<string, BoolVar>();
            // Which of a pair goes first, and which side of a locked booking new work goes, for the hints
            private readonly List<(BoolVar aFirst, ItemVars a, ItemVars b)> _orders = new List<(BoolVar, ItemVars, ItemVars)>();
            private readonly List<(BoolVar before, ItemVars vars, int bookingStart)> _fixedOrders = new List<(BoolVar, ItemVars, int)>();
            private readonly List<LinearExpr> _objective = new List<LinearExpr>();
            // Each configured resource's windows, with a literal per combination for the one it's in
            private readonly List<ConfigurationWindow> _configurationWindows = new List<ConfigurationWindow>();
            // The same windows by resource, one list for each of its combination sets, in time order
            private readonly Dictionary<string, List<List<ConfigurationWindow>>> _configurationGroups = new Dictionary<string, List<List<ConfigurationWindow>>>(StringComparer.OrdinalIgnoreCase);

            public CpModel Model { get; } = new CpModel();
            public Dictionary<string, BoolVar> Assumptions { get; } = new Dictionary<string, BoolVar>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, int> Stats { get; } = new Dictionary<string, int>();

            public ModelBuild(SchedulingProblem problem, bool explain)
            {
                _problem = problem;
                _explain = explain;
                _bucketSeconds = Math.Max(1, problem.BucketMinutes) * 60;
                _horizon = Math.Max(1, problem.HorizonBuckets);
            }

            public string GetRuleName(string ruleId)
            {
                if (_problem.RuleNames.TryGetValue(ruleId, out string? name))
                    return name;
                if (ruleId.StartsWith("item:"))
                    return $"{ruleId.Substring(5)} must be planned";
                return ruleId;
            }

            public void Build()
            {
                foreach (WorkItem item in _problem.Items)
                {
                    ItemVars vars = new ItemVars(item);
                    _items.Add(vars);
                    _itemsByKey[item.Key] = vars;
                    if (item.Quantity > 0 && item.Modes.Count > 0 && item.ReleaseBucket < _horizon)
                        BuildItem(vars);
                }
                foreach (FixedBooking booking in _problem.Fixed)
                    AddFixed(booking);
                BuildResources();
                BuildPools();
                BuildSeparations();
                BuildPrecedences();
                BuildSharedLimits();
                BuildObjective();
                if (!_explain)
                {
                    AddHints();
                    CompleteHints();
                }
                Stats["items"] = _problem.Items.Count;
                Stats["modes"] = _problem.Items.Sum(x => x.Modes.Count * x.MaxSegments);
                Stats["separations"] = _problem.Separations.Count;
                Stats["fixed"] = _problem.Fixed.Count;
            }

            // What the first plan has booked on a resource or pool
            private class HintBooking
            {
                public int Start { get; set; }
                public int End { get; set; }
                public string ItemKey { get; set; } = "";
                public long Count { get; set; }
                public Dictionary<string, long> Demands { get; } = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            }

            // A quick first plan for the solver to start from, CP-SAT repairs whatever it gets wrong, e.g. rules this doesn't know about:
            // pinned items first, then the published plan's order, then earliest due, each item whole on the mode that ends soonest,
            // keeping each resource's capacities, pools' copies, shared limits, down times and pairs that can't share a resource
            private void AddHints()
            {
                Dictionary<string, List<(int start, int end)>> blocked = new Dictionary<string, List<(int, int)>>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, List<HintBooking>> booked = new Dictionary<string, List<HintBooking>>(StringComparer.OrdinalIgnoreCase);
                List<(int, int)> GetBlocked(string key)
                {
                    if (!blocked.TryGetValue(key, out List<(int, int)>? list))
                        blocked[key] = list = new List<(int, int)>();
                    return list;
                }
                List<HintBooking> GetBooked(string key)
                {
                    if (!booked.TryGetValue(key, out List<HintBooking>? list))
                        booked[key] = list = new List<HintBooking>();
                    return list;
                }
                foreach (WorkResource resource in _problem.Resources)
                    GetBlocked("r:" + resource.Key).AddRange(resource.Unavailable.Select(x => (x.Start, x.End)));
                foreach (WorkPool pool in _problem.Pools)
                    GetBlocked("p:" + pool.Key).AddRange(pool.Unavailable.Select(x => (x.Start, x.End)));
                foreach (FixedBooking booking in _problem.Fixed)
                {
                    HintBooking fixedUse = new HintBooking { Start = booking.StartBucket, End = booking.EndBucket, ItemKey = booking.ItemKey, Count = booking.Count };
                    foreach (DimensionDemand demand in booking.Demands)
                        fixedUse.Demands[demand.Dimension] = fixedUse.Demands.GetValueOrDefault(demand.Dimension) + (long)booking.Count * demand.PerCount + demand.Fixed;
                    GetBooked("f:" + booking.ResourceKey).Add(fixedUse);
                    GetBlocked("r:" + booking.ResourceKey).Add((booking.StartBucket, booking.EndBucket));
                    if (!string.IsNullOrEmpty(booking.PoolKey))
                        GetBooked("p:" + booking.PoolKey).Add(new HintBooking { Start = booking.StartBucket, End = booking.EndBucket, ItemKey = booking.ItemKey, Count = booking.Count });
                }
                Dictionary<string, WorkResource> resources = _problem.Resources.ToDictionary(x => x.Key, x => x, StringComparer.OrdinalIgnoreCase);
                Dictionary<string, int> copies = _problem.Pools.ToDictionary(x => x.Key, x => x.Copies, StringComparer.OrdinalIgnoreCase);
                Dictionary<string, Dictionary<string, int>> separated = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
                foreach (Separation separation in _problem.Separations)
                    foreach ((string a, string b) in new[] { (separation.ItemA, separation.ItemB), (separation.ItemB, separation.ItemA) })
                    {
                        if (!separated.TryGetValue(a, out Dictionary<string, int>? others))
                            separated[a] = others = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        others[b] = Math.Max(others.GetValueOrDefault(b), Math.Max(0, separation.GapBuckets));
                    }
                List<(SharedLimit limit, HashSet<string> keys, List<HintBooking> booked)> limits = _problem.SharedLimits
                    .Select(x => (x, new HashSet<string>(x.ItemKeys, StringComparer.OrdinalIgnoreCase), new List<HintBooking>())).ToList();

                foreach (ItemVars vars in _items.Where(x => x.Segments.Count > 0)
                    .OrderBy(x => x.Item.PinResourceKey != null || x.Item.PinStartBucket.HasValue ? 0 : 1)
                    .ThenBy(x => _problem.Baseline.TryGetValue(x.Item.Key, out BaselinePlacement? placed) ? placed.StartBucket : x.Item.DueBucket ?? int.MaxValue)
                    .ThenBy(x => x.Item.ReleaseBucket))
                {
                    WorkItem item = vars.Item;
                    int release = Math.Max(0, item.PinStartBucket ?? item.ReleaseBucket);
                    BaselinePlacement? baseline = item.PinResourceKey == null && !item.PinStartBucket.HasValue ? _problem.Baseline.GetValueOrDefault(item.Key) : null;
                    Dictionary<string, int> itemSeparations = separated.GetValueOrDefault(item.Key) ?? new Dictionary<string, int>();
                    List<(List<HintBooking> booked, int capacity)> itemLimits = limits.Where(x => x.keys.Contains(item.Key)).Select(x => (x.booked, Math.Max(1, x.limit.Capacity))).ToList();
                    (int mode, int start, int duration, int count, long cycles, long inspections) best = (-1, int.MaxValue, 0, 0, 0, 0);
                    Dictionary<string, long> bestDemands = new Dictionary<string, long>();
                    int bestScore = int.MaxValue;
                    for (int m = 0; m < item.Modes.Count; m++)
                    {
                        WorkMode mode = item.Modes[m];
                        if (item.PinResourceKey != null && !string.Equals(item.PinResourceKey, mode.ResourceKey, StringComparison.OrdinalIgnoreCase))
                            continue;
                        int count = Math.Max(1, Math.Max(mode.MinCount, mode.MaxCount));
                        if (!string.IsNullOrEmpty(mode.PoolKey) && copies.TryGetValue(mode.PoolKey, out int poolCopies))
                            count = Math.Max(Math.Max(1, mode.MinCount), Math.Min(count, poolCopies));
                        long cycles = Math.Max(1, (long)Math.Ceiling((double)item.Quantity / ((long)count * Math.Max(1, mode.UnitsPerCycle))));
                        long inspections = item.EveryCycles > 0 && item.EveryCyclesInspectionMinutes > 0 ? cycles / item.EveryCycles : 0;
                        long seconds = cycles * (long)Math.Ceiling(Math.Max(1, mode.CycleSeconds)) + inspections * item.EveryCyclesInspectionMinutes * 60L
                            + (Math.Max(0, mode.SetupMinutes) + Math.Max(0, item.FirstOffInspectionMinutes) + Math.Max(0, item.SetupInspectionMinutes)) * 60L;
                        int duration = (int)Math.Min(_horizon, (seconds + _bucketSeconds - 1) / _bucketSeconds);
                        Dictionary<string, long> demands = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                        foreach (DimensionDemand demand in mode.Demands)
                            demands[demand.Dimension] = demands.GetValueOrDefault(demand.Dimension) + (long)count * demand.PerCount + demand.Fixed;
                        // where the published plan had it, when it still fits there
                        bool sameResource = baseline != null && string.Equals(baseline.ResourceKey, mode.ResourceKey, StringComparison.OrdinalIgnoreCase);
                        int from = sameResource ? Math.Max(release, baseline!.StartBucket) : release;
                        int start = FindStart(from, duration, count, demands, resources.GetValueOrDefault(mode.ResourceKey), GetBlocked("r:" + mode.ResourceKey),
                            GetBooked("r:" + mode.ResourceKey), string.IsNullOrEmpty(mode.PoolKey) ? null : GetBlocked("p:" + mode.PoolKey),
                            string.IsNullOrEmpty(mode.PoolKey) ? null : GetBooked("p:" + mode.PoolKey), string.IsNullOrEmpty(mode.PoolKey) ? 0 : copies.GetValueOrDefault(mode.PoolKey, 1),
                            itemSeparations, itemLimits, GetBooked("f:" + mode.ResourceKey));
                        // the soonest end, with the mode's preference cost as a few buckets, and moving off the published plan as a lot
                        int score = start + duration + mode.Cost / 50 + (baseline == null ? 0 : (sameResource ? 0 : _horizon / 8) + Math.Abs(start - baseline.StartBucket));
                        if (best.mode < 0 || score < bestScore)
                        {
                            best = (m, start, duration, count, cycles, inspections);
                            bestDemands = demands;
                            bestScore = score;
                        }
                    }
                    if (best.mode < 0 || best.start + best.duration > _horizon)
                    {
                        Model.AddHint(vars.Scheduled!, 0);
                        continue;
                    }
                    WorkMode picked = item.Modes[best.mode];
                    vars.HintStart = best.start;
                    vars.HintEnd = best.start + best.duration;
                    vars.HintResource = picked.ResourceKey;
                    if (vars.Late is not null && item.DueBucket.HasValue)
                        Model.AddHint(vars.Late, Math.Max(0, best.start + best.duration - item.DueBucket.Value));
                    HintBooking placed = new HintBooking { Start = best.start, End = best.start + best.duration, ItemKey = item.Key, Count = best.count };
                    foreach (KeyValuePair<string, long> demand in bestDemands)
                        placed.Demands[demand.Key] = demand.Value;
                    GetBooked("r:" + picked.ResourceKey).Add(placed);
                    if (!string.IsNullOrEmpty(picked.PoolKey))
                        GetBooked("p:" + picked.PoolKey).Add(placed);
                    foreach ((List<HintBooking> limitBooked, int _) in itemLimits)
                        limitBooked.Add(placed);
                    for (int s = 0; s < vars.Segments.Count; s++)
                    {
                        SegmentVars seg = vars.Segments[s];
                        if (s > 0)
                        {
                            Model.AddHint(seg.Active, 0);
                            Model.AddHint(seg.Cycles, 0);
                            Model.AddHint(seg.Duration, 0);
                            Model.AddHint(seg.Units, 0);
                            if (seg.Product is not null)
                                Model.AddHint(seg.Product, 0);
                            foreach (BoolVar mode in seg.Modes)
                                Model.AddHint(mode, 0);
                            continue;
                        }
                        Model.AddHint(seg.Active, 1);
                        Model.AddHint(seg.Start, best.start);
                        Model.AddHint(seg.End, best.start + best.duration);
                        Model.AddHint(seg.Duration, best.duration);
                        Model.AddHint(seg.Count, best.count);
                        Model.AddHint(seg.Cycles, best.cycles);
                        Model.AddHint(seg.Units, best.cycles * best.count * Math.Max(1, picked.UnitsPerCycle));
                        if (seg.Product is not null)
                            Model.AddHint(seg.Product, best.cycles * best.count);
                        if (seg.Inspections is not null)
                            Model.AddHint(seg.Inspections, best.inspections);
                        for (int m = 0; m < seg.Modes.Count; m++)
                            Model.AddHint(seg.Modes[m], m == best.mode ? 1 : 0);
                    }
                }
                HintConfigurations(booked);
            }
            // The combination each window of the first plan is in, holding everything booked and changing as little as it can
            private void HintConfigurations(Dictionary<string, List<HintBooking>> booked)
            {
                foreach (List<ConfigurationWindow> windows in _configurationGroups.Values.SelectMany(x => x))
                {
                    string resourceKey = windows[0].ResourceKey;
                    List<HintBooking> bookings = booked.GetValueOrDefault("r:" + resourceKey, new List<HintBooking>())
                        .Concat(booked.GetValueOrDefault("f:" + resourceKey, new List<HintBooking>())).ToList();
                    int[]? picks = PickConfigurations(windows, bookings);
                    if (picks == null)
                        continue;
                    for (int w = 0; w < windows.Count; w++)
                    {
                        for (int o = 0; o < windows[w].Options.Count; o++)
                            Model.AddHint(windows[w].Options[o], o == picks[w] ? 1 : 0);
                        if (windows[w].Changed is not null)
                            Model.AddHint(windows[w].Changed!, w > 0 && picks[w] != picks[w - 1] ? 1 : 0);
                    }
                }
            }
            // Whether a combination holds what's booked, at the busiest moment for each dimension
            private static bool OptionFits(ConfigurationSet set, int option, List<HintBooking> bookings)
            {
                foreach (string dimension in set.Options.SelectMany(x => x.Limits.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    List<HintBooking> taking = bookings.Where(x => x.Demands.GetValueOrDefault(dimension) > 0).ToList();
                    long most = 0;
                    foreach (HintBooking at in taking)
                        most = Math.Max(most, taking.Where(x => x.Start <= at.Start && x.End > at.Start).Sum(x => x.Demands.GetValueOrDefault(dimension)));
                    if (most > set.Options[option].Limits.GetValueOrDefault(dimension))
                        return false;
                }
                return true;
            }
            // A combination for each window that holds what's booked in it, with as few changes as there can be and never a change
            // while work runs across its start, null when there's none, everything overlapping a window counts so it's safe rather than tight
            private static int[]? PickConfigurations(List<ConfigurationWindow> windows, List<HintBooking> bookings)
            {
                const int none = int.MaxValue / 2;
                ConfigurationSet set = windows[0].Set;
                int count = set.Options.Count;
                int[,] cost = new int[windows.Count, count];
                int[,] from = new int[windows.Count, count];
                for (int w = 0; w < windows.Count; w++)
                {
                    ConfigurationWindow window = windows[w];
                    List<HintBooking> inWindow = bookings.Where(x => x.Start < window.End && x.End > window.Start).ToList();
                    bool noChange = set.ChangeBuckets > 0 && bookings.Any(x => x.Start < window.Start + set.ChangeBuckets && x.End > window.Start);
                    for (int o = 0; o < count; o++)
                    {
                        cost[w, o] = none;
                        if (!OptionFits(set, o, inWindow))
                            continue;
                        if (w == 0)
                        {
                            cost[w, o] = 0;
                            continue;
                        }
                        for (int p = 0; p < count; p++)
                        {
                            int step = p == o ? 0 : (noChange ? none : 1);
                            if (cost[w - 1, p] >= none || step >= none)
                                continue;
                            if (cost[w - 1, p] + step < cost[w, o])
                            {
                                cost[w, o] = cost[w - 1, p] + step;
                                from[w, o] = p;
                            }
                        }
                    }
                }
                int last = windows.Count - 1, best = -1;
                for (int o = 0; o < count; o++)
                    if (cost[last, o] < none && (best < 0 || cost[last, o] < cost[last, best]))
                        best = o;
                if (best < 0)
                    return null;
                int[] picks = new int[windows.Count];
                for (int w = last; w >= 0; w--)
                {
                    picks[w] = best;
                    if (w > 0)
                        best = from[w, best];
                }
                return picks;
            }
            // The rest of the hint follows from where the items went: which resource each is on and which of a pair goes first
            private void CompleteHints()
            {
                foreach (KeyValuePair<string, BoolVar> presence in _presence)
                {
                    string[] parts = presence.Key.Split('\u0001');
                    ItemVars? vars = _itemsByKey.GetValueOrDefault(parts[0]);
                    if (vars?.HintResource != null)
                        Model.AddHint(presence.Value, parts[1] == "0" && string.Equals(parts[2], vars.HintResource, StringComparison.OrdinalIgnoreCase) ? 1 : 0);
                }
                foreach ((BoolVar aFirst, ItemVars a, ItemVars b) in _orders)
                    if (a.HintStart.HasValue && b.HintStart.HasValue)
                        Model.AddHint(aFirst, a.HintStart.Value <= b.HintStart.Value ? 1 : 0);
                foreach ((BoolVar before, ItemVars vars, int bookingStart) in _fixedOrders)
                    if (vars.HintEnd.HasValue)
                        Model.AddHint(before, vars.HintEnd.Value <= bookingStart ? 1 : 0);
            }
            // The first start from the time given where the work fits: not while the resource or pool is down, within each of the resource's
            // capacities and the pool's copies, apart from items it can't share the resource with, and within its shared limits,
            // everything that overlaps at all is counted so it's safe rather than tight
            private int FindStart(int from, int length, int count, Dictionary<string, long> demands, WorkResource? resource, List<(int start, int end)> resourceDown,
                List<HintBooking> resourceBooked, List<(int start, int end)>? poolDown, List<HintBooking>? poolBooked, int poolCopies,
                Dictionary<string, int> separations, List<(List<HintBooking> booked, int capacity)> limits, List<HintBooking> fixedUse)
            {
                int start = from;
                length = Math.Max(1, length);
                while (start < _horizon)
                {
                    int end = start + length;
                    int next = int.MaxValue;
                    foreach ((int s, int e) in poolDown == null ? resourceDown : resourceDown.Concat(poolDown))
                        if (s < end && e > start)
                            next = Math.Min(next, e);
                    if (next == int.MaxValue)
                    {
                        List<HintBooking> overlapping = new List<HintBooking>();
                        foreach (HintBooking other in resourceBooked)
                        {
                            int gap = separations.TryGetValue(other.ItemKey, out int found) ? found : -1;
                            if (gap >= 0 && other.Start - gap < end && other.End + gap > start)
                                next = Math.Min(next, other.End + gap);
                            else if (other.Start < end && other.End > start)
                                overlapping.Add(other);
                        }
                        if (resource == null || resource.Capacities.Count == 0)
                        {
                            if (overlapping.Count > 0)
                                next = Math.Min(next, overlapping.Min(x => x.End));
                        }
                        else
                            foreach (KeyValuePair<string, int> capacity in resource.Capacities)
                            {
                                long need = demands.GetValueOrDefault(capacity.Key);
                                if (need > 0 && overlapping.Sum(x => x.Demands.GetValueOrDefault(capacity.Key)) + need > capacity.Value)
                                    next = Math.Min(next, overlapping.Where(x => x.Demands.GetValueOrDefault(capacity.Key) > 0).Min(x => x.End));
                            }
                        if (poolBooked != null)
                        {
                            List<HintBooking> sharing = poolBooked.Where(x => x.Start < end && x.End > start).ToList();
                            if (sharing.Sum(x => x.Count) + count > poolCopies && sharing.Count > 0)
                                next = Math.Min(next, sharing.Min(x => x.End));
                        }
                        foreach ((List<HintBooking> booked, int capacity) in limits)
                        {
                            List<HintBooking> sharing = booked.Where(x => x.Start < end && x.End > start).ToList();
                            if (sharing.Count + 1 > capacity)
                                next = Math.Min(next, sharing.Min(x => x.End));
                        }
                        // the resource's combinations still have to work out with it added, past a window it can't fit in at all
                        if (resource != null && next == int.MaxValue && _configurationGroups.TryGetValue(resource.Key, out List<List<ConfigurationWindow>>? groups))
                        {
                            HintBooking adding = new HintBooking { Start = start, End = end, Count = count };
                            foreach (KeyValuePair<string, long> demand in demands)
                                adding.Demands[demand.Key] = demand.Value;
                            List<HintBooking> all = resourceBooked.Concat(fixedUse).Append(adding).ToList();
                            foreach (List<ConfigurationWindow> windows in groups)
                            {
                                if (!windows.Any(x => x.Start < end && x.End > start) || PickConfigurations(windows, all) != null)
                                    continue;
                                next = Math.Min(next, start + 1);
                                foreach (ConfigurationWindow window in windows.Where(x => x.Start < end && x.End > start))
                                {
                                    List<HintBooking> inWindow = all.Where(x => x.Start < window.End && x.End > window.Start).ToList();
                                    if (!Enumerable.Range(0, window.Set.Options.Count).Any(o => OptionFits(window.Set, o, inWindow)))
                                    {
                                        next = Math.Max(next, window.End);
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    if (next == int.MaxValue)
                        return start;
                    start = Math.Max(start + 1, next);
                }
                return start;
            }

            private BoolVar GetAssumption(string ruleId)
            {
                if (!Assumptions.TryGetValue(ruleId, out BoolVar? literal))
                {
                    literal = Model.NewBoolVar("rule:" + ruleId);
                    Assumptions[ruleId] = literal;
                }
                return literal;
            }
            // The literals a hard rule's constraints are switched on by, only in the explain pass
            private ILiteral[] WithRule(string ruleId, params ILiteral[] literals)
            {
                if (!_explain || string.IsNullOrEmpty(ruleId))
                    return literals;
                return literals.Append(GetAssumption(ruleId)).ToArray();
            }

            private void BuildItem(ItemVars vars)
            {
                WorkItem item = vars.Item;
                int release = Math.Max(0, item.ReleaseBucket);
                int minRate = item.Modes.Min(x => Math.Max(1, x.UnitsPerCycle) * Math.Max(1, x.MinCount));
                int maxCount = Math.Max(1, item.Modes.Max(x => Math.Max(x.MinCount, x.MaxCount)));
                int minCount = Math.Max(1, item.Modes.Min(x => Math.Max(1, x.MinCount)));
                int maxRate = item.Modes.Max(x => Math.Max(1, x.UnitsPerCycle) * Math.Max(x.MinCount, x.MaxCount));
                double fastestCycle = item.Modes.Min(x => Math.Max(1, x.CycleSeconds));
                long maxCycles = (long)Math.Ceiling((double)item.Quantity / minRate);
                maxCycles = Math.Max(1, Math.Min(maxCycles, (long)Math.Ceiling(_horizon * (double)_bucketSeconds / fastestCycle)));
                int segments = Math.Max(1, item.MaxSegments);

                // must it be planned
                vars.Scheduled = Model.NewBoolVar($"sched_{item.Key}");
                if (item.Required || !_problem.AllowUnscheduled || item.PinStartBucket.HasValue)
                {
                    if (_explain)
                        Model.Add(vars.Scheduled == 1).OnlyEnforceIf(GetAssumption("item:" + item.Key));
                    else
                        Model.Add(vars.Scheduled == 1);
                }

                List<LinearExpr> units = new List<LinearExpr>();
                for (int s = 0; s < segments; s++)
                {
                    SegmentVars seg = new SegmentVars();
                    seg.Active = s == 0 ? vars.Scheduled : Model.NewBoolVar($"act_{item.Key}_{s}");
                    seg.Start = Model.NewIntVar(release, _horizon, $"s_{item.Key}_{s}");
                    seg.End = Model.NewIntVar(release, _horizon, $"e_{item.Key}_{s}");
                    seg.Duration = Model.NewIntVar(0, _horizon, $"d_{item.Key}_{s}");
                    seg.Interval = Model.NewOptionalIntervalVar(seg.Start, seg.Duration, seg.End, seg.Active, $"iv_{item.Key}_{s}");
                    seg.Cycles = Model.NewIntVar(0, maxCycles, $"c_{item.Key}_{s}");
                    seg.Count = Model.NewIntVar(minCount, maxCount, $"n_{item.Key}_{s}");
                    seg.Units = Model.NewIntVar(0, maxCycles * maxRate, $"u_{item.Key}_{s}");
                    if (s > 0)
                    {
                        Model.AddImplication(seg.Active, vars.Segments[s - 1].Active);
                        Model.Add(seg.Start >= vars.Segments[s - 1].Start).OnlyEnforceIf(seg.Active);
                        _objective.Add(seg.Active * (long)Math.Max(0, item.SegmentPenalty));
                    }
                    Model.Add(seg.Cycles == 0).OnlyEnforceIf(seg.Active.Not());
                    Model.Add(seg.Duration == 0).OnlyEnforceIf(seg.Active.Not());
                    Model.Add(seg.Units == 0).OnlyEnforceIf(seg.Active.Not());
                    Model.Add(seg.Cycles >= Math.Max(1, Math.Min(item.MinSegmentCycles, maxCycles))).OnlyEnforceIf(seg.Active);

                    // tool cycles, cycles times copies, a product only when the count can vary
                    LinearExpr toolCycles;
                    if (minCount == maxCount)
                        toolCycles = seg.Cycles * minCount;
                    else
                    {
                        IntVar product = Model.NewIntVar(0, maxCycles * maxCount, $"p_{item.Key}_{s}");
                        Model.AddMultiplicationEquality(product, seg.Cycles, seg.Count);
                        seg.Product = product;
                        toolCycles = product;
                    }

                    // inspections every N cycles, the solver keeps the count as low as the cycles allow
                    LinearExpr inspectionSeconds = LinearExpr.Constant(0);
                    if (item.EveryCycles > 0 && item.EveryCyclesInspectionMinutes > 0)
                    {
                        seg.Inspections = Model.NewIntVar(0, maxCycles / item.EveryCycles + 1, $"ins_{item.Key}_{s}");
                        Model.Add(seg.Inspections * item.EveryCycles >= seg.Cycles - (item.EveryCycles - 1));
                        inspectionSeconds = seg.Inspections * (item.EveryCyclesInspectionMinutes * 60L);
                    }

                    List<ILiteral> modeLiterals = new List<ILiteral>();
                    for (int m = 0; m < item.Modes.Count; m++)
                    {
                        WorkMode mode = item.Modes[m];
                        BoolVar picked = Model.NewBoolVar($"m_{item.Key}_{s}_{m}");
                        seg.Modes.Add(picked);
                        modeLiterals.Add(picked);
                        Model.AddImplication(picked, seg.Active);
                        Model.Add(seg.Count >= Math.Max(1, mode.MinCount)).OnlyEnforceIf(picked);
                        Model.Add(seg.Count <= Math.Max(mode.MinCount, mode.MaxCount)).OnlyEnforceIf(picked);
                        Model.Add(seg.Units == toolCycles * Math.Max(1, mode.UnitsPerCycle)).OnlyEnforceIf(picked);

                        long cycleSeconds = (long)Math.Ceiling(Math.Max(1, mode.CycleSeconds));
                        long fixedSeconds = (Math.Max(0, mode.SetupMinutes) + Math.Max(0, item.FirstOffInspectionMinutes) + Math.Max(0, item.SetupInspectionMinutes)) * 60L;
                        LinearExpr needed = seg.Cycles * cycleSeconds + inspectionSeconds + fixedSeconds;
                        Model.Add(seg.Duration * _bucketSeconds >= needed).OnlyEnforceIf(picked);
                        Model.Add(seg.Duration * _bucketSeconds <= needed + (_bucketSeconds - 1)).OnlyEnforceIf(picked);

                        IntervalVar interval = Model.NewOptionalIntervalVar(seg.Start, seg.Duration, seg.End, picked, $"mi_{item.Key}_{s}_{m}");
                        List<(string, LinearExpr)> demands = new List<(string, LinearExpr)>();
                        foreach (DimensionDemand demand in mode.Demands)
                            demands.Add((demand.Dimension, seg.Count * demand.PerCount + demand.Fixed));
                        GetResourceUse(mode.ResourceKey).Add((interval, demands));
                        if (!string.IsNullOrEmpty(mode.PoolKey))
                            GetPoolUse(mode.PoolKey).Add((interval, seg.Count));

                        if (mode.Cost != 0)
                            _objective.Add(picked * ((long)mode.Cost * _problem.Weights.Preferences));
                        if (s == 0 && _problem.Weights.StabilityResource > 0 && _problem.Baseline.TryGetValue(item.Key, out BaselinePlacement? baseline)
                            && !string.Equals(baseline.ResourceKey, mode.ResourceKey, StringComparison.OrdinalIgnoreCase))
                            _objective.Add(picked * (long)_problem.Weights.StabilityResource);
                    }
                    Model.Add(LinearExpr.Sum(modeLiterals.Cast<BoolVar>()) == seg.Active);
                    units.Add(seg.Units);
                    vars.Segments.Add(seg);
                }

                // every unit made, a last cycle can make a few more than asked for but no more
                Model.Add(LinearExpr.Sum(units) >= item.Quantity).OnlyEnforceIf(vars.Scheduled);
                Model.Add(LinearExpr.Sum(units) <= item.Quantity + (long)maxRate * segments);

                if (!item.AllowParallel && segments > 1)
                    Model.AddNoOverlap(vars.Segments.Select(x => x.Interval));

                if (item.PinStartBucket.HasValue)
                    Model.Add(vars.Segments[0].Start == Math.Clamp(item.PinStartBucket.Value, release, _horizon));

                // costs
                long weight = Math.Max(1, item.LatenessWeight);
                if (_problem.AllowUnscheduled && _problem.Weights.Unscheduled > 0)
                    _objective.Add((1 - (LinearExpr)vars.Scheduled) * ((long)_problem.Weights.Unscheduled * weight));
                if (item.DueBucket.HasValue && _problem.Weights.Lateness > 0)
                {
                    IntVar late = Model.NewIntVar(0, _horizon, $"late_{item.Key}");
                    foreach (SegmentVars seg in vars.Segments)
                        Model.Add(late >= seg.End - item.DueBucket.Value).OnlyEnforceIf(seg.Active);
                    vars.Late = late;
                    _objective.Add(late * (weight * _problem.Weights.Lateness));
                }
                if (_problem.Weights.Compact > 0)
                    foreach (SegmentVars seg in vars.Segments)
                        _objective.Add(seg.End * (long)_problem.Weights.Compact);
                if (_problem.Weights.StabilityTime > 0 && _problem.Baseline.TryGetValue(item.Key, out BaselinePlacement? placed))
                {
                    IntVar moved = Model.NewIntVar(0, _horizon, $"moved_{item.Key}");
                    Model.AddAbsEquality(moved, vars.Segments[0].Start - placed.StartBucket);
                    _objective.Add(moved * (long)_problem.Weights.StabilityTime);
                }
            }

            private void AddFixed(FixedBooking booking)
            {
                int start = Math.Clamp(booking.StartBucket, 0, _horizon);
                int end = Math.Clamp(booking.EndBucket, start, _horizon);
                if (_itemsByKey.TryGetValue(booking.ItemKey, out ItemVars? vars))
                {
                    vars.FixedStart = Math.Min(vars.FixedStart ?? int.MaxValue, booking.StartBucket);
                    vars.FixedEnd = Math.Max(vars.FixedEnd ?? int.MinValue, booking.EndBucket);
                }
                if (end <= start)
                    return;
                IntervalVar interval = Model.NewFixedSizeIntervalVar(start, end - start, $"fixed_{booking.ItemKey}_{start}");
                List<(string, LinearExpr)> demands = new List<(string, LinearExpr)>();
                foreach (DimensionDemand demand in booking.Demands)
                    demands.Add((demand.Dimension, LinearExpr.Constant((long)booking.Count * demand.PerCount + demand.Fixed)));
                GetResourceUse(booking.ResourceKey).Add((interval, demands));
                if (!string.IsNullOrEmpty(booking.PoolKey))
                    GetPoolUse(booking.PoolKey).Add((interval, LinearExpr.Constant(booking.Count)));
            }

            private List<(IntervalVar, List<(string, LinearExpr)>)> GetResourceUse(string resourceKey)
            {
                if (!_resourceUse.TryGetValue(resourceKey, out List<(IntervalVar, List<(string, LinearExpr)>)>? use))
                {
                    use = new List<(IntervalVar, List<(string, LinearExpr)>)>();
                    _resourceUse[resourceKey] = use;
                }
                return use;
            }
            private List<(IntervalVar, LinearExpr)> GetPoolUse(string poolKey)
            {
                if (!_poolUse.TryGetValue(poolKey, out List<(IntervalVar, LinearExpr)>? use))
                {
                    use = new List<(IntervalVar, LinearExpr)>();
                    _poolUse[poolKey] = use;
                }
                return use;
            }

            // A cumulative for each capacity dimension of each resource, or no overlap at all when it has none
            private void BuildResources()
            {
                foreach (WorkResource resource in _problem.Resources)
                {
                    List<(IntervalVar interval, List<(string dimension, LinearExpr demand)> demands)> use = _resourceUse.GetValueOrDefault(resource.Key)
                        ?? new List<(IntervalVar, List<(string, LinearExpr)>)>();
                    List<IntervalVar> blocked = GetBlocked(resource.Unavailable, $"down_{resource.Key}");
                    if (resource.Capacities.Count == 0)
                    {
                        if (use.Count > 0)
                            Model.AddNoOverlap(use.Select(x => x.interval).Concat(blocked));
                        continue;
                    }
                    Dictionary<string, CumulativeConstraint> cumulatives = new Dictionary<string, CumulativeConstraint>(StringComparer.OrdinalIgnoreCase);
                    foreach (KeyValuePair<string, int> capacity in resource.Capacities)
                    {
                        CumulativeConstraint cumulative = Model.AddCumulative(Math.Max(0, capacity.Value));
                        cumulatives[capacity.Key] = cumulative;
                        foreach ((IntervalVar interval, List<(string dimension, LinearExpr demand)> demands) in use)
                            foreach ((string dimension, LinearExpr demand) in demands)
                                if (string.Equals(dimension, capacity.Key, StringComparison.OrdinalIgnoreCase))
                                {
                                    cumulative.AddDemand(interval, demand);
                                }
                    }
                    List<IntervalVar> changes = new List<IntervalVar>();
                    foreach (ConfigurationSet set in resource.Configurations)
                        changes.AddRange(BuildConfigurations(resource, set, cumulatives));
                    // work never runs while the resource is down or changing combination, even when it takes none of a dimension,
                    // apart as a change may happen while the resource is down
                    foreach (List<IntervalVar> stops in new[] { blocked, changes })
                    {
                        if (stops.Count == 0 || use.Count == 0)
                            continue;
                        CumulativeConstraint stopped = Model.AddCumulative(use.Count);
                        foreach ((IntervalVar interval, List<(string, LinearExpr)> _) in use)
                            stopped.AddDemand(interval, 1);
                        foreach (IntervalVar stop in stops)
                            stopped.AddDemand(stop, use.Count);
                    }
                }
            }
            // A resource in one combination per window: a literal per combination, exactly one on, and in each dimension a fixed
            // interval over the window taking up what the chosen combination doesn't allow, so the work in the window has the
            // combination's limit rather than the most any combination allows, e.g. 7 A or 5 A and 1 B, never 7 A and 1 B
            // a change of combination stops the resource at the start of the window, the intervals for that are given back
            private List<IntervalVar> BuildConfigurations(WorkResource resource, ConfigurationSet set, Dictionary<string, CumulativeConstraint> cumulatives)
            {
                List<IntervalVar> changes = new List<IntervalVar>();
                if (set.Options.Count == 0)
                    return changes;
                List<string> dimensions = set.Options.SelectMany(x => x.Limits.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                List<ConfigurationWindow> group = new List<ConfigurationWindow>();
                ConfigurationWindow? previous = null;
                foreach (BucketRange range in set.Windows)
                {
                    int start = Math.Clamp(range.Start, 0, _horizon), end = Math.Clamp(range.End, start, _horizon);
                    if (end <= start)
                        continue;
                    ConfigurationWindow window = new ConfigurationWindow { ResourceKey = resource.Key, Set = set, Start = start, End = end };
                    for (int o = 0; o < set.Options.Count; o++)
                        window.Options.Add(Model.NewBoolVar($"cfg_{resource.Key}_{start}_{o}"));
                    if (_explain)
                        Model.Add(LinearExpr.Sum(window.Options) == 1).OnlyEnforceIf(GetAssumption(set.RuleId));
                    else
                        Model.AddExactlyOne(window.Options);
                    // an optional interval per combination, there when it's the one chosen
                    List<IntervalVar> intervals = window.Options.Select((x, o) => Model.NewOptionalFixedSizeIntervalVar(start, end - start, x, $"cfg_{resource.Key}_{start}_{o}")).ToList();
                    foreach (string dimension in dimensions)
                    {
                        if (!cumulatives.TryGetValue(dimension, out CumulativeConstraint? cumulative))
                            continue;
                        int most = resource.Capacities.GetValueOrDefault(dimension);
                        for (int o = 0; o < set.Options.Count; o++)
                        {
                            int unused = Math.Max(0, most - Math.Min(most, set.Options[o].Limits.GetValueOrDefault(dimension)));
                            if (unused > 0)
                                cumulative.AddDemand(intervals[o], unused);
                        }
                    }
                    // a change of combination from the window before takes time at the window's start and costs
                    if (previous != null && (set.ChangeCost > 0 || set.ChangeBuckets > 0) && set.Options.Count > 1)
                    {
                        window.Changed = Model.NewBoolVar($"cfg_change_{resource.Key}_{start}");
                        for (int o = 0; o < set.Options.Count; o++)
                            Model.Add(window.Changed >= window.Options[o] - previous.Options[o]);
                        if (set.ChangeCost > 0)
                            _objective.Add(window.Changed * (long)set.ChangeCost);
                        if (set.ChangeBuckets > 0)
                            changes.Add(Model.NewOptionalFixedSizeIntervalVar(start, Math.Min(set.ChangeBuckets, _horizon - start), window.Changed, $"cfg_reset_{resource.Key}_{start}"));
                    }
                    _configurationWindows.Add(window);
                    group.Add(window);
                    previous = window;
                }
                if (group.Count > 0)
                {
                    if (!_configurationGroups.TryGetValue(resource.Key, out List<List<ConfigurationWindow>>? groups))
                        _configurationGroups[resource.Key] = groups = new List<List<ConfigurationWindow>>();
                    groups.Add(group);
                }
                return changes;
            }
            private void BuildPools()
            {
                foreach (WorkPool pool in _problem.Pools)
                {
                    if (!_poolUse.TryGetValue(pool.Key, out List<(IntervalVar interval, LinearExpr demand)>? use))
                        continue;
                    CumulativeConstraint cumulative = Model.AddCumulative(Math.Max(0, pool.Copies));
                    foreach ((IntervalVar interval, LinearExpr demand) in use)
                        cumulative.AddDemand(interval, demand);
                    foreach (IntervalVar down in GetBlocked(pool.Unavailable, $"pooldown_{pool.Key}"))
                        cumulative.AddDemand(down, Math.Max(0, pool.Copies));
                }
            }
            private List<IntervalVar> GetBlocked(List<BucketRange> ranges, string name)
            {
                List<IntervalVar> blocked = new List<IntervalVar>();
                foreach (BucketRange range in ranges)
                {
                    int start = Math.Clamp(range.Start, 0, _horizon);
                    int end = Math.Clamp(range.End, 0, _horizon);
                    if (end > start)
                        blocked.Add(Model.NewFixedSizeIntervalVar(start, end - start, $"{name}_{start}"));
                }
                return blocked;
            }

            // True when the item's segment runs on the resource
            private BoolVar? GetPresence(ItemVars vars, int segment, string resourceKey)
            {
                string key = $"{vars.Item.Key}\u0001{segment}\u0001{resourceKey}";
                if (_presence.TryGetValue(key, out BoolVar? found))
                    return found;
                List<BoolVar> literals = new List<BoolVar>();
                for (int m = 0; m < vars.Item.Modes.Count; m++)
                    if (string.Equals(vars.Item.Modes[m].ResourceKey, resourceKey, StringComparison.OrdinalIgnoreCase))
                        literals.Add(vars.Segments[segment].Modes[m]);
                if (literals.Count == 0)
                    return null;
                BoolVar on = Model.NewBoolVar($"on_{vars.Item.Key}_{segment}_{resourceKey}");
                Model.Add(LinearExpr.Sum(literals) == on);
                _presence[key] = on;
                return on;
            }

            // Pairs that can't overlap on a resource, or need a gap between them when they share it
            private void BuildSeparations()
            {
                Dictionary<string, List<FixedBooking>> fixedByItem = _problem.Fixed.GroupBy(x => x.ItemKey, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.OrdinalIgnoreCase);
                foreach (Separation separation in _problem.Separations)
                {
                    ItemVars? a = _itemsByKey.GetValueOrDefault(separation.ItemA);
                    ItemVars? b = _itemsByKey.GetValueOrDefault(separation.ItemB);
                    if (a == null || b == null)
                        continue;
                    int gap = Math.Max(0, separation.GapBuckets);
                    HashSet<string> shared = new HashSet<string>(a.Item.Modes.Select(x => x.ResourceKey), StringComparer.OrdinalIgnoreCase);
                    shared.IntersectWith(b.Item.Modes.Select(x => x.ResourceKey));
                    foreach (string resourceKey in shared)
                        for (int sa = 0; sa < a.Segments.Count; sa++)
                            for (int sb = 0; sb < b.Segments.Count; sb++)
                            {
                                BoolVar? onA = GetPresence(a, sa, resourceKey);
                                BoolVar? onB = GetPresence(b, sb, resourceKey);
                                if (onA is null || onB is null)
                                    continue;
                                BoolVar aFirst = Model.NewBoolVar($"sep_{a.Item.Key}_{b.Item.Key}_{resourceKey}_{sa}_{sb}");
                                _orders.Add((aFirst, a, b));
                                Model.Add(a.Segments[sa].End + gap <= b.Segments[sb].Start).OnlyEnforceIf(WithRule(separation.RuleId, aFirst, onA, onB));
                                Model.Add(b.Segments[sb].End + gap <= a.Segments[sa].Start).OnlyEnforceIf(WithRule(separation.RuleId, aFirst.Not(), onA, onB));
                            }
                    // locked work of one against the other's new segments
                    SeparateFromFixed(a, fixedByItem.GetValueOrDefault(b.Item.Key), gap, separation.RuleId);
                    SeparateFromFixed(b, fixedByItem.GetValueOrDefault(a.Item.Key), gap, separation.RuleId);
                }
            }
            private void SeparateFromFixed(ItemVars vars, List<FixedBooking>? bookings, int gap, string ruleId)
            {
                if (bookings == null)
                    return;
                foreach (FixedBooking booking in bookings)
                    for (int s = 0; s < vars.Segments.Count; s++)
                    {
                        BoolVar? on = GetPresence(vars, s, booking.ResourceKey);
                        if (on is null)
                            continue;
                        BoolVar before = Model.NewBoolVar($"fsep_{vars.Item.Key}_{s}_{booking.ItemKey}_{booking.StartBucket}");
                        _fixedOrders.Add((before, vars, booking.StartBucket));
                        Model.Add(vars.Segments[s].End + gap <= booking.StartBucket).OnlyEnforceIf(WithRule(ruleId, before, on));
                        Model.Add(vars.Segments[s].Start >= booking.EndBucket + gap).OnlyEnforceIf(WithRule(ruleId, before.Not(), on));
                    }
            }

            private void BuildPrecedences()
            {
                foreach (Precedence precedence in _problem.Precedences)
                {
                    ItemVars? before = _itemsByKey.GetValueOrDefault(precedence.Before);
                    ItemVars? after = _itemsByKey.GetValueOrDefault(precedence.After);
                    if (before == null || after == null)
                        continue;
                    int gap = Math.Max(0, precedence.GapBuckets);
                    // after can't be planned when before isn't
                    if (after.Scheduled is not null)
                    {
                        if (before.Scheduled is not null)
                        {
                            List<ILiteral> literals = new List<ILiteral> { after.Scheduled.Not(), before.Scheduled };
                            if (_explain)
                                literals.Add(GetAssumption(precedence.RuleId).Not());
                            Model.AddBoolOr(literals);
                        }
                        else if (before.FixedEnd == null && before.Item.Quantity > 0)
                            Model.Add(after.Scheduled == 0).OnlyEnforceIf(WithRule(precedence.RuleId));
                    }
                    foreach (SegmentVars b in after.Segments)
                    {
                        if (before.FixedEnd.HasValue)
                            Model.Add(b.Start >= before.FixedEnd.Value + gap).OnlyEnforceIf(WithRule(precedence.RuleId, b.Active));
                        foreach (SegmentVars a in before.Segments)
                            Model.Add(a.End + gap <= b.Start).OnlyEnforceIf(WithRule(precedence.RuleId, a.Active, b.Active));
                    }
                    if (after.FixedStart.HasValue)
                        foreach (SegmentVars a in before.Segments)
                            Model.Add(a.End + gap <= after.FixedStart.Value).OnlyEnforceIf(WithRule(precedence.RuleId, a.Active));
                }
            }

            private void BuildSharedLimits()
            {
                foreach (SharedLimit limit in _problem.SharedLimits)
                {
                    CumulativeConstraint cumulative = Model.AddCumulative(Math.Max(0, limit.Capacity));
                    HashSet<string> keys = new HashSet<string>(limit.ItemKeys, StringComparer.OrdinalIgnoreCase);
                    foreach (string key in keys)
                        if (_itemsByKey.TryGetValue(key, out ItemVars? vars))
                            foreach (SegmentVars seg in vars.Segments)
                                cumulative.AddDemand(seg.Interval, 1);
                    foreach (FixedBooking booking in _problem.Fixed)
                        if (keys.Contains(booking.ItemKey) && booking.EndBucket > Math.Max(0, booking.StartBucket) && booking.StartBucket < _horizon)
                        {
                            int start = Math.Clamp(booking.StartBucket, 0, _horizon);
                            int end = Math.Clamp(booking.EndBucket, start, _horizon);
                            if (end > start)
                                cumulative.AddDemand(Model.NewFixedSizeIntervalVar(start, end - start, $"lim_{booking.ItemKey}_{start}"), 1);
                        }
                }
            }

            private void BuildObjective()
            {
                if (_problem.Weights.Makespan > 0)
                {
                    IntVar makespan = Model.NewIntVar(0, _horizon, "makespan");
                    foreach (ItemVars vars in _items)
                        foreach (SegmentVars seg in vars.Segments)
                            Model.Add(makespan >= seg.End).OnlyEnforceIf(seg.Active);
                    _objective.Add(makespan * (long)_problem.Weights.Makespan);
                }
                if (_objective.Count > 0)
                    Model.Minimize(LinearExpr.Sum(_objective));
            }

            public void Extract(CpSolver solver, ScheduleResult result)
            {
                foreach (ItemVars vars in _items)
                {
                    WorkItem item = vars.Item;
                    if (item.Modes.Count == 0)
                    {
                        if (item.Quantity > 0)
                            result.Unscheduled.Add(new UnscheduledItem { ItemKey = item.Key, Reason = item.NoModeReason ?? "No resource is allowed by the rules" });
                        continue;
                    }
                    if (vars.Scheduled is null)
                    {
                        if (item.Quantity > 0)
                            result.Unscheduled.Add(new UnscheduledItem { ItemKey = item.Key, Reason = "Released after the end of the horizon" });
                        continue;
                    }
                    if (!solver.BooleanValue(vars.Scheduled))
                    {
                        result.Unscheduled.Add(new UnscheduledItem { ItemKey = item.Key, Reason = "Doesn't fit in the horizon with the current rules and locks" });
                        continue;
                    }
                    for (int s = 0; s < vars.Segments.Count; s++)
                    {
                        SegmentVars seg = vars.Segments[s];
                        if (!solver.BooleanValue(seg.Active))
                            continue;
                        int m = seg.Modes.FindIndex(x => solver.BooleanValue(x));
                        if (m < 0)
                            continue;
                        WorkMode mode = item.Modes[m];
                        int start = (int)solver.Value(seg.Start);
                        int end = (int)solver.Value(seg.End);
                        long cycles = solver.Value(seg.Cycles);
                        SegmentAssignment assignment = new SegmentAssignment
                        {
                            ItemKey = item.Key,
                            Segment = s,
                            ResourceKey = mode.ResourceKey,
                            PoolKey = mode.PoolKey,
                            Count = (int)solver.Value(seg.Count),
                            Cycles = cycles,
                            Quantity = solver.Value(seg.Units),
                            StartBucket = start,
                            EndBucket = end,
                            SetupMinutes = mode.SetupMinutes,
                            LateBuckets = item.DueBucket.HasValue ? Math.Max(0, end - item.DueBucket.Value) : 0,
                            Cost = mode.Cost,
                            CostReasons = mode.CostReasons
                        };
                        assignment.Inspections = GetInspections(item, mode, cycles, seg.Inspections is null ? 0 : solver.Value(seg.Inspections));
                        result.Assignments.Add(assignment);
                    }
                }
                ExtractFixed(result);
                foreach (ConfigurationWindow window in _configurationWindows)
                {
                    int o = window.Options.FindIndex(x => solver.BooleanValue(x));
                    if (o < 0)
                        continue;
                    ResourceConfiguration option = window.Set.Options[o];
                    ConfigurationChoice? last = result.Configurations.LastOrDefault();
                    if (last != null && last.ResourceKey == window.ResourceKey && last.RuleId == window.Set.RuleId && last.Key == option.Key && last.EndBucket == window.Start)
                        last.EndBucket = window.End;
                    else
                        result.Configurations.Add(new ConfigurationChoice { ResourceKey = window.ResourceKey, RuleId = window.Set.RuleId, Key = option.Key, Label = option.Label, StartBucket = window.Start, EndBucket = window.End });
                }
            }
            public void ExtractFixed(ScheduleResult result)
            {
                foreach (FixedBooking booking in _problem.Fixed)
                    result.Assignments.Add(new SegmentAssignment
                    {
                        ItemKey = booking.ItemKey,
                        Segment = -1,
                        ResourceKey = booking.ResourceKey,
                        PoolKey = booking.PoolKey,
                        Count = booking.Count,
                        Quantity = booking.Quantity,
                        StartBucket = booking.StartBucket,
                        EndBucket = booking.EndBucket,
                        Fixed = true
                    });
            }
            // Where the inspections fall in a segment, setup, then the first off after the first cycle, then every N cycles
            private static List<InspectionPoint> GetInspections(WorkItem item, WorkMode mode, long cycles, long periodic)
            {
                List<InspectionPoint> points = new List<InspectionPoint>();
                double cycleMinutes = Math.Max(1, mode.CycleSeconds) / 60.0;
                double at = mode.SetupMinutes;
                if (item.SetupInspectionMinutes > 0)
                {
                    points.Add(new InspectionPoint { Kind = "Setup", OffsetMinutes = at, Minutes = item.SetupInspectionMinutes });
                    at += item.SetupInspectionMinutes;
                }
                if (cycles <= 0)
                    return points;
                at += cycleMinutes;
                if (item.FirstOffInspectionMinutes > 0)
                {
                    points.Add(new InspectionPoint { Kind = "FirstOff", OffsetMinutes = at, Minutes = item.FirstOffInspectionMinutes });
                    at += item.FirstOffInspectionMinutes;
                }
                if (item.EveryCycles > 0 && item.EveryCyclesInspectionMinutes > 0)
                {
                    double done = 1;
                    for (long k = 1; k <= Math.Min(periodic, 500); k++)
                    {
                        double cycleNo = k * item.EveryCycles;
                        if (cycleNo > cycles)
                            break;
                        at += (cycleNo - done) * cycleMinutes;
                        done = cycleNo;
                        points.Add(new InspectionPoint { Kind = "Periodic", OffsetMinutes = at, Minutes = item.EveryCyclesInspectionMinutes });
                        at += item.EveryCyclesInspectionMinutes;
                    }
                }
                return points;
            }
        }

        private class ConfigurationWindow
        {
            public string ResourceKey { get; set; } = "";
            public ConfigurationSet Set { get; set; } = null!;
            public int Start { get; set; }
            public int End { get; set; }
            public List<BoolVar> Options { get; } = new List<BoolVar>();
            public BoolVar? Changed { get; set; }
        }

        private class ItemVars
        {
            public WorkItem Item { get; }
            public BoolVar? Scheduled { get; set; }
            public List<SegmentVars> Segments { get; } = new List<SegmentVars>();
            public IntVar? Late { get; set; }
            public int? FixedStart { get; set; }
            public int? FixedEnd { get; set; }
            // Where the first plan put it, for the hints
            public int? HintStart { get; set; }
            public int? HintEnd { get; set; }
            public string? HintResource { get; set; }

            public ItemVars(WorkItem item)
            {
                Item = item;
            }
        }
        private class SegmentVars
        {
            public BoolVar Active { get; set; } = null!;
            public IntVar Start { get; set; } = null!;
            public IntVar End { get; set; } = null!;
            public IntVar Duration { get; set; } = null!;
            public IntervalVar Interval { get; set; } = null!;
            public IntVar Cycles { get; set; } = null!;
            public IntVar Count { get; set; } = null!;
            public IntVar Units { get; set; } = null!;
            public IntVar? Inspections { get; set; }
            public List<BoolVar> Modes { get; } = new List<BoolVar>();
            public IntVar? Product { get; set; }
        }
    }
}
