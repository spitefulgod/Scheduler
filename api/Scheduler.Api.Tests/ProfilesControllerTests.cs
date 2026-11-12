using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Scheduler.Api.Tests
{
    [Collection("Api")]
    public class ProfilesControllerTests
    {
        private readonly ApiFactory _factory;

        public ProfilesControllerTests(ApiFactory factory)
        {
            _factory = factory;
        }
        private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
        {
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        }

        [Fact]
        public async Task Navigator_ShowsThePressSchedulingProfile()
        {
            HttpClient client = _factory.CreateClient();
            JsonElement profiles = await ReadJson(await client.GetAsync("/api/profiles"));
            string profileId = profiles.EnumerateArray().First(x => x.GetProperty("name").GetString() == "Press Scheduling").GetProperty("id").GetString()!;

            JsonElement folders = await ReadJson(await client.GetAsync($"/api/profiles/{profileId}/navigator"));

            Dictionary<string, int> counts = folders.EnumerateArray().ToDictionary(x => x.GetProperty("id").GetString()!, x => x.GetProperty("children").GetArrayLength());
            Assert.Equal(13, counts["sources"]);
            Assert.Equal(6, counts["entities"]);
            Assert.Equal(7, counts["relationships"]);
            Assert.Equal(11, counts["calendars"]);
            Assert.Equal(18, counts["rules"]);
        }

        [Fact]
        public async Task Profiles_NeedAUniqueName()
        {
            HttpClient client = _factory.CreateClient();
            HttpResponseMessage added = await client.PostAsJsonAsync("/api/profiles", new { name = "Lengths", description = "Lengths through a routing", timeUnitMinutes = 30 });
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);
            Assert.Equal(30, (await ReadJson(added)).GetProperty("timeUnitMinutes").GetInt32());

            HttpResponseMessage again = await client.PostAsJsonAsync("/api/profiles", new { name = "Lengths", timeUnitMinutes = 15 });
            Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        }
    }
}
