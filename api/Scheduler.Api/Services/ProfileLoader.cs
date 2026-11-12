using Scheduler.Solver.Rules;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Scheduler.Api.Services
{
    // Fills the rule engine's ProfileModel: the set up from SchedulerMeta and the rows from the sources, read only
    // relationship values come live from their source when they have one, values typed into the matrix win over the source's
    public class ProfileLoader
    {
        // Most rows read from one source
        public const int MaxRows = 200000;

        private readonly string _dbConnSettings;
        private readonly ISourceSecrets _secrets;
        private readonly Dictionary<string, JsonObject> _read = new Dictionary<string, JsonObject>();
        private readonly Dictionary<string, SourceInfo> _sources = new Dictionary<string, SourceInfo>(StringComparer.OrdinalIgnoreCase);

        public List<string> Warnings { get; } = new List<string>();
        // The profile's settings json
        public JsonElement Settings { get; private set; }

        public ProfileLoader(string dbConnSettings, ISourceSecrets secrets)
        {
            _dbConnSettings = dbConnSettings;
            _secrets = secrets;
        }

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        // What GetProfileModel gives back, on top of what the rule engine takes
        private class ModelJson
        {
            public string ProfileId { get; set; } = "";
            public string Name { get; set; } = "";
            public int TimeUnitMinutes { get; set; } = 15;
            public JsonElement Settings { get; set; }
            public List<EntityJson> Entities { get; set; } = new List<EntityJson>();
            public List<ValueScaleModel> ValueScales { get; set; } = new List<ValueScaleModel>();
            public List<RelationshipJson> Relationships { get; set; } = new List<RelationshipJson>();
            public List<CalendarModel> Calendars { get; set; } = new List<CalendarModel>();
            public List<RuleModel> Rules { get; set; } = new List<RuleModel>();
            public List<SourceInfo> Sources { get; set; } = new List<SourceInfo>();
        }
        private class EntityJson
        {
            public string Id { get; set; } = "";
            public string Name { get; set; } = "";
            public string Kind { get; set; } = "Item";
            public string? DurationFormula { get; set; }
            public string? SourceId { get; set; }
            public List<AttributeJson> Attributes { get; set; } = new List<AttributeJson>();
        }
        private class AttributeJson
        {
            public string Name { get; set; } = "";
            public string DataType { get; set; } = "STRING";
            public string Role { get; set; } = "Custom";
            public string? SourceColumn { get; set; }
            public string? ReferencesEntity { get; set; }
        }
        private class RelationshipJson
        {
            public string Id { get; set; } = "";
            public string Name { get; set; } = "";
            public string EntityA { get; set; } = "";
            public string EntityB { get; set; } = "";
            public string ValueScaleId { get; set; } = "";
            public string? DefaultValue { get; set; }
            public bool Symmetric { get; set; }
            public string? SourceId { get; set; }
            public string? SourceAColumn { get; set; }
            public string? SourceBColumn { get; set; }
            public string? SourceValueColumn { get; set; }
            public List<RelationshipValueModel> Values { get; set; } = new List<RelationshipValueModel>();
        }
        public class SourceInfo
        {
            public string Id { get; set; } = "";
            public string ConnectionId { get; set; } = "";
            public string Kind { get; set; } = "table";
            public string? Schema { get; set; }
            public string? ObjectName { get; set; }
            public string? QueryText { get; set; }
        }

        public async Task<ProfileModel> Load(string profileId)
        {
            string json = SchedulerDb.Run(_dbConnSettings, "GetProfileModel", profileId) ?? "{}";
            ModelJson source = JsonSerializer.Deserialize<ModelJson>(json, JsonOptions) ?? new ModelJson();
            Settings = source.Settings.ValueKind == JsonValueKind.Object ? source.Settings.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
            foreach (SourceInfo info in source.Sources)
                _sources[info.Id] = info;

            ProfileModel model = new ProfileModel
            {
                ProfileId = source.ProfileId,
                Name = source.Name,
                TimeUnitMinutes = source.TimeUnitMinutes,
                ValueScales = source.ValueScales,
                Calendars = source.Calendars,
                Rules = source.Rules.Where(x => x.Enabled).ToList()
            };
            foreach (EntityJson entity in source.Entities)
            {
                EntityModel built = new EntityModel { Id = entity.Id, Name = entity.Name, Kind = entity.Kind, DurationFormula = entity.DurationFormula };
                foreach (AttributeJson attribute in entity.Attributes)
                    built.Attributes.Add(new AttributeModel { Name = attribute.Name, DataType = attribute.DataType, Role = attribute.Role, ReferencesEntity = attribute.ReferencesEntity });
                if (!string.IsNullOrEmpty(entity.SourceId))
                    built.Rows = await ReadEntityRows(entity);
                model.Entities.Add(built);
            }
            foreach (RelationshipJson relationship in source.Relationships)
            {
                RelationshipModel built = new RelationshipModel
                {
                    Id = relationship.Id,
                    Name = relationship.Name,
                    EntityA = relationship.EntityA,
                    EntityB = relationship.EntityB,
                    ValueScaleId = relationship.ValueScaleId,
                    DefaultValue = relationship.DefaultValue,
                    Symmetric = relationship.Symmetric
                };
                ValueScaleModel? scale = model.GetScale(relationship.ValueScaleId);
                built.Values = await ReadRelationshipValues(relationship.SourceId, relationship.SourceAColumn, relationship.SourceBColumn, relationship.SourceValueColumn,
                    relationship.Values, scale);
                model.Relationships.Add(built);
            }
            return model;
        }

        // The rows of an entity's source, one dictionary of attribute name to value each
        private async Task<List<Dictionary<string, object?>>> ReadEntityRows(EntityJson entity)
        {
            List<Dictionary<string, object?>> rows = new List<Dictionary<string, object?>>();
            JsonObject? read = await ReadSource(entity.SourceId!, entity.Name);
            if (read == null)
                return rows;
            Dictionary<string, int> columns = GetColumns(read);
            List<(AttributeJson attribute, int index)> mapped = new List<(AttributeJson, int)>();
            foreach (AttributeJson attribute in entity.Attributes)
            {
                if (string.IsNullOrWhiteSpace(attribute.SourceColumn))
                    continue;
                if (columns.TryGetValue(attribute.SourceColumn, out int index))
                    mapped.Add((attribute, index));
                else
                    Warnings.Add($"{entity.Name}.{attribute.Name} reads column {attribute.SourceColumn}, which the source doesn't have");
            }
            foreach (JsonNode? row in (JsonArray)read["rows"]!)
            {
                Dictionary<string, object?> values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach ((AttributeJson attribute, int index) in mapped)
                    values[attribute.Name] = Convert(((JsonArray)row!)[index], attribute.DataType);
                rows.Add(values);
            }
            return rows;
        }

        // Live values from the relationship's source with the typed in values on top
        private async Task<List<RelationshipValueModel>> ReadRelationshipValues(string? sourceId, string? aColumn, string? bColumn, string? valueColumn,
            List<RelationshipValueModel> typedIn, ValueScaleModel? scale)
        {
            Dictionary<string, RelationshipValueModel> values = new Dictionary<string, RelationshipValueModel>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(sourceId) && !string.IsNullOrEmpty(aColumn) && !string.IsNullOrEmpty(bColumn))
            {
                JsonObject? read = await ReadSource(sourceId, "a relationship");
                if (read != null)
                {
                    Dictionary<string, int> columns = GetColumns(read);
                    // a list with no value column means every pair in it is allowed
                    string present = scale?.Values.FirstOrDefault(x => x.Meaning == "Allowed")?.Code ?? "Y";
                    if (columns.TryGetValue(aColumn, out int a) && columns.TryGetValue(bColumn, out int b))
                    {
                        int value = !string.IsNullOrEmpty(valueColumn) && columns.TryGetValue(valueColumn, out int found) ? found : -1;
                        foreach (JsonNode? row in (JsonArray)read["rows"]!)
                        {
                            JsonArray cells = (JsonArray)row!;
                            string? aKey = Text(cells[a]);
                            string? bKey = Text(cells[b]);
                            if (string.IsNullOrEmpty(aKey) || string.IsNullOrEmpty(bKey))
                                continue;
                            string? cell = value >= 0 ? Text(cells[value]) : present;
                            if (!string.IsNullOrEmpty(cell))
                                values[aKey + "\u0001" + bKey] = new RelationshipValueModel { A = aKey, B = bKey, Value = cell };
                        }
                    }
                    else
                        Warnings.Add($"A relationship reads columns {aColumn} and {bColumn}, which its source doesn't have");
                }
            }
            foreach (RelationshipValueModel value in typedIn)
                values[value.A + "\u0001" + value.B] = value;
            return values.Values.ToList();
        }

        // Reads a source once per load, null when it can't be read, with the reason in the warnings
        public async Task<JsonObject?> ReadSource(string sourceId, string forWhat)
        {
            if (_read.TryGetValue(sourceId, out JsonObject? cached))
                return cached;
            if (!_sources.TryGetValue(sourceId, out SourceInfo? info))
            {
                Warnings.Add($"The source for {forWhat} isn't in this profile");
                return null;
            }
            try
            {
                SourceConnection connection = SourceConnection.FromJson(SchedulerDb.Run(_dbConnSettings, "GetConnectionDetails", info.ConnectionId), _secrets, _dbConnSettings);
                JsonObject read = await SqlServerSource.ReadAll(connection, info.Kind, info.Schema, info.ObjectName, info.QueryText, MaxRows);
                if (read["truncated"]?.GetValue<bool>() == true)
                    Warnings.Add($"Only the first {MaxRows} rows were read for {forWhat}");
                _read[sourceId] = read;
                return read;
            }
            catch (Exception ex)
            {
                Warnings.Add($"Couldn't read the source for {forWhat}: {ex.Message}");
                return null;
            }
        }

        private static Dictionary<string, int> GetColumns(JsonObject read)
        {
            Dictionary<string, int> columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            JsonArray list = (JsonArray)read["columns"]!;
            for (int i = 0; i < list.Count; i++)
                columns.TryAdd(list[i]!["name"]!.GetValue<string>(), i);
            return columns;
        }
        private static string? Text(JsonNode? node)
        {
            if (node == null)
                return null;
            if (node is JsonValue value && value.TryGetValue(out string? text))
                return text.Trim();
            return node.ToJsonString().Trim('"').Trim();
        }
        // A cell as the attribute's data type
        public static object? Convert(JsonNode? node, string dataType)
        {
            if (node == null)
                return null;
            JsonValue? value = node as JsonValue;
            switch (dataType.ToUpperInvariant())
            {
                case "INTEGER":
                case "DECIMAL":
                    if (value != null && value.TryGetValue(out double number))
                        return number;
                    return double.TryParse(Text(node), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : null;
                case "BOOLEAN":
                    if (value != null && value.TryGetValue(out bool flag))
                        return flag;
                    return Solver.Formula.ToBool(Text(node));
                case "DATETIME":
                    return DateTime.TryParse(Text(node), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date) ? date : null;
                default:
                    if (value != null && value.TryGetValue(out double asNumber))
                        return asNumber.ToString(CultureInfo.InvariantCulture);
                    return Text(node);
            }
        }
    }
}
