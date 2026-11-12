using Microsoft.AspNetCore.Mvc;
using Scheduler.Api.Services;
using System.DirectoryServices.AccountManagement;
using System.Runtime.Versioning;
using System.Security.Claims;
using System.Security.Principal;
using System.Text.Json;

namespace Scheduler.Api.Controllers
{
    // Domain user security: who's signed in and what they can do, their own settings, the site owners and each profile's users
    [Route("api")]
    [ApiController]
    [ProfileAccess(Access.SignedIn)]
    public class UsersController : BaseController
    {
        private readonly UserAccessService _access;

        public UsersController(IConfiguration configuration, UserAccessService access) : base(configuration)
        {
            _access = access;
        }
        // What the client sends to give a role, the role is only needed for a profile
        public class UserRoleDetails
        {
            public string UserName { get; set; } = "";
            public string? Role { get; set; }
        }
        // What a user can do in one profile
        public class ProfileRights
        {
            public string ProfileId { get; set; } = "";
            public bool ReadModel { get; set; }
            public bool EditModel { get; set; }
            public bool ViewSchedule { get; set; }
            public bool Solve { get; set; }
            public bool ManageUsers { get; set; }
        }
        // The signed in user, for the sidebar and for the client to show only what they can use
        public class LoginDetails
        {
            public string? Id { get; set; }
            public string UserName { get; set; } = "";
            public string DisplayName { get; set; } = "";
            public string Initials { get; set; } = "";
            public string? Email { get; set; }
            public bool SiteOwner { get; set; }
            public bool Designer { get; set; }
            public List<ProfileRights> Profiles { get; set; } = new List<ProfileRights>();
            public JsonElement Settings { get; set; }
        }

        // Logs the user in, as in Recommit and ProjectLayout: who they are from their Windows sign in, their display name and email
        // from the domain, what they can do in each profile and their own screen settings, the web app does this once at the start
        [HttpGet("user/login")]
        public IActionResult Login()
        {
            try
            {
                UserAccess access = _access.Get(HttpContext);
                if (OperatingSystem.IsWindows() && User.Identity is not WindowsIdentity && User.Identity?.AuthenticationType != DevelopmentSignIn.SchemeName)
                    throw new Exception("Windows authentication is required");
                string? userId = User.FindFirst(ClaimTypes.PrimarySid)?.Value;
                if (string.IsNullOrEmpty(userId) && OperatingSystem.IsWindows() && User.Identity is WindowsIdentity identity)
                    userId = identity.User?.Value;

                // display name and email, never fail the login because of the lookup
                string displayName = UserPart(access.UserName);
                string? email = null;
                if (OperatingSystem.IsWindows() && !string.IsNullOrEmpty(userId))
                {
                    UserPrincipal? principal = FindUser(userId);
                    if (principal != null)
                        using (principal)
                        {
                            if (!string.IsNullOrWhiteSpace(principal.DisplayName))
                                displayName = principal.DisplayName;
                            else if (!string.IsNullOrWhiteSpace(principal.Name))
                                displayName = principal.Name;
                            email = string.IsNullOrWhiteSpace(principal.EmailAddress) ? null : principal.EmailAddress;
                        }
                }
                LoginDetails login = new LoginDetails
                {
                    Id = userId,
                    UserName = access.UserName,
                    DisplayName = displayName,
                    Initials = Initials(displayName),
                    Email = email,
                    SiteOwner = access.SiteOwner,
                    Designer = access.Can(Access.Designer, null),
                    Settings = SchedulerDb.RunElement(_dbConnSettings, "GetUserSettings", access.UserName)
                };
                foreach (KeyValuePair<string, Access> profile in access.Profiles)
                    login.Profiles.Add(GetRights(profile.Key, profile.Value));
                return Ok(login);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }
        // The user's own screen settings, e.g. { propertiesCollapsed }, merged into what's saved
        [HttpPut("user/settings")]
        public IActionResult SaveSettings([FromBody] JsonElement settings)
        {
            return RunSP("SaveUserSettings", _access.Get(HttpContext).UserName, settings.ValueKind == JsonValueKind.Object ? settings.GetRawText() : "{}");
        }

        [HttpGet("site-owners")]
        [ProfileAccess(Access.Site)]
        public IActionResult GetSiteOwners()
        {
            return RunSP("GetUserRoles", null, true);
        }
        [HttpPost("site-owners")]
        [ProfileAccess(Access.Site)]
        public IActionResult AddSiteOwner([FromBody] UserRoleDetails user)
        {
            return Changed(RunSP("SaveUserRole", null, user.UserName, Roles.SiteOwner, _access.Get(HttpContext).UserName));
        }
        [HttpDelete("site-owners/{userRoleId}")]
        [ProfileAccess(Access.Site)]
        public IActionResult DeleteSiteOwner(string userRoleId)
        {
            return Changed(RunSP("DeleteUserRole", null, userRoleId));
        }

        // A profile's owners, readers and solvers
        [HttpGet("profiles/{profileId}/users")]
        [ProfileAccess(Access.ManageUsers)]
        public IActionResult GetProfileUsers(string profileId)
        {
            return RunSP("GetUserRoles", profileId, false);
        }
        [HttpPost("profiles/{profileId}/users")]
        [ProfileAccess(Access.ManageUsers)]
        public IActionResult AddProfileUser(string profileId, [FromBody] UserRoleDetails user)
        {
            if (user.Role == Roles.SiteOwner)
                return BadRequest("Site owners are given on the site, not a profile");
            return Changed(RunSP("SaveUserRole", profileId, user.UserName, user.Role, _access.Get(HttpContext).UserName));
        }
        [HttpDelete("profiles/{profileId}/users/{userRoleId}")]
        [ProfileAccess(Access.ManageUsers)]
        public IActionResult DeleteProfileUser(string profileId, string userRoleId)
        {
            return Changed(RunSP("DeleteUserRole", profileId, userRoleId));
        }

        // The roles are kept for a short while, a change has to show straight away
        private IActionResult Changed(IActionResult result)
        {
            _access.Clear();
            return result;
        }
        // jsmith from DOMAIN\jsmith or jsmith@domain.com
        private static string UserPart(string name)
        {
            string user = name.Contains('\\') ? name.Substring(name.LastIndexOf('\\') + 1) : name;
            return user.Contains('@') ? user.Substring(0, user.IndexOf('@')) : user;
        }
        // JS from John Smith, JO from jsmith
        private static string Initials(string displayName)
        {
            string[] parts = displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1)
                return (parts[0].Substring(0, 1) + parts[parts.Length - 1].Substring(0, 1)).ToUpperInvariant();
            return displayName.Substring(0, Math.Min(2, displayName.Length)).ToUpperInvariant();
        }
        // Search the domain for the user that's using the api, then this machine
        [SupportedOSPlatform("windows")]
        private static UserPrincipal? FindUser(string sid)
        {
            foreach (ContextType type in new ContextType[] { ContextType.Domain, ContextType.Machine })
            {
                try
                {
                    using (PrincipalContext context = new PrincipalContext(type))
                    {
                        UserPrincipal? principal = UserPrincipal.FindByIdentity(context, IdentityType.Sid, sid);
                        if (principal != null)
                            return principal;
                    }
                }
                catch (Exception)
                {
                    // try the next context
                }
            }
            return null;
        }
        private static ProfileRights GetRights(string profileId, Access access)
        {
            return new ProfileRights
            {
                ProfileId = profileId,
                ReadModel = access.HasFlag(Access.ReadModel),
                EditModel = access.HasFlag(Access.EditModel),
                ViewSchedule = access.HasFlag(Access.ViewSchedule),
                Solve = access.HasFlag(Access.Solve),
                ManageUsers = access.HasFlag(Access.ManageUsers)
            };
        }
    }
}
