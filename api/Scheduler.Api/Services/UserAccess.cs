using System.Security.Claims;
using System.Text.Json;

namespace Scheduler.Api.Services
{
    // What a request needs, the profile ones are checked against the profile in the route (or ?profileId=)
    [Flags]
    public enum Access
    {
        None = 0,
        // the model: connections in it, sources, entities, relationships, calendars and rules
        ReadModel = 1,
        EditModel = 2,
        // the schedule: runs, the plan, locks and the solve settings
        ViewSchedule = 4,
        Solve = 8,
        // who else can use the profile
        ManageUsers = 16,
        // site wide: any signed in user, site owners and owners of any profile (connections), site owners only
        SignedIn = 32,
        Designer = 64,
        Site = 128
    }

    // The roles a user can be given, SiteOwner is for the whole site, the rest for one profile
    public static class Roles
    {
        public const string SiteOwner = "SiteOwner";
        public const string ProjectOwner = "ProjectOwner";
        public const string ProjectReader = "ProjectReader";
        public const string ProjectSolver = "ProjectSolver";

        public const Access Owner = Access.ReadModel | Access.EditModel | Access.ViewSchedule | Access.Solve | Access.ManageUsers;
        public const Access Reader = Access.ReadModel | Access.ViewSchedule;
        // a solver only gets the schedule, never the model behind it
        public const Access Solver = Access.ViewSchedule | Access.Solve;

        public static Access GetAccess(string role)
        {
            switch (role)
            {
                case ProjectOwner:
                    return Owner;
                case ProjectReader:
                    return Reader;
                case ProjectSolver:
                    return Solver;
                default:
                    return Access.None;
            }
        }
    }

    // What one signed in user can do, worked out once per request
    public class UserAccess
    {
        private const Access ProfileNeeds = Access.ReadModel | Access.EditModel | Access.ViewSchedule | Access.Solve | Access.ManageUsers;

        public string UserName { get; }
        public bool SiteOwner { get; }
        // profile id to what the user can do in it, roles from groups are added in
        public Dictionary<string, Access> Profiles { get; }

        public UserAccess(string userName, bool siteOwner, Dictionary<string, Access> profiles)
        {
            UserName = userName;
            SiteOwner = siteOwner;
            Profiles = profiles;
        }
        public Access GetProfileAccess(string profileId)
        {
            if (SiteOwner)
                return Roles.Owner;
            return Profiles.TryGetValue(profileId, out Access access) ? access : Access.None;
        }
        // Without a profile a profile need is met by having it on any profile, e.g. the built in value scales
        public bool Can(Access need, string? profileId)
        {
            if (need == Access.None || need == Access.SignedIn || SiteOwner)
                return true;
            if (need == Access.Site)
                return false;
            if (need == Access.Designer)
                return Profiles.Values.Any(x => x.HasFlag(Access.ManageUsers));
            if ((need & ProfileNeeds) != need)
                return false;
            if (string.IsNullOrEmpty(profileId))
                return Profiles.Values.Any(x => (x & need) == need);
            return (GetProfileAccess(profileId) & need) == need;
        }
    }

    // Reads the roles from SchedulerMeta and works out each request's UserAccess
    // the roles are kept for a short while, changing them through the API clears that straight away
    public class UserAccessService
    {
        private static readonly TimeSpan KeepRoles = TimeSpan.FromSeconds(30);
        private const string ItemKey = "Scheduler.UserAccess";
        private readonly string _dbConnSettings;
        private readonly string[] _siteOwners;
        private readonly object _lock = new object();
        private List<UserRole>? _roles;
        private DateTime _rolesRead;

        public class UserRole
        {
            public string Id { get; set; } = "";
            public string UserName { get; set; } = "";
            public string? ProfileId { get; set; }
            public string Role { get; set; } = "";
        }

        public UserAccessService(IConfiguration configuration)
        {
            _dbConnSettings = configuration["SchedulerDBConn"]!.ToString();
            // site owners in the config can't be locked out by a change in the database
            _siteOwners = configuration.GetSection("Security:SiteOwners").Get<string[]>() ?? new string[0];
        }

        // The signed in user's access, read once per request
        public UserAccess Get(HttpContext context)
        {
            if (context.Items.TryGetValue(ItemKey, out object? saved) && saved is UserAccess access)
                return access;
            access = Build(context.User);
            context.Items[ItemKey] = access;
            return access;
        }
        public void Clear()
        {
            lock (_lock)
                _roles = null;
        }

        private UserAccess Build(ClaimsPrincipal user)
        {
            string userName = GetUserName(user);
            if (userName == "")
                return new UserAccess("", false, new Dictionary<string, Access>());

            List<UserRole> roles = GetRoles();
            // a new site has no owner, the first user in becomes it unless the config names them
            if (_siteOwners.Length == 0 && !roles.Any(x => x.Role == Roles.SiteOwner))
            {
                roles = ReadRoles("ClaimSiteOwner", userName);
                lock (_lock)
                {
                    _roles = roles;
                    _rolesRead = DateTime.UtcNow;
                }
            }

            bool siteOwner = _siteOwners.Any(x => IsUser(user, userName, x));
            Dictionary<string, Access> profiles = new Dictionary<string, Access>();
            foreach (UserRole role in roles)
            {
                if (!IsUser(user, userName, role.UserName))
                    continue;
                if (role.Role == Roles.SiteOwner)
                    siteOwner = true;
                else if (role.ProfileId != null)
                    profiles[role.ProfileId] = (profiles.TryGetValue(role.ProfileId, out Access had) ? had : Access.None) | Roles.GetAccess(role.Role);
            }
            return new UserAccess(userName, siteOwner, profiles);
        }
        public static string GetUserName(ClaimsPrincipal user)
        {
            return user.Identity?.IsAuthenticated == true ? user.Identity.Name ?? "" : "";
        }
        // The role's user is this user, or a domain group they're in
        private static bool IsUser(ClaimsPrincipal user, string userName, string roleUser)
        {
            if (string.Equals(userName, roleUser, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!roleUser.Contains('\\'))
                return false;
            try
            {
                return user.IsInRole(roleUser);
            }
            catch
            {
                // a name the domain doesn't know isn't a group the user is in
                return false;
            }
        }
        private List<UserRole> GetRoles()
        {
            lock (_lock)
                if (_roles != null && DateTime.UtcNow - _rolesRead < KeepRoles)
                    return _roles;
            List<UserRole> roles = ReadRoles("GetUserRoles");
            lock (_lock)
            {
                _roles = roles;
                _rolesRead = DateTime.UtcNow;
            }
            return roles;
        }
        private List<UserRole> ReadRoles(string procedure, params object?[] args)
        {
            string json = SchedulerDb.Run(_dbConnSettings, procedure, args) ?? "[]";
            return JsonSerializer.Deserialize<List<UserRole>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<UserRole>();
        }
    }
}
