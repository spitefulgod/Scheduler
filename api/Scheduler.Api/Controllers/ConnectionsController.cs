using Microsoft.AspNetCore.Mvc;
using Scheduler.Api.Services;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Scheduler.Api.Controllers
{
    // Read only connections to where the data comes from, shared by every profile, only SQL Server for now
    // the password is encrypted here before it's saved and never sent back, the client only learns whether there is one
    [Route("api/connections")]
    [ApiController]
    public class ConnectionsController : BaseController
    {
        private readonly ISourceSecrets _secrets;

        public ConnectionsController(IConfiguration configuration, ISourceSecrets secrets) : base(configuration)
        {
            _secrets = secrets;
        }
        // What the client sends to add, change or test one, a blank password keeps the one it has
        public class ConnectionDetails
        {
            public string Name { get; set; } = "";
            public string TypeId { get; set; } = SourceConnection.SqlServerType;
            public JsonElement Settings { get; set; }
            public string? Password { get; set; }
        }

        [HttpGet]
        public IActionResult GetConnections()
        {
            return RunSP("GetConnections");
        }
        [HttpGet("{connectionId}")]
        public IActionResult GetConnection(string connectionId)
        {
            return RunSP("GetConnection", connectionId);
        }
        [HttpPost]
        public IActionResult AddConnection([FromBody] ConnectionDetails connection)
        {
            return Save(null, connection);
        }
        [HttpPut("{connectionId}")]
        public IActionResult SaveConnection(string connectionId, [FromBody] ConnectionDetails connection)
        {
            return Save(connectionId, connection);
        }
        [HttpDelete("{connectionId}")]
        public IActionResult DeleteConnection(string connectionId)
        {
            return RunSP("DeleteConnection", connectionId);
        }
        // Tests what's in the connection wizard before it's saved, nothing is kept
        // with a connectionId and no password it uses the password already saved for that connection
        [HttpPost("test")]
        public async Task<IActionResult> TestDetails([FromBody] ConnectionDetails connection, [FromQuery] string? connectionId = null)
        {
            try
            {
                SqlServerSettings settings = GetSettings(connection);
                string? password = connection.Password;
                if (!settings.UsesWindows && string.IsNullOrEmpty(password))
                {
                    if (connectionId == null)
                        throw new Exception("The password is needed");
                    using (SQLConnector conn = SQLConnector.GetConnection(_dbConnSettings))
                    using (JsonDocument document = JsonDocument.Parse(PullSPResult(conn, "GetConnectionDetails", connectionId) ?? "{}"))
                    {
                        string? secret = document.RootElement.GetProperty("secret").GetString();
                        if (secret == null)
                            throw new Exception("The password is needed");
                        password = _secrets.Decrypt(secret);
                    }
                }
                (bool ok, string message) = await SqlServerSource.Test(SourceConnection.ForSqlServer(settings, password));
                return Ok(new { ok = ok, message = message });
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }
        // Tests a saved connection and keeps how it went
        [HttpPost("{connectionId}/test")]
        public async Task<IActionResult> Test(string connectionId)
        {
            try
            {
                (bool ok, string message) = await SqlServerSource.Test(GetSourceConnection(connectionId, _secrets));
                return RunSP("SaveConnectionTest", connectionId, ok, message);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }
        // Its tables and views, [{ schema, name, type }]
        [HttpGet("{connectionId}/objects")]
        public async Task<IActionResult> GetObjects(string connectionId)
        {
            try
            {
                JsonArray objects = await SqlServerSource.ReadObjects(GetSourceConnection(connectionId, _secrets));
                return OkJson(objects.ToJsonString());
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }
        // The columns of one table or view
        [HttpGet("{connectionId}/objects/columns")]
        public async Task<IActionResult> GetColumns(string connectionId, [FromQuery] string schema, [FromQuery] string name)
        {
            try
            {
                JsonArray columns = await SqlServerSource.ReadColumns(GetSourceConnection(connectionId, _secrets), schema, name);
                return OkJson(columns.ToJsonString());
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }
        // The first rows of one table or view
        [HttpGet("{connectionId}/objects/preview")]
        public async Task<IActionResult> Preview(string connectionId, [FromQuery] string schema, [FromQuery] string name, [FromQuery] int top = SqlServerSource.PreviewRows)
        {
            try
            {
                JsonObject preview = await SqlServerSource.Preview(GetSourceConnection(connectionId, _secrets), schema, name, top);
                return OkJson(preview.ToJsonString());
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        private IActionResult Save(string? connectionId, ConnectionDetails connection)
        {
            try
            {
                SqlServerSettings settings = GetSettings(connection);
                // a SQL Server login needs its password when it's new, after that a blank one keeps what's there
                if (!settings.UsesWindows && connectionId == null && string.IsNullOrEmpty(connection.Password))
                    throw new Exception("The password is needed");
                string? secret = settings.UsesWindows || string.IsNullOrEmpty(connection.Password) ? null : _secrets.Encrypt(connection.Password);
                return RunSP("SaveConnection", connectionId, connection.Name, connection.TypeId, settings.ToJson().ToJsonString(), secret, settings.UsesWindows);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }
        private static SqlServerSettings GetSettings(ConnectionDetails connection)
        {
            if (connection.TypeId != SourceConnection.SqlServerType)
                throw new Exception("Only SQL Server connections can be added for now");
            SqlServerSettings settings = SqlServerSettings.FromJson(connection.Settings);
            string error = settings.GetError();
            if (error != "")
                throw new Exception(error);
            return settings;
        }
    }
}
