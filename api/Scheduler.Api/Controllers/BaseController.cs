using Microsoft.AspNetCore.Mvc;
using Scheduler.Api.Services;
using System.Data;
using System.Text.Json;

namespace Scheduler.Api.Controllers
{
    [ApiController]
    public abstract class BaseController : ControllerBase
    {
        protected readonly string _dbConnSettings;

        public BaseController(IConfiguration configuration)
        {
            _dbConnSettings = configuration["ConnectionStrings:SchedulerDBConn"]!.ToString();
        }
        // Returns the json from a stored procedure as a json response (Ok(string) would be text/plain)
        protected IActionResult OkJson(string? json)
        {
            return Content(string.IsNullOrEmpty(json) ? "{}" : json, "application/json");
        }
        // Runs a stored procedure and hands back its json, or its error message as a bad request
        protected IActionResult RunSP(string procedure, params object?[] args)
        {
            try
            {
                using (SQLConnector conn = SQLConnector.GetConnection(_dbConnSettings))
                {
                    string? objObject = PullSPResult(conn, procedure, args);
                    return OkJson(objObject);
                }
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message.ToString());
            }
        }
        // A source connection ready to read, the password is decrypted here and never leaves the API
        protected SourceConnection GetSourceConnection(string connectionId, ISourceSecrets secrets)
        {
            using (SQLConnector conn = SQLConnector.GetConnection(_dbConnSettings))
                return SourceConnection.FromJson(PullSPResult(conn, "GetConnectionDetails", connectionId), secrets, _dbConnSettings);
        }
        // Used to pull structured errors from stored procedures
        private protected string? PullSPResult(SQLConnector Conn, string SPName, params object?[] args)
        {
            return SchedulerDb.Pull(Conn, SPName, args);
        }
    }
}
