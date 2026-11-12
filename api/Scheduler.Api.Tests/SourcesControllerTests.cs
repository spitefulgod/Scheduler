using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Scheduler.Api.Tests
{
    [Collection("Api")]
    public class SourcesControllerTests
    {
        private readonly ApiFactory _factory;

        public SourcesControllerTests(ApiFactory factory)
        {
            _factory = factory;
        }
        private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
        {
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        }
        private static string GetUrl()
        {
            return $"/api/profiles/{ApiFactory.RunQuery("SELECT id FROM Profiles WHERE name = 'Press Scheduling'")}/sources";
        }
        private static string DemoConnectionId()
        {
            return ApiFactory.RunQuery("SELECT id FROM Connections WHERE typeId = 'DEMO'");
        }

        [Fact]
        public async Task Sources_ReadTheirColumnsWhenSaved()
        {
            HttpClient client = _factory.CreateClient();
            string url = GetUrl();

            HttpResponseMessage added = await client.PostAsJsonAsync(url, new { connectionId = DemoConnectionId(), name = "Presses again", kind = "table", schema = "src", objectName = "Press" });

            Assert.Equal(HttpStatusCode.OK, added.StatusCode);
            JsonElement source = await ReadJson(added);
            Assert.Equal(7, source.GetProperty("columns").GetArrayLength());
            string sourceId = source.GetProperty("id").GetString()!;
            JsonElement preview = await ReadJson(await client.GetAsync($"{url}/{sourceId}/preview"));
            Assert.Equal(10, preview.GetProperty("rows").GetArrayLength());
            Assert.False(preview.GetProperty("truncated").GetBoolean());
            Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"{url}/{sourceId}")).StatusCode);
        }

        [Fact]
        public async Task QuerySources_MustOnlyReadTheSource()
        {
            HttpClient client = _factory.CreateClient();
            string url = GetUrl();
            string jobs = ApiFactory.RunQuery("SELECT COUNT(*) FROM src.Job");

            HttpResponseMessage good = await client.PostAsJsonAsync(url, new { connectionId = DemoConnectionId(), name = "Big jobs", kind = "query", queryText = "SELECT JobNo, Quantity FROM src.Job WHERE Quantity > 900" });
            Assert.Equal(HttpStatusCode.OK, good.StatusCode);
            Assert.Equal(2, (await ReadJson(good)).GetProperty("columns").GetArrayLength());

            foreach (string sql in new[] { "DELETE FROM src.Job", "SELECT * FROM src.Job; DELETE FROM src.Job", "SELECT * INTO src.JobCopy FROM src.Job", "SELECT * FROM dbo.Connections" })
            {
                HttpResponseMessage bad = await client.PostAsJsonAsync(url, new { connectionId = DemoConnectionId(), name = "Bad", kind = "query", queryText = sql });
                Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            }
            Assert.Equal("0", ApiFactory.RunQuery("SELECT COUNT(*) FROM Sources WHERE name = 'Bad'"));
            Assert.Equal(jobs, ApiFactory.RunQuery("SELECT COUNT(*) FROM src.Job"));
            await client.DeleteAsync($"{url}/{(await ReadJson(good)).GetProperty("id").GetString()}");
        }
    }
}
