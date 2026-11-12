using System.Data;
using System.Text.Json;

namespace Scheduler.Api.Services
{
    // Calls SchedulerMeta's stored procedures from outside a controller, e.g. the solve service, the same way BaseController.RunSP does
    // every procedure gives back one results column of json, an { errorMessage } is thrown
    internal static class SchedulerDb
    {
        public static string? Run(string dbConnSettings, string procedure, params object?[] args)
        {
            using (SQLConnector conn = SQLConnector.GetConnection(dbConnSettings))
                return Pull(conn, procedure, args);
        }
        public static JsonElement RunElement(string dbConnSettings, string procedure, params object?[] args)
        {
            using (JsonDocument document = JsonDocument.Parse(Run(dbConnSettings, procedure, args) ?? "{}"))
                return document.RootElement.Clone();
        }

        // Used to pull structured errors from stored procedures
        public static string? Pull(SQLConnector Conn, string SPName, params object?[] args)
        {
            int returnValue = 0;

            DataTableCollection? result = Conn.GetDataSetFromSP(SPName, ref returnValue, args);
            if (returnValue != 1 || result == null)
                throwDBError(result);

            string? data = null;
            // Check if result has an error
            if (result != null && result.Count > 0 && result[0].Rows.Count != 0 && result[0].Columns.IndexOf("results") >= 0)
            {
                data = result[0].Rows[0]["results"].ToString() ?? "{}";
                data = data == "" ? "{}" : data;
                using (JsonDocument document = JsonDocument.Parse(data))
                    if (document.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        for (int i = 0; i < document.RootElement.GetArrayLength(); i++)
                            if (document.RootElement[i].ValueKind == JsonValueKind.Object && document.RootElement[i].TryGetProperty("errorMessage", out JsonElement errorMessageElement))
                                throw new Exception(errorMessageElement.ToString());
                    }
                    else if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("errorMessage", out JsonElement errorMessageElement2))
                        throw new Exception(errorMessageElement2.ToString());
            }
            // return the result
            return data;
        }
        //
        private static void throwDBError(DataTableCollection? result)
        {
            // Is there a given error?
            if (result != null && result.Count > 0 && result[0].Rows.Count > 0 && result[0].Columns.IndexOf("results") >= 0)
                using (JsonDocument document = JsonDocument.Parse(result[0].Rows[0]["results"].ToString() ?? ""))
                    if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("errorMessage", out JsonElement errorMessageElement))
                        throw new Exception(errorMessageElement.ToString());

            throw new Exception("Unable to pull resultset from database procedure");
        }
    }
}
