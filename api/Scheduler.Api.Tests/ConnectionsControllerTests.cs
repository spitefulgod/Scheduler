using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Scheduler.Api.Tests
{
    [Collection("Api")]
    public class ConnectionsControllerTests
    {
        private readonly ApiFactory _factory;
        private const string Url = "/api/connections";

        public ConnectionsControllerTests(ApiFactory factory)
        {
            _factory = factory;
        }
        private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
        {
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        }
        private static object Details(string name, string? password, string user = ApiFactory.SourceUser) => new
        {
            name = name,
            typeId = "SQLSERVER",
            settings = new { server = ApiFactory.SqlServer, database = ApiFactory.DatabaseName, user = user, trustServerCertificate = true },
            password = password
        };
        private string DemoConnectionId()
        {
            return ApiFactory.RunQuery("SELECT id FROM Connections WHERE typeId = 'DEMO'");
        }

        [Fact]
        public async Task Connections_KeepTheirPasswordEncryptedAndNeverSendItBack()
        {
            HttpClient client = _factory.CreateClient();

            // a new SQL Server login needs its password
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Url, Details("NoPassword", null))).StatusCode);
            HttpResponseMessage added = await client.PostAsJsonAsync(Url, Details("Factory", ApiFactory.SourcePassword));
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);
            string body = await added.Content.ReadAsStringAsync();
            JsonElement connection = JsonDocument.Parse(body).RootElement;
            string connectionId = connection.GetProperty("id").GetString()!;
            Assert.True(connection.GetProperty("hasPassword").GetBoolean());
            Assert.DoesNotContain(ApiFactory.SourcePassword, body);
            Assert.False(connection.TryGetProperty("secret", out _));

            // the database only holds it encrypted
            string secret = ApiFactory.RunQuery($"SELECT secret FROM Connections WHERE id = '{connectionId}'");
            Assert.StartsWith("v1:", secret);
            Assert.DoesNotContain(ApiFactory.SourcePassword, secret);

            // a blank password keeps the one it has
            HttpResponseMessage changed = await client.PutAsJsonAsync($"{Url}/{connectionId}", Details("Factory data", null));
            Assert.Equal("Factory data", (await ReadJson(changed)).GetProperty("name").GetString());
            Assert.Equal(secret, ApiFactory.RunQuery($"SELECT secret FROM Connections WHERE id = '{connectionId}'"));

            string list = await (await client.GetAsync(Url)).Content.ReadAsStringAsync();
            Assert.Contains(connectionId, list);
            Assert.DoesNotContain("v1:", list);

            Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"{Url}/{connectionId}")).StatusCode);
        }

        [Fact]
        public async Task Test_SaysHowItWentAndKeepsIt()
        {
            HttpClient client = _factory.CreateClient();
            JsonElement draft = await ReadJson(await client.PostAsJsonAsync($"{Url}/test", Details("Draft", ApiFactory.SourcePassword)));
            Assert.True(draft.GetProperty("ok").GetBoolean(), draft.GetProperty("message").GetString());
            JsonElement wrong = await ReadJson(await client.PostAsJsonAsync($"{Url}/test", Details("Draft", "not the password")));
            Assert.False(wrong.GetProperty("ok").GetBoolean());
            Assert.DoesNotContain("not the password", wrong.GetProperty("message").GetString());

            string connectionId = (await ReadJson(await client.PostAsJsonAsync(Url, Details("Tested", ApiFactory.SourcePassword)))).GetProperty("id").GetString()!;
            JsonElement tested = await ReadJson(await client.PostAsync($"{Url}/{connectionId}/test", null));
            Assert.True(tested.GetProperty("lastTestOk").GetBoolean());
            // the saved password is used when the wizard leaves it blank
            JsonElement again = await ReadJson(await client.PostAsJsonAsync($"{Url}/test?connectionId={connectionId}", Details("Tested", null)));
            Assert.True(again.GetProperty("ok").GetBoolean());
            await client.DeleteAsync($"{Url}/{connectionId}");
        }

        [Fact]
        public async Task Objects_ListTheTablesAndViewsWithTheirColumnsAndRows()
        {
            HttpClient client = _factory.CreateClient();
            string connectionId = (await ReadJson(await client.PostAsJsonAsync(Url, Details("Objects", ApiFactory.SourcePassword)))).GetProperty("id").GetString()!;

            JsonElement objects = await ReadJson(await client.GetAsync($"{Url}/{connectionId}/objects"));
            Assert.Contains(objects.EnumerateArray(), x => x.GetProperty("name").GetString() == "Job" && x.GetProperty("type").GetString() == "table");
            Assert.Contains(objects.EnumerateArray(), x => x.GetProperty("name").GetString() == "ToolPressFit" && x.GetProperty("type").GetString() == "view");

            JsonElement columns = await ReadJson(await client.GetAsync($"{Url}/{connectionId}/objects/columns?schema=src&name=Job"));
            Assert.Equal("JobNo", columns[0].GetProperty("name").GetString());
            Assert.Equal("nvarchar", columns[0].GetProperty("sqlType").GetString());

            JsonElement preview = await ReadJson(await client.GetAsync($"{Url}/{connectionId}/objects/preview?schema=src&name=Job&top=10"));
            Assert.Equal(10, preview.GetProperty("rows").GetArrayLength());
            Assert.True(preview.GetProperty("truncated").GetBoolean());
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{Url}/{connectionId}/objects/preview?schema=src&name=Missing")).StatusCode);
            await client.DeleteAsync($"{Url}/{connectionId}");
        }

        [Fact]
        public async Task DemoConnection_OnlyShowsTheSrcSchemaAndCantBeChanged()
        {
            HttpClient client = _factory.CreateClient();
            string connectionId = DemoConnectionId();

            JsonElement objects = await ReadJson(await client.GetAsync($"{Url}/{connectionId}/objects"));
            Assert.Equal(13, objects.GetArrayLength());
            Assert.All(objects.EnumerateArray(), x => Assert.Equal("src", x.GetProperty("schema").GetString()));
            // the scheduler's own tables, e.g. where the passwords are, can't be read through it
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{Url}/{connectionId}/objects/preview?schema=dbo&name=Connections")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{Url}/{connectionId}", Details("Demo", ApiFactory.SourcePassword))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync($"{Url}/{connectionId}")).StatusCode);
        }
    }
}
