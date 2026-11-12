using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Scheduler.Api.Tests
{
    // Domain user security, each role only reaches its own part of a profile
    [Collection("Api")]
    public class SecurityTests
    {
        private readonly ApiFactory _factory;

        public SecurityTests(ApiFactory factory)
        {
            _factory = factory;
        }
        private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
        {
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        }
        private async Task<string> GetPressProfileId()
        {
            JsonElement profiles = await ReadJson(await _factory.CreateClient().GetAsync("/api/profiles"));
            return profiles.EnumerateArray().First(x => x.GetProperty("name").GetString() == "Press Scheduling").GetProperty("id").GetString()!;
        }
        // Gives a user a role on the Press Scheduling profile as the site owner
        private async Task<string> GiveRole(string userName, string role)
        {
            string profileId = await GetPressProfileId();
            HttpResponseMessage added = await _factory.CreateClient().PostAsJsonAsync($"/api/profiles/{profileId}/users", new { userName = userName, role = role });
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);
            return profileId;
        }

        [Fact]
        public async Task Solver_OnlyReachesTheSchedule()
        {
            string profileId = await GiveRole("TEST\\solver", "ProjectSolver");
            HttpClient solver = _factory.CreateClientAs("TEST\\solver");

            JsonElement profiles = await ReadJson(await solver.GetAsync("/api/profiles"));
            Assert.Equal(new string[] { "Press Scheduling" }, profiles.EnumerateArray().Select(x => x.GetProperty("name").GetString()).ToArray());

            JsonElement folders = await ReadJson(await solver.GetAsync($"/api/profiles/{profileId}/navigator"));
            Assert.Equal(new string[] { "runs" }, folders.EnumerateArray().Select(x => x.GetProperty("id").GetString()).ToArray());

            Assert.Equal(HttpStatusCode.OK, (await solver.GetAsync($"/api/profiles/{profileId}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await solver.GetAsync($"/api/profiles/{profileId}/schedule/runs")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await solver.GetAsync($"/api/profiles/{profileId}/schedule/locks")).StatusCode);
            JsonElement attributes = await ReadJson(await solver.GetAsync($"/api/profiles/{profileId}/schedule/attributes"));
            Assert.True(attributes.GetArrayLength() > 0);

            // the model behind the schedule is out of reach
            Assert.Equal(HttpStatusCode.Forbidden, (await solver.GetAsync($"/api/profiles/{profileId}/entities")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await solver.GetAsync($"/api/profiles/{profileId}/rules")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await solver.GetAsync($"/api/profiles/{profileId}/sources")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await solver.GetAsync($"/api/profiles/{profileId}/relationships")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await solver.GetAsync($"/api/profiles/{profileId}/calendars")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await solver.PostAsync($"/api/profiles/{profileId}/rules/check", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await solver.GetAsync("/api/connections")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await solver.GetAsync($"/api/profiles/{profileId}/users")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await solver.PutAsJsonAsync($"/api/profiles/{profileId}", new { name = "Renamed", timeUnitMinutes = 15 })).StatusCode);
        }

        [Fact]
        public async Task Reader_SeesTheModelButChangesNothing()
        {
            string profileId = await GiveRole("TEST\\reader", "ProjectReader");
            HttpClient reader = _factory.CreateClientAs("TEST\\reader");

            Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/api/profiles/{profileId}/entities")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/api/profiles/{profileId}/rules")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/api/profiles/{profileId}/schedule/runs")).StatusCode);
            JsonElement folders = await ReadJson(await reader.GetAsync($"/api/profiles/{profileId}/navigator"));
            Assert.Equal(7, folders.GetArrayLength());

            Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync($"/api/profiles/{profileId}", new { name = "Renamed", timeUnitMinutes = 15 })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"/api/profiles/{profileId}/schedule/solve", new { kind = "Solve" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"/api/profiles/{profileId}/schedule/locks", new { kind = "Item", itemKey = "x" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/api/profiles", new { name = "Mine", timeUnitMinutes = 15 })).StatusCode);
        }

        [Fact]
        public async Task ProjectOwner_ManagesUsersButNotTheSite()
        {
            string profileId = await GiveRole("TEST\\projectowner", "ProjectOwner");
            HttpClient owner = _factory.CreateClientAs("TEST\\projectowner");

            HttpResponseMessage added = await owner.PostAsJsonAsync($"/api/profiles/{profileId}/users", new { userName = "TEST\\planner", role = "ProjectReader" });
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);
            JsonElement users = await ReadJson(added);
            string roleId = users.EnumerateArray().First(x => x.GetProperty("userName").GetString() == "TEST\\planner").GetProperty("id").GetString()!;
            Assert.Equal(HttpStatusCode.OK, (await _factory.CreateClientAs("TEST\\planner").GetAsync($"/api/profiles/{profileId}/entities")).StatusCode);

            Assert.Equal(HttpStatusCode.OK, (await owner.DeleteAsync($"/api/profiles/{profileId}/users/{roleId}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await _factory.CreateClientAs("TEST\\planner").GetAsync($"/api/profiles/{profileId}/entities")).StatusCode);

            // connections are for owners, the site's owners are not
            Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/connections")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/site-owners")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync("/api/site-owners", new { userName = "TEST\\projectowner" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/api/profiles/{profileId}/users", new { userName = "TEST\\x", role = "SiteOwner" })).StatusCode);
        }

        [Fact]
        public async Task UserWithNoRole_SeesNoProfiles()
        {
            string profileId = await GetPressProfileId();
            HttpClient nobody = _factory.CreateClientAs("TEST\\nobody");

            JsonElement profiles = await ReadJson(await nobody.GetAsync("/api/profiles"));
            Assert.Equal(0, profiles.GetArrayLength());
            Assert.Equal(HttpStatusCode.Forbidden, (await nobody.GetAsync($"/api/profiles/{profileId}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await nobody.GetAsync($"/api/profiles/{profileId}/schedule/plan")).StatusCode);
            JsonElement me = await ReadJson(await nobody.GetAsync("/api/user/login"));
            Assert.Equal("TEST\\nobody", me.GetProperty("userName").GetString());
            Assert.False(me.GetProperty("siteOwner").GetBoolean());
        }

        [Fact]
        public async Task SiteOwner_GivesOutSiteOwner()
        {
            HttpClient site = _factory.CreateClient();
            JsonElement me = await ReadJson(await site.GetAsync("/api/user/login"));
            Assert.True(me.GetProperty("siteOwner").GetBoolean());

            HttpResponseMessage added = await site.PostAsJsonAsync("/api/site-owners", new { userName = "TEST\\second" });
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);
            JsonElement second = await ReadJson(await _factory.CreateClientAs("TEST\\second").GetAsync("/api/user/login"));
            Assert.True(second.GetProperty("siteOwner").GetBoolean());
        }

        [Fact]
        public async Task Settings_AreKeptPerUser()
        {
            HttpClient first = _factory.CreateClientAs("TEST\\first");
            HttpClient other = _factory.CreateClientAs("TEST\\other");

            Assert.Equal(HttpStatusCode.OK, (await first.PutAsJsonAsync("/api/user/settings", new { propertiesCollapsed = true })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await first.PutAsJsonAsync("/api/user/settings", new { density = "compact" })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await other.PutAsJsonAsync("/api/user/settings", new { propertiesCollapsed = false })).StatusCode);

            JsonElement firstSettings = (await ReadJson(await first.GetAsync("/api/user/login"))).GetProperty("settings");
            Assert.True(firstSettings.GetProperty("propertiesCollapsed").GetBoolean());
            Assert.Equal("compact", firstSettings.GetProperty("density").GetString());
            Assert.False((await ReadJson(await other.GetAsync("/api/user/login"))).GetProperty("settings").GetProperty("propertiesCollapsed").GetBoolean());
        }
    }
}
