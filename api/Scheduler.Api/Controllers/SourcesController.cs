using Microsoft.AspNetCore.Mvc;
using Scheduler.Api.Services;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Scheduler.Api.Controllers
{
    // The tables, views and saved selects a profile reads from, their columns are kept so they can be mapped without connecting
    [Route("api/profiles/{profileId}/sources")]
    [ApiController]
    public class SourcesController : BaseController
    {
        private readonly ISourceSecrets _secrets;

        public SourcesController(IConfiguration configuration, ISourceSecrets secrets) : base(configuration)
        {
            _secrets = secrets;
        }
        // What the client sends to add or change one, kind is table, view or query
        public class SourceDetails
        {
            public string ConnectionId { get; set; } = "";
            public string Name { get; set; } = "";
            public string Kind { get; set; } = "table";
            public string? Schema { get; set; }
            public string? ObjectName { get; set; }
            public string? QueryText { get; set; }
        }

        [HttpGet]
        public IActionResult GetSources(string profileId)
        {
            return RunSP("GetSources", profileId);
        }
        [HttpGet("{sourceId}")]
        public IActionResult GetSource(string profileId, string sourceId)
        {
            return RunSP("GetSource", profileId, sourceId);
        }
        [HttpPost]
        public Task<IActionResult> AddSource(string profileId, [FromBody] SourceDetails source)
        {
            return Save(profileId, null, source);
        }
        [HttpPut("{sourceId}")]
        public Task<IActionResult> SaveSource(string profileId, string sourceId, [FromBody] SourceDetails source)
        {
            return Save(profileId, sourceId, source);
        }
        [HttpDelete("{sourceId}")]
        public IActionResult DeleteSource(string profileId, string sourceId)
        {
            return RunSP("DeleteSource", profileId, sourceId);
        }
        // Reads its columns again from the connection
        [HttpPost("{sourceId}/refresh")]
        public async Task<IActionResult> Refresh(string profileId, string sourceId)
        {
            try
            {
                SourceDetails source = GetDetails(profileId, sourceId);
                JsonArray columns = await ReadColumns(source);
                return RunSP("SaveSourceColumns", profileId, sourceId, columns.ToJsonString());
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }
        // The first rows, { columns, rows, truncated }
        [HttpGet("{sourceId}/preview")]
        public async Task<IActionResult> Preview(string profileId, string sourceId, [FromQuery] int top = SqlServerSource.PreviewRows)
        {
            try
            {
                SourceDetails source = GetDetails(profileId, sourceId);
                SourceConnection connection = GetSourceConnection(source.ConnectionId, _secrets);
                JsonObject preview = source.Kind == "query"
                    ? await SqlServerSource.PreviewQuery(connection, source.QueryText ?? "", top)
                    : await SqlServerSource.Preview(connection, source.Schema ?? "", source.ObjectName ?? "", top);
                return OkJson(preview.ToJsonString());
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        // The columns are read first, so a table that isn't there or a query that isn't only a select is never saved
        private async Task<IActionResult> Save(string profileId, string? sourceId, SourceDetails source)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(source.ConnectionId))
                    throw new Exception("The connection is needed");
                JsonArray columns = await ReadColumns(source);
                using (SQLConnector conn = SQLConnector.GetConnection(_dbConnSettings))
                {
                    string? saved = PullSPResult(conn, "SaveSource", profileId, sourceId, source.ConnectionId, source.Name, source.Kind, source.Schema, source.ObjectName, source.QueryText);
                    using (JsonDocument document = JsonDocument.Parse(saved ?? "{}"))
                        sourceId = document.RootElement.GetProperty("id").GetString();
                    return OkJson(PullSPResult(conn, "SaveSourceColumns", profileId, sourceId, columns.ToJsonString()));
                }
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }
        private async Task<JsonArray> ReadColumns(SourceDetails source)
        {
            SourceConnection connection = GetSourceConnection(source.ConnectionId, _secrets);
            if (source.Kind == "query")
                return (JsonArray)(await SqlServerSource.PreviewQuery(connection, source.QueryText ?? "", 1))["columns"]!.DeepClone();
            return await SqlServerSource.ReadColumns(connection, source.Schema ?? "", source.ObjectName ?? "");
        }
        // A saved source as it was last saved
        private SourceDetails GetDetails(string profileId, string sourceId)
        {
            using (SQLConnector conn = SQLConnector.GetConnection(_dbConnSettings))
            {
                string? json = PullSPResult(conn, "GetSource", profileId, sourceId);
                return JsonSerializer.Deserialize<SourceDetails>(json ?? "{}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new SourceDetails();
            }
        }
    }
}
