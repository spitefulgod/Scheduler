using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;

namespace Scheduler.Api.Services
{
    // Signs every call in as Security:DevUser, for running away from the domain, e.g. a laptop or the tests
    // with Security:AllowUserHeader the X-Scheduler-User header picks the user, the tests use it to try each role
    // Program only allows it in the Development environment, a real server always uses Windows sign in
    public class DevelopmentSignIn : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "Development";
        public const string UserHeader = "X-Scheduler-User";
        private readonly IConfiguration _configuration;

        public DevelopmentSignIn(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, IConfiguration configuration)
            : base(options, logger, encoder)
        {
            _configuration = configuration;
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            string userName = _configuration["Security:DevUser"] ?? $"{Environment.UserDomainName}\\{Environment.UserName}";
            if (_configuration.GetValue<bool>("Security:AllowUserHeader") && Request.Headers.TryGetValue(UserHeader, out Microsoft.Extensions.Primitives.StringValues header) && header.ToString() != "")
                userName = header.ToString();

            ClaimsIdentity identity = new ClaimsIdentity(new Claim[] { new Claim(ClaimTypes.Name, userName) }, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
