using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scheduler.Solver.Rules
{
    // A profile as the rule engine sees it: its entity types with their rows, value scales, relationships, calendars and rules
    // the API fills it from SchedulerMeta and the sources, the tests fill it by hand
    public class ProfileModel
    {
        public string ProfileId { get; set; } = "";
        public string Name { get; set; } = "";
        public int TimeUnitMinutes { get; set; } = 15;
        public List<EntityModel> Entities { get; set; } = new List<EntityModel>();
        public List<ValueScaleModel> ValueScales { get; set; } = new List<ValueScaleModel>();
        public List<RelationshipModel> Relationships { get; set; } = new List<RelationshipModel>();
        public List<CalendarModel> Calendars { get; set; } = new List<CalendarModel>();
        public List<RuleModel> Rules { get; set; } = new List<RuleModel>();

        public EntityModel? GetEntity(string? name)
        {
            return string.IsNullOrWhiteSpace(name) ? null : Entities.FirstOrDefault(x => string.Equals(x.Name, name.Trim(), StringComparison.OrdinalIgnoreCase) || x.Id == name);
        }
        public RelationshipModel? GetRelationship(string? idOrName)
        {
            return string.IsNullOrWhiteSpace(idOrName) ? null : Relationships.FirstOrDefault(x => x.Id == idOrName || string.Equals(x.Name, idOrName, StringComparison.OrdinalIgnoreCase));
        }
        public ValueScaleModel? GetScale(string? id)
        {
            return ValueScales.FirstOrDefault(x => x.Id == id);
        }
    }

    public class EntityModel
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        // Item, Resource or Reference
        public string Kind { get; set; } = "Item";
        public string? DurationFormula { get; set; }
        public List<AttributeModel> Attributes { get; set; } = new List<AttributeModel>();
        // One dictionary per row, attribute name to value
        public List<Dictionary<string, object?>> Rows { get; set; } = new List<Dictionary<string, object?>>();

        public AttributeModel? GetAttribute(string? name)
        {
            return string.IsNullOrWhiteSpace(name) ? null : Attributes.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        public AttributeModel? GetByRole(string role)
        {
            return Attributes.FirstOrDefault(x => string.Equals(x.Role, role, StringComparison.OrdinalIgnoreCase));
        }
    }

    public class AttributeModel
    {
        public string Name { get; set; } = "";
        public string DataType { get; set; } = "STRING";
        public string Role { get; set; } = "Custom";
        // The entity type this attribute's value is the key of, e.g. job.compound references Compound
        public string? ReferencesEntity { get; set; }
    }

    public class ValueScaleModel
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public List<ScaleValueModel> Values { get; set; } = new List<ScaleValueModel>();

        public ScaleValueModel? Get(string? code)
        {
            return code == null ? null : Values.FirstOrDefault(x => string.Equals(x.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));
        }
    }

    public class ScaleValueModel
    {
        public string Code { get; set; } = "";
        public string Label { get; set; } = "";
        // Forbidden, Allowed, Penalty or Numeric
        public string Meaning { get; set; } = "Allowed";
        public int Weight { get; set; }
    }

    // A matrix or list of values between two entity types, e.g. Compound x Press graded Preferred to Not allowed
    public class RelationshipModel
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string EntityA { get; set; } = "";
        public string EntityB { get; set; } = "";
        public string ValueScaleId { get; set; } = "";
        public string? DefaultValue { get; set; }
        public bool Symmetric { get; set; }
        public List<RelationshipValueModel> Values { get; set; } = new List<RelationshipValueModel>();

        private Dictionary<string, string>? _lookup;
        // The value for a pair, the other way round too when it's symmetric, then the default
        public string? Get(string? a, string? b)
        {
            if (_lookup == null)
            {
                _lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (RelationshipValueModel value in Values)
                    _lookup[value.A + "\u0001" + value.B] = value.Value;
            }
            if (a == null || b == null)
                return DefaultValue;
            if (_lookup.TryGetValue(a + "\u0001" + b, out string? found))
                return found;
            if (Symmetric && _lookup.TryGetValue(b + "\u0001" + a, out found))
                return found;
            return DefaultValue;
        }
        // Every B with a value for this A, e.g. a job's alternative tools
        public IEnumerable<(string b, string value)> GetForA(string a)
        {
            foreach (RelationshipValueModel value in Values)
                if (string.Equals(value.A, a, StringComparison.OrdinalIgnoreCase))
                    yield return (value.B, value.Value);
        }
    }

    public class RelationshipValueModel
    {
        public string A { get; set; } = "";
        public string B { get; set; } = "";
        public string Value { get; set; } = "";
    }

    // Working time, a calendar takes its parent's shifts when it has none of its own, and its parent's exceptions as well as its own
    public class CalendarModel
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string? ParentId { get; set; }
        public string? ResourceEntity { get; set; }
        public string? ResourceKey { get; set; }
        public List<CalendarShiftModel> Shifts { get; set; } = new List<CalendarShiftModel>();
        public List<CalendarExceptionModel> Exceptions { get; set; } = new List<CalendarExceptionModel>();
    }

    public class CalendarShiftModel
    {
        // 0 Sunday to 6 Saturday
        public int DayOfWeek { get; set; }
        // HH:mm or HH:mm:ss, an end before the start runs past midnight
        public string StartTime { get; set; } = "00:00";
        public string EndTime { get; set; } = "00:00";
    }

    public class CalendarExceptionModel
    {
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        // Down or Available
        public string Kind { get; set; } = "Down";
        public string? Reason { get; set; }
    }

    public class RuleModel
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string RuleTypeId { get; set; } = "";
        public bool Enabled { get; set; } = true;
        // Hard or Soft
        public string Strength { get; set; } = "Hard";
        public int Weight { get; set; }
        // { who : { entity, when }, params : { ... } } as the rule builder saves it
        public JsonElement Blocks { get; set; }

        [JsonIgnore]
        public bool IsHard => !string.Equals(Strength, "Soft", StringComparison.OrdinalIgnoreCase);

        public string? GetParam(string name)
        {
            if (Blocks.ValueKind != JsonValueKind.Object)
                return null;
            JsonElement holder = Blocks.TryGetProperty("params", out JsonElement found) && found.ValueKind == JsonValueKind.Object ? found : Blocks;
            if (!holder.TryGetProperty(name, out JsonElement value))
                return null;
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    string text = value.GetString() ?? "";
                    return text.Trim() == "" ? null : text;
                case JsonValueKind.Number:
                    return value.GetRawText();
                case JsonValueKind.True:
                    return "true";
                case JsonValueKind.False:
                    return "false";
                default:
                    return null;
            }
        }
        // The entity type the rule is for, from who.entity
        public string? GetWhoEntity()
        {
            if (Blocks.ValueKind == JsonValueKind.Object && Blocks.TryGetProperty("who", out JsonElement who) && who.ValueKind == JsonValueKind.Object
                && who.TryGetProperty("entity", out JsonElement entity) && entity.ValueKind == JsonValueKind.String)
                return entity.GetString();
            return null;
        }
        // Which of them, a condition such as item.compound = 'SIL-50', blank for all
        public string? GetWhoWhen()
        {
            if (Blocks.ValueKind == JsonValueKind.Object && Blocks.TryGetProperty("who", out JsonElement who) && who.ValueKind == JsonValueKind.Object
                && who.TryGetProperty("when", out JsonElement when) && when.ValueKind == JsonValueKind.String)
            {
                string text = when.GetString() ?? "";
                return text.Trim() == "" ? null : text;
            }
            return null;
        }
    }

    // What one run is asked on top of the profile: the horizon, what's locked, what's pinned and what's broken down
    public class RunContext
    {
        public DateTime HorizonStart { get; set; } = DateTime.Today;
        public int HorizonDays { get; set; } = 28;
        public bool AllowUnscheduled { get; set; } = true;
        public ObjectiveWeights? Weights { get; set; }
        // What-if moves: this item on this resource, at this time when given
        public List<ItemPin> Pins { get; set; } = new List<ItemPin>();
        // Breakdowns and other stops, the resource can't be used between From and To
        public List<ResourceDown> Downs { get; set; } = new List<ResourceDown>();
        // Emergency re-plans: these items may only go to these resources
        public Dictionary<string, List<string>> RestrictTo { get; set; } = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        // Locked work from the published plan
        public List<LockedBooking> Locked { get; set; } = new List<LockedBooking>();
        // Where each item is in the published plan, for the stability goal
        public Dictionary<string, BaselineTime> Baseline { get; set; } = new Dictionary<string, BaselineTime>(StringComparer.OrdinalIgnoreCase);
        // Items finished or otherwise not to plan
        public HashSet<string> Exclude { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public class ItemPin
    {
        public string ItemKey { get; set; } = "";
        public string? ResourceKey { get; set; }
        public DateTime? Start { get; set; }
    }

    public class ResourceDown
    {
        public string ResourceKey { get; set; } = "";
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public string? Reason { get; set; }
    }

    public class LockedBooking
    {
        public string ItemKey { get; set; } = "";
        public string ResourceKey { get; set; } = "";
        public string? PoolKey { get; set; }
        public int Count { get; set; } = 1;
        public DateTime Start { get; set; }
        public DateTime End { get; set; }
        public long Quantity { get; set; }
    }

    public class BaselineTime
    {
        public string ResourceKey { get; set; } = "";
        public DateTime Start { get; set; }
    }
}
