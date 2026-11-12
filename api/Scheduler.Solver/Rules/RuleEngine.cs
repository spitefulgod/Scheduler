using System.Globalization;

namespace Scheduler.Solver.Rules
{
    public class Diagnostic
    {
        // error, warning or info
        public string Level { get; set; } = "info";
        public string? RuleId { get; set; }
        public string? RuleName { get; set; }
        public string? ItemKey { get; set; }
        public string Message { get; set; } = "";
    }

    // A row of an entity type with its key and label
    public class EntityRow
    {
        public string Key { get; set; } = "";
        public string Label { get; set; } = "";
        public EntityModel Entity { get; set; } = null!;
        public Dictionary<string, object?> Values { get; set; } = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    }

    public class EngineResult
    {
        public SchedulingProblem Problem { get; set; } = new SchedulingProblem();
        public List<Diagnostic> Diagnostics { get; set; } = new List<Diagnostic>();
        public string? ItemEntity { get; set; }
        public string? ResourceEntity { get; set; }
        public string? ToolingEntity { get; set; }
        // How many items each rule applied to and how many ways to run them it took out, by rule id
        public Dictionary<string, RuleStat> RuleStats { get; set; } = new Dictionary<string, RuleStat>();
        public bool HasErrors => Diagnostics.Any(x => x.Level == "error");
    }

    public class RuleStat
    {
        public int Items { get; set; }
        public int ModesRemoved { get; set; }
    }

    // The rule engine, it reads a profile's rows and rules and makes the generic problem the solver takes
    // each rule type is handled here by name, the RuleTypes catalogue describes their parameters to the rule builder
    //   RunsOn         items run on one of the rows of a resource entity, e.g. Press
    //   UsesTooling    items also draw copies from a pool entity, e.g. Tool, with alternatives, how many at once and units per cycle
    //   Allowed        a relationship decides which item / resource / tooling combinations are allowed, avoided or forbidden
    //   Condition      an expression over item, resource and tooling, e.g. tooling.size <= resource.max_tool_size
    //   Capacity       a resource dimension, e.g. platen space, and what each tool copy or item takes of it
    //   CapacityMatrix a resource x key matrix of how many fit, e.g. 7 small or 2 large tools on a press
    //   Configurations combinations a resource can be set up in, e.g. 7 A tools, or 5 A and 1 B, one combination per window
    //   Setup          minutes to set a mode up, e.g. a tool change
    //   Inspection     first off, every N cycles and on setup inspections
    //   Split          an item may run in several segments with different tool counts, in parallel when allowed
    //   Compatible     a matrix of values, e.g. compounds, that can't share a resource at the same time
    //   MustMatch      items sharing a resource at once need the same value, e.g. cycle time or temperature
    //   Changeover     a gap between items whose values differ, e.g. colour or allergen clean downs
    //   Precedence     an item after another, e.g. operations of an order
    //   SharedLimit    a limit across every resource, e.g. at most 2 silicone jobs at once
    //   Calendar       resources (and tooling) only work in their calendars' working time
    //   Goal           lateness, makespan, compact, stability or unscheduled weights
    public class RuleEngine
    {
        private readonly ProfileModel _model;
        private readonly RunContext _context;
        private readonly EngineResult _result = new EngineResult();
        private readonly Dictionary<string, Formula?> _formulas = new Dictionary<string, Formula?>();
        private readonly Dictionary<string, Dictionary<string, EntityRow>> _rows = new Dictionary<string, Dictionary<string, EntityRow>>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _reported = new HashSet<string>();
        private List<RuleModel> _rules = new List<RuleModel>();

        public RuleEngine(ProfileModel model, RunContext context)
        {
            _model = model;
            _context = context;
        }

        public static EngineResult Build(ProfileModel model, RunContext context)
        {
            return new RuleEngine(model, context).Run();
        }

        private SchedulingProblem Problem => _result.Problem;

        private EngineResult Run()
        {
            Problem.BucketMinutes = Math.Clamp(_model.TimeUnitMinutes, 1, 1440);
            Problem.HorizonStart = _context.HorizonStart;
            Problem.HorizonBuckets = Math.Max(1, (int)Math.Ceiling(Math.Max(1, _context.HorizonDays) * 1440.0 / Problem.BucketMinutes));
            Problem.AllowUnscheduled = _context.AllowUnscheduled;
            if (_context.Weights != null)
                Problem.Weights = _context.Weights;

            _rules = _model.Rules.Where(x => x.Enabled).ToList();
            foreach (RuleModel rule in _rules)
                Problem.RuleNames[rule.Id] = rule.Name;
            foreach (EntityModel entity in _model.Entities)
                _rows[entity.Name] = ReadRows(entity);

            // what's an item, what do they run on and what do they draw from
            List<EntityModel> itemEntities = _model.Entities.Where(x => string.Equals(x.Kind, "Item", StringComparison.OrdinalIgnoreCase)).ToList();
            if (itemEntities.Count == 0)
            {
                Error(null, "There's no entity of kind Item to schedule");
                return _result;
            }
            List<RuleModel> runsOn = RulesOf("RunsOn");
            EntityModel? resourceEntity = runsOn.Select(x => _model.GetEntity(x.GetParam("resource"))).FirstOrDefault(x => x != null)
                ?? _model.Entities.FirstOrDefault(x => string.Equals(x.Kind, "Resource", StringComparison.OrdinalIgnoreCase));
            if (resourceEntity == null)
            {
                Error(null, "No resource to run on, add a Runs on rule naming a Resource entity");
                return _result;
            }
            foreach (RuleModel rule in runsOn)
            {
                EntityModel? named = _model.GetEntity(rule.GetParam("resource"));
                if (named != null && named != resourceEntity)
                    Warn(rule, $"Only one resource entity can be run on, {resourceEntity.Name} is used and {named.Name} is ignored");
            }
            _result.ItemEntity = string.Join(", ", itemEntities.Select(x => x.Name));
            _result.ResourceEntity = resourceEntity.Name;

            List<EntityRow> resources = _rows[resourceEntity.Name].Values.ToList();
            BuildResources(resourceEntity, resources);
            BuildConfigurations(resourceEntity);
            BuildPools();

            Dictionary<string, (WorkItem item, EntityRow row)> items = new Dictionary<string, (WorkItem, EntityRow)>(StringComparer.OrdinalIgnoreCase);
            foreach (EntityModel entity in itemEntities)
                foreach (EntityRow row in _rows[entity.Name].Values)
                {
                    if (_context.Exclude.Contains(row.Key))
                        continue;
                    if (items.ContainsKey(row.Key))
                    {
                        Warn(null, $"{entity.Name} {row.Key} has the same key as another item and is left out");
                        continue;
                    }
                    WorkItem item = BuildItem(row, resourceEntity, resources);
                    items[row.Key] = (item, row);
                    Problem.Items.Add(item);
                }

            BuildLocked(items, resourceEntity);
            BuildPairs(items);
            BuildPrecedences(items);
            BuildSharedLimits(items);
            BuildBaseline();
            return _result;
        }

        // Rows keyed by their Id attribute, with the Name attribute as the label
        private Dictionary<string, EntityRow> ReadRows(EntityModel entity)
        {
            Dictionary<string, EntityRow> rows = new Dictionary<string, EntityRow>(StringComparer.OrdinalIgnoreCase);
            AttributeModel? id = entity.GetByRole("Id");
            AttributeModel? name = entity.GetByRole("Name");
            int index = 0;
            foreach (Dictionary<string, object?> values in entity.Rows)
            {
                index++;
                Dictionary<string, object?> copy = new Dictionary<string, object?>(values, StringComparer.OrdinalIgnoreCase);
                string key = (id == null ? null : Formula.ToText(copy.GetValueOrDefault(id.Name))?.Trim()) ?? index.ToString(CultureInfo.InvariantCulture);
                if (key == "" || rows.ContainsKey(key))
                    continue;
                string label = (name == null ? null : Formula.ToText(copy.GetValueOrDefault(name.Name))) ?? key;
                rows[key] = new EntityRow { Key = key, Label = label, Entity = entity, Values = copy };
            }
            return rows;
        }

        private List<RuleModel> RulesOf(string ruleTypeId)
        {
            return _rules.Where(x => string.Equals(x.RuleTypeId, ruleTypeId, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        // Whether a rule is for this item, its who entity and condition
        private bool Applies(RuleModel rule, EntityRow item)
        {
            string? entity = rule.GetWhoEntity();
            if (!string.IsNullOrWhiteSpace(entity) && !string.Equals(entity, item.Entity.Name, StringComparison.OrdinalIgnoreCase) && entity != item.Entity.Id)
                return false;
            string? when = rule.GetWhoWhen();
            bool applies = true;
            if (when != null)
            {
                Formula? formula = GetFormula(rule, "who.when", when);
                applies = formula != null && SafeBool(rule, formula, Scope(item, null, null));
            }
            if (applies && _counted.Add(rule.Id + "\u0001" + item.Key))
                GetStat(rule).Items++;
            return applies;
        }
        private readonly HashSet<string> _counted = new HashSet<string>();
        private RuleStat GetStat(RuleModel rule)
        {
            if (!_result.RuleStats.TryGetValue(rule.Id, out RuleStat? stat))
            {
                stat = new RuleStat();
                _result.RuleStats[rule.Id] = stat;
            }
            return stat;
        }

        private void BuildResources(EntityModel resourceEntity, List<EntityRow> resources)
        {
            List<RuleModel> calendarRules = RulesOf("Calendar");
            bool useCalendars = calendarRules.Count > 0;
            DateTime from = Problem.HorizonStart, to = Problem.ToTime(Problem.HorizonBuckets);
            foreach (EntityRow row in resources)
            {
                WorkResource resource = new WorkResource { Key = row.Key, Label = row.Label };
                foreach (KeyValuePair<string, object?> value in row.Values)
                    resource.Attributes[value.Key] = Formula.ToText(value.Value);
                if (useCalendars)
                {
                    CalendarModel? calendar = CalendarBuilder.FindFor(_model, resourceEntity.Name, row.Key);
                    if (calendar != null)
                        foreach ((DateTime start, DateTime end, string reason) in CalendarBuilder.GetDownTime(_model, calendar, from, to))
                            resource.Unavailable.Add(new BucketRange { Start = Problem.ToBucket(start), End = Problem.ToBucketCeiling(end), Reason = reason });
                }
                foreach (ResourceDown down in _context.Downs.Where(x => string.Equals(x.ResourceKey, row.Key, StringComparison.OrdinalIgnoreCase)))
                    resource.Unavailable.Add(new BucketRange { Start = Problem.ToBucket(down.From), End = Problem.ToBucketCeiling(down.To), Reason = down.Reason ?? "Down" });
                Problem.Resources.Add(resource);
            }

            // capacity dimensions from Capacity and CapacityMatrix rules
            foreach (RuleModel rule in RulesOf("Capacity"))
            {
                string? text = rule.GetParam("capacity");
                Formula? capacity = text == null ? null : GetFormula(rule, "capacity", text);
                if (capacity == null)
                {
                    Warn(rule, "Needs a capacity, e.g. resource.platen_slots");
                    continue;
                }
                foreach (WorkResource resource in Problem.Resources)
                {
                    EntityRow row = _rows[resourceEntity.Name][resource.Key];
                    double? value = SafeNumber(rule, capacity, Scope(null, row, null));
                    resource.Capacities["cap:" + rule.Id] = (int)Math.Floor(Math.Max(0, value ?? 0) + 1e-9);
                }
            }
            foreach (RuleModel rule in RulesOf("CapacityMatrix"))
            {
                RelationshipModel? matrix = _model.GetRelationship(rule.GetParam("relationshipId"));
                if (matrix == null)
                {
                    Warn(rule, "Needs a resource x size matrix with numbers in it");
                    continue;
                }
                bool resourceIsA = string.Equals(matrix.EntityA, resourceEntity.Name, StringComparison.OrdinalIgnoreCase);
                foreach (WorkResource resource in Problem.Resources)
                    foreach (RelationshipValueModel value in matrix.Values)
                    {
                        string resourceKey = resourceIsA ? value.A : value.B;
                        string otherKey = resourceIsA ? value.B : value.A;
                        if (!string.Equals(resourceKey, resource.Key, StringComparison.OrdinalIgnoreCase))
                            continue;
                        double? count = Formula.ToNumber(value.Value);
                        if (count.HasValue)
                            resource.Capacities[$"mat:{rule.Id}:{otherKey}"] = (int)Math.Floor(Math.Max(0, count.Value) + 1e-9);
                    }
            }
        }

        // Configurations rules: each resource gets the combinations it's allowed, and its capacity in each size is the most any of them has
        private readonly Dictionary<string, HashSet<string>> _configured = new Dictionary<string, HashSet<string>>();
        private void BuildConfigurations(EntityModel resourceEntity)
        {
            foreach (RuleModel rule in RulesOf("Configurations"))
            {
                RelationshipModel? matrix = _model.GetRelationship(rule.GetParam("relationshipId"));
                if (matrix == null)
                {
                    Warn(rule, "Needs a combination x size matrix with numbers in it");
                    continue;
                }
                RelationshipModel? allowed = _model.GetRelationship(rule.GetParam("allowedRelationshipId"));
                bool resourceIsA = allowed != null && string.Equals(allowed.EntityA, resourceEntity.Name, StringComparison.OrdinalIgnoreCase);
                string? combinationEntity = allowed == null ? matrix.EntityA : (resourceIsA ? allowed.EntityB : allowed.EntityA);
                bool combinationIsA = !string.Equals(matrix.EntityB, combinationEntity, StringComparison.OrdinalIgnoreCase) || string.Equals(matrix.EntityA, combinationEntity, StringComparison.OrdinalIgnoreCase);
                Dictionary<string, EntityRow> combinationRows = combinationEntity != null && _rows.TryGetValue(combinationEntity, out Dictionary<string, EntityRow>? found) ? found : new Dictionary<string, EntityRow>();

                // the combinations, from the matrix, each a count per size
                Dictionary<string, ResourceConfiguration> combinations = new Dictionary<string, ResourceConfiguration>(StringComparer.OrdinalIgnoreCase);
                foreach (RelationshipValueModel value in matrix.Values)
                {
                    string combination = combinationIsA ? value.A : value.B;
                    string size = combinationIsA ? value.B : value.A;
                    double? count = Formula.ToNumber(value.Value);
                    if (!combinations.TryGetValue(combination, out ResourceConfiguration? option))
                    {
                        string label = combinationRows.TryGetValue(combination, out EntityRow? row) && !string.IsNullOrEmpty(row.Label) ? row.Label : combination;
                        combinations[combination] = option = new ResourceConfiguration { Key = combination, Label = label };
                    }
                    if (count.HasValue && count.Value > 0)
                        option.Limits[$"cfg:{rule.Id}:{size}"] = (int)Math.Floor(count.Value + 1e-9);
                }
                if (combinations.Count == 0)
                {
                    Warn(rule, $"{matrix.Name} has no combinations in it");
                    continue;
                }
                ValueScaleModel? scale = allowed == null ? null : _model.GetScale(allowed.ValueScaleId);

                // when the combination can change, windows from midnight, none means it never does
                int windowHours = ParamIntConst(rule, "windowHours", 24);
                List<BucketRange> windows = new List<BucketRange>();
                if (windowHours <= 0)
                    windows.Add(new BucketRange { Start = 0, End = Problem.HorizonBuckets });
                else
                {
                    int length = Math.Max(1, Problem.MinutesToBuckets(windowHours * 60.0));
                    int from = Problem.ToBucket(Problem.HorizonStart.Date);
                    for (int start = from; start < Problem.HorizonBuckets; start += length)
                        if (start + length > 0)
                            windows.Add(new BucketRange { Start = Math.Max(0, start), End = Math.Min(Problem.HorizonBuckets, start + length) });
                }
                HashSet<string> configured = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _configured[rule.Id] = configured;
                foreach (WorkResource resource in Problem.Resources)
                {
                    List<ResourceConfiguration> options = new List<ResourceConfiguration>();
                    foreach (ResourceConfiguration option in combinations.Values)
                    {
                        if (allowed != null)
                        {
                            string? value = resourceIsA ? allowed.Get(resource.Key, option.Key) : allowed.Get(option.Key, resource.Key);
                            ScaleValueModel? meaning = scale?.Get(value);
                            if (value == null || meaning == null || meaning.Meaning == "Forbidden")
                                continue;
                        }
                        options.Add(option);
                    }
                    // a resource with no combinations isn't limited by this rule
                    if (options.Count == 0)
                        continue;
                    configured.Add(resource.Key);
                    foreach (string dimension in options.SelectMany(x => x.Limits.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
                        resource.Capacities[dimension] = options.Max(x => x.Limits.GetValueOrDefault(dimension));
                    int changeMinutes = Math.Max(0, ParamInt(rule, "changeMinutes", Scope(null, _rows[resourceEntity.Name][resource.Key], null), 0));
                    resource.Configurations.Add(new ConfigurationSet
                    {
                        RuleId = rule.Id,
                        Windows = windows,
                        ChangeBuckets = changeMinutes == 0 ? 0 : Problem.MinutesToBuckets(changeMinutes),
                        ChangeCost = Math.Max(0, ParamIntConst(rule, "changeCost", 0)),
                        Options = options
                    });
                }
                if (configured.Count == 0)
                    Warn(rule, $"No {resourceEntity.Name} is allowed any of the combinations, so the rule does nothing");
            }
        }

        // Tooling pools from UsesTooling rules, one pool per row with its copies
        private void BuildPools()
        {
            foreach (RuleModel rule in RulesOf("UsesTooling"))
            {
                EntityModel? tooling = _model.GetEntity(rule.GetParam("tooling"));
                if (tooling == null)
                {
                    Warn(rule, "Needs the tooling entity, e.g. Tool");
                    continue;
                }
                _result.ToolingEntity ??= tooling.Name;
                string copiesText = rule.GetParam("copies") ?? (tooling.GetByRole("Copies") is AttributeModel copiesAttribute ? "tooling." + copiesAttribute.Name : "1");
                Formula? copies = GetFormula(rule, "copies", copiesText);
                List<CalendarModel> calendars = RulesOf("Calendar").Any(x => string.Equals(x.GetParam("applyTo"), "tooling", StringComparison.OrdinalIgnoreCase)) ? _model.Calendars : new List<CalendarModel>();
                foreach (EntityRow row in _rows[tooling.Name].Values)
                {
                    if (Problem.Pools.Any(x => string.Equals(x.Key, PoolKey(tooling, row.Key), StringComparison.OrdinalIgnoreCase)))
                        continue;
                    WorkPool pool = new WorkPool { Key = PoolKey(tooling, row.Key), Label = row.Label };
                    pool.Copies = copies == null ? 1 : (int)Math.Max(0, Math.Floor((SafeNumber(rule, copies, Scope(null, null, row)) ?? 1) + 1e-9));
                    CalendarModel? own = calendars.FirstOrDefault(x => string.Equals(x.ResourceKey, row.Key, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(x.ResourceEntity, tooling.Name, StringComparison.OrdinalIgnoreCase));
                    if (own != null)
                        foreach ((DateTime start, DateTime end, string reason) in CalendarBuilder.GetDownTime(_model, own, Problem.HorizonStart, Problem.ToTime(Problem.HorizonBuckets)))
                            pool.Unavailable.Add(new BucketRange { Start = Problem.ToBucket(start), End = Problem.ToBucketCeiling(end), Reason = reason });
                    foreach (ResourceDown down in _context.Downs.Where(x => string.Equals(x.ResourceKey, row.Key, StringComparison.OrdinalIgnoreCase) || string.Equals(x.ResourceKey, pool.Key, StringComparison.OrdinalIgnoreCase)))
                        pool.Unavailable.Add(new BucketRange { Start = Problem.ToBucket(down.From), End = Problem.ToBucketCeiling(down.To), Reason = down.Reason ?? "Down" });
                    Problem.Pools.Add(pool);
                }
            }
        }
        // Pools are kept by their row key, the entity name only goes in front when two tooling entities share a key
        private string PoolKey(EntityModel tooling, string key)
        {
            List<string> toolingEntities = RulesOf("UsesTooling").Select(x => _model.GetEntity(x.GetParam("tooling"))?.Name).Where(x => x != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList()!;
            return toolingEntities.Count > 1 ? tooling.Name + ":" + key : key;
        }

        private WorkItem BuildItem(EntityRow row, EntityModel resourceEntity, List<EntityRow> resources)
        {
            EntityModel entity = row.Entity;
            WorkItem item = new WorkItem { Key = row.Key, Label = row.Label };
            foreach (KeyValuePair<string, object?> value in row.Values)
                item.Attributes[value.Key] = Formula.ToText(value.Value);

            // how much and how long, from the cycle time, or the duration formula as one batch
            AttributeModel? quantity = entity.GetByRole("Quantity");
            AttributeModel? cycle = entity.GetByRole("CycleTime");
            AttributeModel? duration = entity.GetByRole("Duration");
            double cycleSeconds = 60;
            if (cycle != null)
            {
                item.Quantity = (long)Math.Max(0, Math.Ceiling((quantity == null ? 1 : Formula.ToNumber(row.Values.GetValueOrDefault(quantity.Name))) ?? 0));
                cycleSeconds = Formula.ToNumber(row.Values.GetValueOrDefault(cycle.Name)) ?? 0;
                if (cycleSeconds <= 0)
                {
                    Warn(null, $"{row.Key} has no cycle time, a minute is used", row.Key);
                    cycleSeconds = 60;
                }
            }
            else
            {
                double? minutes = null;
                if (!string.IsNullOrWhiteSpace(entity.DurationFormula))
                {
                    Formula? formula = GetFormula(null, "duration:" + entity.Id, entity.DurationFormula!);
                    minutes = formula == null ? null : SafeNumber(null, formula, Scope(row, null, null));
                }
                else if (duration != null)
                    minutes = Formula.ToNumber(row.Values.GetValueOrDefault(duration.Name));
                item.Quantity = 1;
                cycleSeconds = Math.Max(1, (minutes ?? Problem.BucketMinutes) * 60);
            }

            AttributeModel? release = entity.GetByRole("ReleaseDate");
            if (release != null && ToDate(row.Values.GetValueOrDefault(release.Name)) is DateTime released)
                item.ReleaseBucket = Math.Max(0, Problem.ToBucketCeiling(released));
            AttributeModel? due = entity.GetByRole("DueDate");
            if (due != null && ToDate(row.Values.GetValueOrDefault(due.Name)) is DateTime dueDate)
                item.DueBucket = Problem.ToBucket(dueDate);

            ApplyItemRules(item, row);
            BuildModes(item, row, resourceEntity, resources, cycleSeconds);
            return item;
        }

        // Inspection, Split and Goal rules set things on the item itself
        private void ApplyItemRules(WorkItem item, EntityRow row)
        {
            Func<string, object?> resolve = Scope(row, null, null);
            foreach (RuleModel rule in RulesOf("Inspection").Where(x => Applies(x, row)))
            {
                item.FirstOffInspectionMinutes = Math.Max(item.FirstOffInspectionMinutes, ParamInt(rule, "firstOffMinutes", resolve));
                int every = ParamInt(rule, "everyCycles", resolve);
                if (every > 0)
                {
                    item.EveryCycles = item.EveryCycles == 0 ? every : Math.Min(item.EveryCycles, every);
                    item.EveryCyclesInspectionMinutes = Math.Max(item.EveryCyclesInspectionMinutes, ParamInt(rule, "everyMinutes", resolve));
                }
                item.SetupInspectionMinutes = Math.Max(item.SetupInspectionMinutes, ParamInt(rule, "setupMinutes", resolve));
            }
            foreach (RuleModel rule in RulesOf("Split").Where(x => Applies(x, row)))
            {
                item.MaxSegments = Math.Clamp(ParamInt(rule, "maxSegments", resolve, 2), 1, 8);
                item.MinSegmentCycles = Math.Max(1, ParamInt(rule, "minCycles", resolve, 1));
                string? parallel = rule.GetParam("allowParallel");
                item.AllowParallel = parallel != null && GetFormula(rule, "allowParallel", parallel) is Formula formula && SafeBool(rule, formula, resolve);
                item.SegmentPenalty = Math.Max(0, ParamInt(rule, "segmentPenalty", resolve, rule.Weight > 0 ? rule.Weight : 50));
            }
            foreach (RuleModel rule in RulesOf("Goal"))
            {
                string goal = rule.GetParam("goal") ?? "Lateness";
                if (!string.Equals(goal, "Lateness", StringComparison.OrdinalIgnoreCase) || !Applies(rule, row))
                    continue;
                string? weightText = rule.GetParam("weightAttribute");
                if (weightText == null)
                    continue;
                if (!weightText.Contains('.') && !weightText.Contains('('))
                    weightText = "item." + weightText;
                double? weight = GetFormula(rule, "weightAttribute", weightText) is Formula formula ? SafeNumber(rule, formula, resolve) : null;
                if (!weight.HasValue)
                    continue;
                // higher numbers are more important unless the rule says lower is, e.g. priority 1 first
                bool higher = !string.Equals(rule.GetParam("higherIsMoreImportant"), "false", StringComparison.OrdinalIgnoreCase);
                double max = Math.Max(1, ParamInt(rule, "maxValue", resolve, 10));
                item.LatenessWeight = (int)Math.Max(1, Math.Round(higher ? weight.Value : max + 1 - weight.Value));
            }
            ApplyGoalWeights();
        }

        private bool _goalsApplied;
        // Goal rules set the plan's weights, the rule's weight is the weight on that goal
        private void ApplyGoalWeights()
        {
            if (_goalsApplied || _context.Weights != null)
                return;
            _goalsApplied = true;
            foreach (RuleModel rule in RulesOf("Goal"))
            {
                int weight = Math.Max(0, rule.Weight);
                switch ((rule.GetParam("goal") ?? "Lateness").ToLowerInvariant())
                {
                    case "lateness":
                        Problem.Weights.Lateness = weight;
                        break;
                    case "makespan":
                        Problem.Weights.Makespan = weight;
                        break;
                    case "compact":
                        Problem.Weights.Compact = weight;
                        break;
                    case "stability":
                        Problem.Weights.StabilityResource = weight;
                        Problem.Weights.StabilityTime = Math.Max(0, ParamIntConst(rule, "timeWeight", 1));
                        break;
                    case "unscheduled":
                        Problem.Weights.Unscheduled = weight;
                        break;
                    case "preferences":
                        Problem.Weights.Preferences = Math.Max(1, weight);
                        break;
                }
            }
        }

        // Every resource, times every tool the item can use, less the ones the rules don't allow
        private void BuildModes(WorkItem item, EntityRow row, EntityModel resourceEntity, List<EntityRow> resources, double cycleSeconds)
        {
            RuleModel? toolingRule = RulesOf("UsesTooling").FirstOrDefault(x => Applies(x, row));
            EntityModel? tooling = toolingRule == null ? null : _model.GetEntity(toolingRule.GetParam("tooling"));
            List<(EntityRow? tool, int cost, string? reason)> tools = new List<(EntityRow?, int, string?)>();
            if (toolingRule != null && tooling != null)
            {
                Dictionary<string, EntityRow> toolRows = _rows[tooling.Name];
                string? defaultText = toolingRule.GetParam("toolAttribute");
                if (defaultText != null && GetFormula(toolingRule, "toolAttribute", defaultText) is Formula toolFormula)
                {
                    string? toolKey = Formula.ToText(SafeEvaluate(toolingRule, toolFormula, Scope(row, null, null)))?.Trim();
                    if (!string.IsNullOrEmpty(toolKey) && toolRows.TryGetValue(toolKey, out EntityRow? tool))
                        tools.Add((tool, 0, null));
                }
                RelationshipModel? alternatives = _model.GetRelationship(toolingRule.GetParam("alternativesRelationshipId"));
                if (alternatives != null)
                {
                    ValueScaleModel? scale = _model.GetScale(alternatives.ValueScaleId);
                    foreach ((string b, string value) in alternatives.GetForA(row.Key))
                    {
                        if (!toolRows.TryGetValue(b, out EntityRow? tool) || tools.Any(x => x.tool == tool))
                            continue;
                        ScaleValueModel? meaning = scale?.Get(value);
                        if (meaning != null && meaning.Meaning == "Forbidden")
                            continue;
                        int cost = meaning != null && meaning.Meaning == "Penalty" ? meaning.Weight : 0;
                        tools.Add((tool, cost, cost > 0 ? $"{alternatives.Name}: {meaning!.Label}" : null));
                    }
                }
                if (tools.Count == 0)
                {
                    item.NoModeReason = $"No {tooling.Name} is set for it ({toolingRule.Name})";
                    return;
                }
            }
            else
                tools.Add((null, 0, null));

            // why each resource went, so an item with nowhere to go can say why
            Dictionary<string, int> removedBy = new Dictionary<string, int>();
            List<string> allowedResources = _context.RestrictTo.GetValueOrDefault(item.Key) ?? new List<string>();
            ItemPin? pin = _context.Pins.FirstOrDefault(x => string.Equals(x.ItemKey, item.Key, StringComparison.OrdinalIgnoreCase));
            foreach (EntityRow resource in resources)
                foreach ((EntityRow? tool, int toolCost, string? toolReason) in tools)
                {
                    (WorkMode? mode, string? removed) = BuildMode(item, row, resource, tool, tooling, toolingRule, cycleSeconds, false);
                    if (mode == null)
                    {
                        removedBy[removed ?? "rules"] = removedBy.GetValueOrDefault(removed ?? "rules") + 1;
                        continue;
                    }
                    if (allowedResources.Count > 0 && !allowedResources.Contains(resource.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        removedBy["the emergency re-plan's alternatives"] = removedBy.GetValueOrDefault("the emergency re-plan's alternatives") + 1;
                        continue;
                    }
                    if (pin?.ResourceKey != null && !string.Equals(pin.ResourceKey, resource.Key, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (toolCost > 0)
                    {
                        mode.Cost += toolCost;
                        mode.CostReasons.Add(toolReason!);
                    }
                    item.Modes.Add(mode);
                }
            if (pin != null)
            {
                item.PinResourceKey = pin.ResourceKey;
                if (pin.Start.HasValue)
                    item.PinStartBucket = Math.Max(0, Problem.ToBucket(pin.Start.Value));
                item.Required = true;
            }
            if (item.Modes.Count == 0)
            {
                if (pin?.ResourceKey != null)
                    item.NoModeReason = $"The rules don't allow it on {pin.ResourceKey}";
                else
                    item.NoModeReason = "No " + resourceEntity.Name + " is allowed: " + string.Join("; ", removedBy.OrderByDescending(x => x.Value).Select(x => $"{x.Key} took out {x.Value}"));
                Problem.RuleNames["item:" + item.Key] = $"{item.Key} must be planned";
            }
        }

        // One mode, or null and the name of the rule that took it out, ignoreRules keeps it for locked work
        private (WorkMode? mode, string? removedBy) BuildMode(WorkItem item, EntityRow row, EntityRow resource, EntityRow? tool, EntityModel? tooling, RuleModel? toolingRule, double cycleSeconds, bool ignoreRules)
        {
            Func<string, object?> scope = Scope(row, resource, tool);
            WorkMode mode = new WorkMode { ResourceKey = resource.Key, CycleSeconds = cycleSeconds };
            if (tool != null && tooling != null && toolingRule != null)
            {
                mode.PoolKey = PoolKey(tooling, tool.Key);
                WorkPool? pool = Problem.Pools.FirstOrDefault(x => x.Key == mode.PoolKey);
                int copies = pool?.Copies ?? 1;
                mode.MinCount = Math.Max(1, ParamInt(toolingRule, "minCount", scope, 1));
                mode.MaxCount = Math.Min(copies, Math.Max(mode.MinCount, ParamInt(toolingRule, "maxCount", scope, mode.MinCount)));
                mode.UnitsPerCycle = Math.Max(1, ParamInt(toolingRule, "unitsPerCycle", scope, 1));
                string? cycleText = toolingRule.GetParam("cycleSeconds");
                if (cycleText != null && GetFormula(toolingRule, "cycleSeconds", cycleText) is Formula cycleFormula && SafeNumber(toolingRule, cycleFormula, scope) is double toolCycle && toolCycle > 0)
                    mode.CycleSeconds = toolCycle;
                if (!ignoreRules && mode.MaxCount < mode.MinCount)
                    return (null, $"{toolingRule.Name} (not enough copies)");
            }

            // eligibility, hard rules take the mode out, soft ones cost
            foreach (RuleModel rule in RulesOf("RunsOn").Concat(RulesOf("Condition")).Concat(RulesOf("Allowed")))
            {
                if (!Applies(rule, row))
                    continue;
                (bool allowed, int cost, string? label) = Judge(rule, scope, row, resource, tool);
                if (!allowed)
                {
                    if (rule.IsHard)
                    {
                        if (!ignoreRules)
                        {
                            GetStat(rule).ModesRemoved++;
                            return (null, rule.Name);
                        }
                    }
                    else
                    {
                        mode.Cost += Math.Max(1, rule.Weight);
                        mode.CostReasons.Add(rule.Name);
                    }
                }
                else if (cost > 0)
                {
                    mode.Cost += cost * Math.Max(1, rule.Weight);
                    mode.CostReasons.Add($"{rule.Name}: {label}");
                }
            }

            foreach (RuleModel rule in RulesOf("Setup").Where(x => Applies(x, row)))
                mode.SetupMinutes += ParamInt(rule, "minutes", scope);

            // what it takes of each capacity dimension, and fewer copies when they wouldn't fit
            WorkResource? target = Problem.Resources.FirstOrDefault(x => x.Key == resource.Key);
            foreach (RuleModel rule in RulesOf("Capacity").Where(x => Applies(x, row)))
            {
                int demand = ParamInt(rule, "demand", scope, 1);
                bool perItem = string.Equals(rule.GetParam("per"), "item", StringComparison.OrdinalIgnoreCase) || tool == null;
                string dimension = "cap:" + rule.Id;
                mode.Demands.Add(new DimensionDemand { Dimension = dimension, PerCount = perItem ? 0 : demand, Fixed = perItem ? demand : 0 });
                if (!ignoreRules && target != null && !Fits(mode, target, dimension, rule))
                    return (null, $"{rule.Name} (no room)");
            }
            foreach (RuleModel rule in RulesOf("CapacityMatrix").Where(x => Applies(x, row)))
            {
                string? keyText = rule.GetParam("key");
                string? key = keyText != null && GetFormula(rule, "key", keyText) is Formula keyFormula ? Formula.ToText(SafeEvaluate(rule, keyFormula, scope))?.Trim() : null;
                if (string.IsNullOrEmpty(key))
                    continue;
                bool perItem = string.Equals(rule.GetParam("per"), "item", StringComparison.OrdinalIgnoreCase) || tool == null;
                string dimension = $"mat:{rule.Id}:{key}";
                int demand = Math.Max(1, ParamInt(rule, "demand", scope, 1));
                mode.Demands.Add(new DimensionDemand { Dimension = dimension, PerCount = perItem ? 0 : demand, Fixed = perItem ? demand : 0 });
                if (target != null && !target.Capacities.ContainsKey(dimension))
                    target.Capacities[dimension] = 0;
                if (!ignoreRules && target != null && !Fits(mode, target, dimension, rule))
                    return (null, $"{rule.Name} (none of size {key})");
            }
            foreach (RuleModel rule in RulesOf("Configurations").Where(x => Applies(x, row)))
            {
                if (target == null || !_configured.TryGetValue(rule.Id, out HashSet<string>? configured) || !configured.Contains(target.Key))
                    continue;
                string? keyText = rule.GetParam("key");
                string? key = keyText != null && GetFormula(rule, "key", keyText) is Formula keyFormula ? Formula.ToText(SafeEvaluate(rule, keyFormula, scope))?.Trim() : null;
                if (string.IsNullOrEmpty(key))
                    continue;
                bool perItem = string.Equals(rule.GetParam("per"), "item", StringComparison.OrdinalIgnoreCase) || tool == null;
                string dimension = $"cfg:{rule.Id}:{key}";
                int demand = Math.Max(1, ParamInt(rule, "demand", scope, 1));
                mode.Demands.Add(new DimensionDemand { Dimension = dimension, PerCount = perItem ? 0 : demand, Fixed = perItem ? demand : 0 });
                if (!target.Capacities.ContainsKey(dimension))
                    target.Capacities[dimension] = 0;
                if (!ignoreRules && !Fits(mode, target, dimension, rule))
                    return (null, $"{rule.Name} (no combination takes size {key})");
            }
            return (mode, null);
        }
        // Whether the least the mode can use fits, and the most copies cut down to what fits
        private static bool Fits(WorkMode mode, WorkResource resource, string dimension, RuleModel rule)
        {
            int capacity = resource.Capacities.GetValueOrDefault(dimension);
            DimensionDemand demand = mode.Demands.Last(x => x.Dimension == dimension);
            if (demand.Fixed + demand.PerCount * mode.MinCount > capacity)
                return false;
            if (demand.PerCount > 0)
                mode.MaxCount = Math.Max(mode.MinCount, Math.Min(mode.MaxCount, (capacity - demand.Fixed) / demand.PerCount));
            return true;
        }

        // An eligibility rule's verdict on one combination
        private (bool allowed, int cost, string? label) Judge(RuleModel rule, Func<string, object?> scope, EntityRow item, EntityRow resource, EntityRow? tool)
        {
            switch (rule.RuleTypeId)
            {
                case "RunsOn":
                {
                    string? when = rule.GetParam("when");
                    if (when == null)
                        return (true, 0, null);
                    Formula? formula = GetFormula(rule, "when", when);
                    return (formula == null || SafeBool(rule, formula, scope), 0, null);
                }
                case "Condition":
                {
                    string? condition = rule.GetParam("condition");
                    Formula? formula = condition == null ? null : GetFormula(rule, "condition", condition);
                    return (formula == null || SafeBool(rule, formula, scope), 0, null);
                }
                case "Allowed":
                {
                    RelationshipModel? relationship = _model.GetRelationship(rule.GetParam("relationshipId"));
                    if (relationship == null)
                    {
                        Warn(rule, "Needs a relationship");
                        return (true, 0, null);
                    }
                    string? a = Bind(rule, "a", relationship.EntityA, scope, item, resource, tool);
                    string? b = Bind(rule, "b", relationship.EntityB, scope, item, resource, tool);
                    // the rule doesn't apply when a side has nothing to look up, e.g. a mode with no tool
                    if (a == null || b == null)
                        return (true, 0, null);
                    ScaleValueModel? value = _model.GetScale(relationship.ValueScaleId)?.Get(relationship.Get(a, b));
                    if (value == null)
                        return (true, 0, null);
                    if (value.Meaning == "Forbidden")
                        return (false, 0, value.Label);
                    return (true, value.Meaning == "Penalty" ? value.Weight : 0, value.Label);
                }
            }
            return (true, 0, null);
        }
        // The key a side of a relationship is looked up with, from the rule's binding or worked out from the entity types
        private string? Bind(RuleModel rule, string side, string entityName, Func<string, object?> scope, EntityRow item, EntityRow resource, EntityRow? tool)
        {
            string? text = rule.GetParam(side);
            if (text != null)
            {
                Formula? formula = GetFormula(rule, side, text);
                return formula == null ? null : Formula.ToText(SafeEvaluate(rule, formula, scope))?.Trim();
            }
            if (string.Equals(entityName, item.Entity.Name, StringComparison.OrdinalIgnoreCase))
                return item.Key;
            if (string.Equals(entityName, resource.Entity.Name, StringComparison.OrdinalIgnoreCase))
                return resource.Key;
            if (tool != null && string.Equals(entityName, tool.Entity.Name, StringComparison.OrdinalIgnoreCase))
                return tool.Key;
            if (tool == null && _result.ToolingEntity != null && string.Equals(entityName, _result.ToolingEntity, StringComparison.OrdinalIgnoreCase))
                return null;
            // an item attribute that references the entity, e.g. job.compound for Compound
            AttributeModel? reference = item.Entity.Attributes.FirstOrDefault(x => string.Equals(x.ReferencesEntity, entityName, StringComparison.OrdinalIgnoreCase));
            if (reference != null)
                return Formula.ToText(item.Values.GetValueOrDefault(reference.Name))?.Trim();
            Warn(rule, $"Can't tell how to look up {entityName}, set the {side} side, e.g. item.{entityName.ToLowerInvariant()}");
            return null;
        }

        // Locked work stays where it is, the rest of the item is what's left to plan
        private void BuildLocked(Dictionary<string, (WorkItem item, EntityRow row)> items, EntityModel resourceEntity)
        {
            foreach (LockedBooking locked in _context.Locked)
            {
                FixedBooking booking = new FixedBooking
                {
                    ItemKey = locked.ItemKey,
                    ResourceKey = locked.ResourceKey,
                    PoolKey = locked.PoolKey,
                    Count = Math.Max(1, locked.Count),
                    StartBucket = Problem.ToBucket(locked.Start),
                    EndBucket = Problem.ToBucketCeiling(locked.End),
                    Quantity = locked.Quantity
                };
                if (items.TryGetValue(locked.ItemKey, out (WorkItem item, EntityRow row) found))
                {
                    found.item.Quantity = Math.Max(0, found.item.Quantity - locked.Quantity);
                    WorkMode? mode = found.item.Modes.FirstOrDefault(x => string.Equals(x.ResourceKey, locked.ResourceKey, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(x.PoolKey ?? "", locked.PoolKey ?? "", StringComparison.OrdinalIgnoreCase));
                    if (mode == null && _rows[resourceEntity.Name].TryGetValue(locked.ResourceKey, out EntityRow? resource))
                    {
                        // the rules may have changed since it was locked, it's worked out again ignoring them
                        RuleModel? toolingRule = RulesOf("UsesTooling").FirstOrDefault(x => Applies(x, found.row));
                        EntityModel? tooling = toolingRule == null ? null : _model.GetEntity(toolingRule.GetParam("tooling"));
                        EntityRow? tool = null;
                        if (tooling != null && locked.PoolKey != null)
                            tool = _rows[tooling.Name].Values.FirstOrDefault(x => PoolKey(tooling, x.Key) == locked.PoolKey);
                        mode = BuildMode(found.item, found.row, resource, tool, tooling, toolingRule, 60, true).mode;
                    }
                    if (mode != null)
                        booking.Demands = mode.Demands;
                }
                Problem.Fixed.Add(booking);
            }
        }

        // Pair rules, worked out on the values so items with the same value are only compared once
        private void BuildPairs(Dictionary<string, (WorkItem item, EntityRow row)> items)
        {
            Dictionary<string, Separation> pairs = new Dictionary<string, Separation>();
            void AddPair(string a, string b, int gap, string ruleId)
            {
                if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                    return;
                string key = string.CompareOrdinal(a, b) < 0 ? a + "\u0001" + b : b + "\u0001" + a;
                if (pairs.TryGetValue(key, out Separation? existing))
                {
                    if (gap > existing.GapBuckets)
                    {
                        existing.GapBuckets = gap;
                        existing.RuleId = ruleId;
                    }
                    return;
                }
                pairs[key] = new Separation { ItemA = a, ItemB = b, GapBuckets = gap, RuleId = ruleId };
            }

            foreach (RuleModel rule in RulesOf("Compatible").Concat(RulesOf("MustMatch")).Concat(RulesOf("Changeover")))
            {
                string? attributeText = rule.GetParam("attribute");
                if (attributeText == null)
                {
                    Warn(rule, "Needs the attribute to compare, e.g. item.compound");
                    continue;
                }
                if (!attributeText.Contains('.') && !attributeText.Contains('('))
                    attributeText = "item." + attributeText;
                Formula? attribute = GetFormula(rule, "attribute", attributeText);
                if (attribute == null)
                    continue;
                // items grouped by their value
                Dictionary<string, List<string>> groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, object?> raw = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach ((WorkItem item, EntityRow row) in items.Values)
                {
                    if (!Applies(rule, row))
                        continue;
                    object? value = SafeEvaluate(rule, attribute, Scope(row, null, null));
                    string text = Formula.ToText(value)?.Trim() ?? "";
                    if (!groups.TryGetValue(text, out List<string>? members))
                    {
                        members = new List<string>();
                        groups[text] = members;
                        raw[text] = value;
                    }
                    members.Add(item.Key);
                }
                RelationshipModel? relationship = _model.GetRelationship(rule.GetParam("relationshipId"));
                ValueScaleModel? scale = relationship == null ? null : _model.GetScale(relationship.ValueScaleId);
                double tolerance = Formula.ToNumber(rule.GetParam("tolerance")) ?? 0;
                int defaultGap = Problem.MinutesToBuckets(Formula.ToNumber(rule.GetParam("minutes")) ?? 0);
                List<string> values = groups.Keys.ToList();
                for (int i = 0; i < values.Count; i++)
                    for (int j = i; j < values.Count; j++)
                    {
                        string a = values[i], b = values[j];
                        int? gap = null;
                        switch (rule.RuleTypeId)
                        {
                            case "Compatible":
                                if (relationship == null)
                                {
                                    Warn(rule, "Needs a relationship of which pairs are allowed together");
                                    break;
                                }
                                ScaleValueModel? meaning = scale?.Get(relationship.Get(a, b));
                                if (meaning != null && meaning.Meaning == "Forbidden")
                                    gap = Problem.MinutesToBuckets(Formula.ToNumber(rule.GetParam("gapMinutes")) ?? 0);
                                break;
                            case "MustMatch":
                                if (i == j)
                                    break;
                                double? x = Formula.ToNumber(raw[a]), y = Formula.ToNumber(raw[b]);
                                bool differ = x.HasValue && y.HasValue ? Math.Abs(x.Value - y.Value) > tolerance + 1e-9 : !Formula.AreEqual(raw[a], raw[b]);
                                if (differ)
                                    gap = 0;
                                break;
                            case "Changeover":
                                if (i == j)
                                    break;
                                double? minutes = relationship == null ? null : Formula.ToNumber(relationship.Get(a, b) ?? (relationship.Symmetric ? null : relationship.Get(b, a)));
                                gap = minutes.HasValue ? Problem.MinutesToBuckets(minutes.Value) : defaultGap;
                                break;
                        }
                        if (!gap.HasValue)
                            continue;
                        foreach (string itemA in groups[a])
                            foreach (string itemB in groups[b])
                                AddPair(itemA, itemB, gap.Value, rule.Id);
                    }
            }
            Problem.Separations.AddRange(pairs.Values);
            if (pairs.Count > 20000)
                Warn(null, $"{pairs.Count} pairs of items have to be kept apart, the solve may be slow, a longer time unit or fewer pair rules will help");
        }

        // Item after item, from an attribute holding the other's key, or by sequence within a group
        private void BuildPrecedences(Dictionary<string, (WorkItem item, EntityRow row)> items)
        {
            foreach (RuleModel rule in RulesOf("Precedence"))
            {
                int gap = Problem.MinutesToBuckets(Formula.ToNumber(rule.GetParam("gapMinutes")) ?? 0);
                string? predecessorText = rule.GetParam("predecessor");
                if (predecessorText != null && GetFormula(rule, "predecessor", predecessorText) is Formula predecessor)
                    foreach ((WorkItem item, EntityRow row) in items.Values.Where(x => Applies(rule, x.row)))
                    {
                        string? before = Formula.ToText(SafeEvaluate(rule, predecessor, Scope(row, null, null)))?.Trim();
                        if (string.IsNullOrEmpty(before))
                            continue;
                        if (!items.ContainsKey(before))
                        {
                            Info(rule, $"{item.Key} comes after {before}, which isn't being planned", item.Key);
                            continue;
                        }
                        Problem.Precedences.Add(new Precedence { Before = before, After = item.Key, GapBuckets = gap, RuleId = rule.Id });
                    }
                string? groupText = rule.GetParam("group");
                string? sequenceText = rule.GetParam("sequence");
                if (groupText != null && sequenceText != null && GetFormula(rule, "group", groupText) is Formula group && GetFormula(rule, "sequence", sequenceText) is Formula sequence)
                {
                    List<(string key, string group, double sequence)> members = new List<(string, string, double)>();
                    foreach ((WorkItem item, EntityRow row) in items.Values.Where(x => Applies(rule, x.row)))
                    {
                        string groupKey = Formula.ToText(SafeEvaluate(rule, group, Scope(row, null, null)))?.Trim() ?? "";
                        if (groupKey != "")
                            members.Add((item.Key, groupKey, SafeNumber(rule, sequence, Scope(row, null, null)) ?? 0));
                    }
                    foreach (IGrouping<string, (string key, string group, double sequence)> chain in members.GroupBy(x => x.group, StringComparer.OrdinalIgnoreCase))
                    {
                        List<(string key, string group, double sequence)> ordered = chain.OrderBy(x => x.sequence).ToList();
                        for (int i = 1; i < ordered.Count; i++)
                        {
                            // the last of the lower sequence before the next one up, so equal sequences can run side by side
                            int previous = i - 1;
                            while (previous >= 0 && ordered[previous].sequence == ordered[i].sequence)
                                previous--;
                            if (previous < 0)
                                continue;
                            double lower = ordered[previous].sequence;
                            foreach ((string key, string group, double sequence) before in ordered.Where(x => x.sequence == lower))
                                Problem.Precedences.Add(new Precedence { Before = before.key, After = ordered[i].key, GapBuckets = gap, RuleId = rule.Id });
                        }
                    }
                }
            }
        }

        private void BuildSharedLimits(Dictionary<string, (WorkItem item, EntityRow row)> items)
        {
            foreach (RuleModel rule in RulesOf("SharedLimit"))
            {
                SharedLimit limit = new SharedLimit { RuleId = rule.Id, Name = rule.Name, Capacity = Math.Max(0, ParamIntConst(rule, "capacity", 1)) };
                limit.ItemKeys = items.Values.Where(x => Applies(rule, x.row)).Select(x => x.item.Key).ToList();
                if (limit.ItemKeys.Count > limit.Capacity)
                    Problem.SharedLimits.Add(limit);
            }
        }

        private void BuildBaseline()
        {
            foreach (KeyValuePair<string, BaselineTime> placed in _context.Baseline)
                Problem.Baseline[placed.Key] = new BaselinePlacement { ResourceKey = placed.Value.ResourceKey, StartBucket = Problem.ToBucket(placed.Value.Start) };
        }

        // Names in expressions: item.x, resource.x, tooling.x (or tool.x), a bare name is the item's,
        // .key is the row's key and a name past an attribute that references an entity reads that entity, e.g. item.compound.family
        private Func<string, object?> Scope(EntityRow? item, EntityRow? resource, EntityRow? tooling)
        {
            return name =>
            {
                string[] parts = name.Split('.');
                EntityRow? row;
                int at = 1;
                switch (parts[0].ToLowerInvariant())
                {
                    case "item":
                    case "job":
                        row = item;
                        break;
                    case "resource":
                        row = resource;
                        break;
                    case "tooling":
                    case "tool":
                        row = tooling;
                        break;
                    default:
                        row = item;
                        at = 0;
                        break;
                }
                if (row == null)
                    return null;
                return Follow(row, parts, at);
            };
        }
        private object? Follow(EntityRow row, string[] parts, int at)
        {
            if (at >= parts.Length)
                return row.Key;
            string name = parts[at];
            AttributeModel? attribute = row.Entity.GetAttribute(name);
            if (attribute == null)
            {
                if (name.Equals("key", StringComparison.OrdinalIgnoreCase) || name.Equals("id", StringComparison.OrdinalIgnoreCase))
                    return row.Key;
                if (name.Equals("label", StringComparison.OrdinalIgnoreCase) || name.Equals("name", StringComparison.OrdinalIgnoreCase))
                    return row.Label;
                throw new Exception($"{row.Entity.Name} has no attribute {name}");
            }
            object? value = row.Values.GetValueOrDefault(attribute.Name);
            if (at + 1 >= parts.Length)
                return value;
            EntityModel? referenced = _model.GetEntity(attribute.ReferencesEntity);
            if (referenced == null)
                throw new Exception($"{row.Entity.Name}.{attribute.Name} doesn't reference another entity");
            string? key = Formula.ToText(value)?.Trim();
            if (key == null || !_rows[referenced.Name].TryGetValue(key, out EntityRow? next))
                return null;
            return Follow(next, parts, at + 1);
        }

        private Formula? GetFormula(RuleModel? rule, string name, string text)
        {
            string cacheKey = (rule?.Id ?? "") + "\u0001" + name + "\u0001" + text;
            if (_formulas.TryGetValue(cacheKey, out Formula? cached))
                return cached;
            Formula? formula = null;
            if (Formula.TryParse(text, out Formula? parsed, out string error))
                formula = parsed;
            else
                Error(rule, $"{name}: {error}");
            _formulas[cacheKey] = formula;
            return formula;
        }
        private object? SafeEvaluate(RuleModel? rule, Formula formula, Func<string, object?> scope)
        {
            try
            {
                return formula.Evaluate(scope);
            }
            catch (Exception ex)
            {
                Warn(rule, $"{formula.Text}: {ex.Message}");
                return null;
            }
        }
        private double? SafeNumber(RuleModel? rule, Formula formula, Func<string, object?> scope)
        {
            return Formula.ToNumber(SafeEvaluate(rule, formula, scope));
        }
        private bool SafeBool(RuleModel? rule, Formula formula, Func<string, object?> scope)
        {
            return Formula.ToBool(SafeEvaluate(rule, formula, scope));
        }
        // A whole number parameter, a number or an expression, rounded up
        private int ParamInt(RuleModel rule, string name, Func<string, object?> scope, int fallback = 0)
        {
            string? text = rule.GetParam(name);
            if (text == null)
                return fallback;
            Formula? formula = GetFormula(rule, name, text);
            double? value = formula == null ? null : SafeNumber(rule, formula, scope);
            return value.HasValue ? (int)Math.Ceiling(value.Value - 1e-9) : fallback;
        }
        private int ParamIntConst(RuleModel rule, string name, int fallback)
        {
            return ParamInt(rule, name, _ => null, fallback);
        }
        private static DateTime? ToDate(object? value)
        {
            switch (value)
            {
                case DateTime date:
                    return date;
                case DateTimeOffset offset:
                    return offset.LocalDateTime;
                case string text when DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed):
                    return parsed;
                default:
                    return null;
            }
        }

        private void Error(RuleModel? rule, string message)
        {
            Add("error", rule, message, null);
        }
        private void Warn(RuleModel? rule, string message, string? itemKey = null)
        {
            Add("warning", rule, message, itemKey);
        }
        private void Info(RuleModel? rule, string message, string? itemKey = null)
        {
            Add("info", rule, message, itemKey);
        }
        // Each message once, a bad expression in a rule would otherwise be reported for every item
        private void Add(string level, RuleModel? rule, string message, string? itemKey)
        {
            if (!_reported.Add(level + (rule?.Id ?? "") + message))
                return;
            if (_result.Diagnostics.Count >= 200)
                return;
            _result.Diagnostics.Add(new Diagnostic { Level = level, RuleId = rule?.Id, RuleName = rule?.Name, ItemKey = itemKey, Message = message });
        }
    }
}
