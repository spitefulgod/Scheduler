using Microsoft.Data.SqlClient;
using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Scheduler.Api.Services
{
    // What's needed to connect to a SQL Server source, the password is kept apart, encrypted
    public class SqlServerSettings
    {
        // A port goes on the end, e.g. sql01,1433
        public string Server { get; set; } = "";
        public string Database { get; set; } = "";
        // Blank signs in with Windows, as whoever the API runs as
        public string User { get; set; } = "";
        // For a server whose certificate isn't trusted by the API's machine, the connection is still encrypted
        public bool TrustServerCertificate { get; set; }

        public bool UsesWindows => string.IsNullOrWhiteSpace(User);

        // What's wrong with the settings, empty when there's nothing
        public string GetError()
        {
            if (string.IsNullOrWhiteSpace(Server))
                return "The server is needed";
            if (string.IsNullOrWhiteSpace(Database))
                return "The database is needed";
            return "";
        }
        public static SqlServerSettings FromJson(JsonElement json)
        {
            if (json.ValueKind != JsonValueKind.Object)
                return new SqlServerSettings();
            return json.Deserialize<SqlServerSettings>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new SqlServerSettings();
        }
        public JsonObject ToJson()
        {
            return new JsonObject { ["server"] = Server.Trim(), ["database"] = Database.Trim(), ["user"] = User.Trim(), ["trustServerCertificate"] = TrustServerCertificate };
        }
    }

    // A source connection ready to use, with the password decrypted into the connection string, it stays in the API
    public class SourceConnection
    {
        public const string SqlServerType = "SQLSERVER";
        public const string DemoType = "DEMO";
        // The demo connection reads this database, so it only gets the made up source tables, never the scheduler's own
        public const string DemoSchema = "src";

        public string ConnectionString { get; private set; } = "";
        public string DatabaseName { get; private set; } = "";
        // Only this schema can be read, null for any
        public string? OnlySchema { get; private set; }

        // From what GetConnectionDetails gives back, the demo connection uses the scheduler's own connection string
        public static SourceConnection FromJson(string? json, ISourceSecrets secrets, string schedulerConnection)
        {
            using (JsonDocument document = JsonDocument.Parse(json ?? "{}"))
            {
                JsonElement root = document.RootElement;
                string typeId = root.GetProperty("typeId").GetString() ?? "";
                if (typeId == DemoType)
                    return ForDemo(schedulerConnection);
                if (typeId != SqlServerType)
                    throw new Exception("Only SQL Server connections can be used for now");
                SqlServerSettings settings = SqlServerSettings.FromJson(root.GetProperty("settings"));
                string? secret = root.TryGetProperty("secret", out JsonElement secretElement) ? secretElement.GetString() : null;
                if (!settings.UsesWindows && secret == null)
                    throw new Exception("The connection has no password, set it again");
                return ForSqlServer(settings, settings.UsesWindows ? null : secrets.Decrypt(secret!));
            }
        }
        public static SourceConnection ForSqlServer(SqlServerSettings settings, string? password)
        {
            return new SourceConnection
            {
                ConnectionString = SqlServerSource.GetConnectionString(settings, password),
                DatabaseName = settings.Database.Trim()
            };
        }
        public static SourceConnection ForDemo(string schedulerConnection)
        {
            SqlConnectionStringBuilder builder = new SqlConnectionStringBuilder(schedulerConnection)
            {
                ApplicationName = "Scheduler source",
                ApplicationIntent = ApplicationIntent.ReadOnly
            };
            return new SourceConnection { ConnectionString = builder.ConnectionString, DatabaseName = builder.InitialCatalog, OnlySchema = DemoSchema };
        }
    }

    // Connects to a SQL Server source and reads it, every statement goes through ReadOnlySqlGuard first
    // and runs in a transaction that's always rolled back, so nothing sent to a source is ever kept
    public static class SqlServerSource
    {
        // Most rows a preview gives back
        public const int PreviewRows = 200;

        // The catalog views read to list tables, views and columns
        private static readonly SourceObjectName[] Catalog = new SourceObjectName[]
        {
            new SourceObjectName("sys", "objects"),
            new SourceObjectName("sys", "schemas"),
            new SourceObjectName("sys", "columns"),
            new SourceObjectName("sys", "types")
        };

        public static string GetConnectionString(SqlServerSettings settings, string? password)
        {
            SqlConnectionStringBuilder builder = new SqlConnectionStringBuilder
            {
                DataSource = settings.Server.Trim(),
                InitialCatalog = settings.Database.Trim(),
                IntegratedSecurity = settings.UsesWindows,
                Encrypt = SqlConnectionEncryptOption.Mandatory,
                TrustServerCertificate = settings.TrustServerCertificate,
                ConnectTimeout = 15,
                ApplicationName = "Scheduler source",
                ApplicationIntent = ApplicationIntent.ReadOnly,
                PersistSecurityInfo = false
            };
            if (!settings.UsesWindows)
            {
                builder.UserID = settings.User.Trim();
                builder.Password = password ?? "";
            }
            return builder.ConnectionString;
        }

        // Connects and reads one row from the catalog, gives back what went wrong when it can't
        public static async Task<(bool ok, string message)> Test(SourceConnection source)
        {
            try
            {
                await Query(source, "SELECT TOP (1) o.name FROM sys.objects o", Catalog, 1, null);
                return (true, "Connected");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        // Every table and view, [{ schema, name, type }] where type is table or view
        public static async Task<JsonArray> ReadObjects(SourceConnection source)
        {
            string sql = @"
SELECT s.name [schemaName], o.name [objectName], CASE WHEN o.type = 'V' THEN 'view' ELSE 'table' END [objectType]
    FROM sys.objects o
        JOIN sys.schemas s ON s.schema_id = o.schema_id
    WHERE o.type IN ('U', 'V') AND o.is_ms_shipped = 0 AND (@schema IS NULL OR s.name = @schema)
    ORDER BY s.name, o.name";
            JsonObject result = await Query(source, sql, Catalog, int.MaxValue, new Dictionary<string, object?> { ["@schema"] = source.OnlySchema });
            JsonArray objects = new JsonArray();
            foreach (JsonNode? row in (JsonArray)result["rows"]!)
                objects.Add(new JsonObject { ["schema"] = row![0]!.GetValue<string>(), ["name"] = row[1]!.GetValue<string>(), ["type"] = row[2]!.GetValue<string>() });
            return objects;
        }

        // The columns of one table or view, [{ name, sqlType, nullable }]
        public static async Task<JsonArray> ReadColumns(SourceConnection source, string schema, string name)
        {
            await CheckObject(source, schema, name);
            string sql = @"
SELECT c.name [columnName], t.name [sqlType], c.is_nullable [nullable]
    FROM sys.objects o
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        JOIN sys.columns c ON c.object_id = o.object_id
        JOIN sys.types t ON t.user_type_id = c.user_type_id
    WHERE s.name = @schema AND o.name = @name
    ORDER BY c.column_id";
            JsonObject result = await Query(source, sql, Catalog, int.MaxValue, new Dictionary<string, object?> { ["@schema"] = schema, ["@name"] = name });
            JsonArray columns = new JsonArray();
            foreach (JsonNode? row in (JsonArray)result["rows"]!)
                columns.Add(new JsonObject { ["name"] = row![0]!.GetValue<string>(), ["sqlType"] = row[1]!.GetValue<string>(), ["nullable"] = row[2]!.GetValue<bool>() });
            return columns;
        }

        // The first rows of a table or view, { columns, rows, truncated }
        public static async Task<JsonObject> Preview(SourceConnection source, string schema, string name, int top)
        {
            await CheckObject(source, schema, name);
            top = Math.Clamp(top, 1, PreviewRows);
            // one more row than asked for, to know whether there are more
            string sql = $"SELECT TOP ({top + 1}) * FROM {Quote(schema)}.{Quote(name)}";
            return await Query(source, sql, new SourceObjectName[] { new SourceObjectName(schema, name) }, top, null);
        }

        // The first rows of a saved select, it can read any table or view the connection can
        public static async Task<JsonObject> PreviewQuery(SourceConnection source, string sql, int top)
        {
            List<SourceObjectName> allowed = new List<SourceObjectName>();
            foreach (JsonNode? item in await ReadObjects(source))
                allowed.Add(new SourceObjectName(item!["schema"]!.GetValue<string>(), item["name"]!.GetValue<string>()));
            return await Query(source, sql, allowed, Math.Clamp(top, 1, PreviewRows), null);
        }

        // Every row of a table, view or saved select, up to maxRows, for the rule engine, { columns, rows, truncated }
        public static async Task<JsonObject> ReadAll(SourceConnection source, string kind, string? schema, string? name, string? queryText, int maxRows)
        {
            if (kind == "query")
            {
                List<SourceObjectName> allowed = new List<SourceObjectName>();
                foreach (JsonNode? item in await ReadObjects(source))
                    allowed.Add(new SourceObjectName(item!["schema"]!.GetValue<string>(), item["name"]!.GetValue<string>()));
                return await Query(source, queryText ?? "", allowed, maxRows, null);
            }
            await CheckObject(source, schema ?? "", name ?? "");
            return await Query(source, $"SELECT * FROM {Quote(schema!)}.{Quote(name!)}", new SourceObjectName[] { new SourceObjectName(schema!, name!) }, maxRows, null);
        }

        // Runs one checked select and gives back { columns : [{ name, sqlType, nullable }], rows : [[...]], truncated }
        public static async Task<JsonObject> Query(SourceConnection source, string sql, IReadOnlyCollection<SourceObjectName> allowed, int maxRows, Dictionary<string, object?>? parameters)
        {
            GuardResult guard = ReadOnlySqlGuard.Validate(sql, allowed, source.DatabaseName);
            if (!guard.IsAllowed)
                throw new Exception(guard.Reason);

            using (SqlConnection connection = new SqlConnection(source.ConnectionString))
            {
                await connection.OpenAsync();
                using (SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadUncommitted))
                {
                    try
                    {
                        using (SqlCommand command = new SqlCommand(sql, connection, transaction))
                        {
                            command.CommandTimeout = 30;
                            if (parameters != null)
                                foreach (KeyValuePair<string, object?> parameter in parameters)
                                    command.Parameters.AddWithValue(parameter.Key, parameter.Value ?? DBNull.Value);
                            using (SqlDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SingleResult))
                                return await ReadResult(reader, maxRows);
                        }
                    }
                    finally
                    {
                        // nothing sent to a source is ever kept
                        transaction.Rollback();
                    }
                }
            }
        }

        private static async Task<JsonObject> ReadResult(SqlDataReader reader, int maxRows)
        {
            JsonArray columns = new JsonArray();
            foreach (System.Data.Common.DbColumn column in await reader.GetColumnSchemaAsync())
                columns.Add(new JsonObject { ["name"] = column.ColumnName, ["sqlType"] = column.DataTypeName ?? "unknown", ["nullable"] = column.AllowDBNull ?? true });

            JsonArray rows = new JsonArray();
            bool truncated = false;
            while (await reader.ReadAsync())
            {
                if (rows.Count >= maxRows)
                {
                    truncated = true;
                    break;
                }
                JsonArray row = new JsonArray();
                for (int i = 0; i < reader.FieldCount; i++)
                    row.Add(reader.IsDBNull(i) ? null : JsonSerializer.SerializeToNode(reader.GetValue(i)));
                rows.Add(row);
            }
            return new JsonObject { ["columns"] = columns, ["rows"] = rows, ["truncated"] = truncated };
        }

        // Makes sure the table or view is there, and in the schema the connection is kept to
        private static async Task CheckObject(SourceConnection source, string schema, string name)
        {
            if (string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(name))
                throw new Exception("The schema and name are needed");
            foreach (JsonNode? item in await ReadObjects(source))
                if (string.Equals(item!["schema"]!.GetValue<string>(), schema, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item["name"]!.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase))
                    return;
            throw new Exception($"'{schema}.{name}' isn't on this connection");
        }

        public static string Quote(string identifier)
        {
            return "[" + identifier.Replace("]", "]]") + "]";
        }
    }
}
