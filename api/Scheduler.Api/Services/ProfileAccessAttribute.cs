using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Scheduler.Api.Services
{
    // What a controller or action needs, the read need is for GET and the write need for everything else
    // an action's own attribute wins over its controller's, the profile comes from the route or ?profileId=
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public class ProfileAccessAttribute : Attribute, IAuthorizationFilter
    {
        public Access Read { get; }
        public Access Write { get; }

        public ProfileAccessAttribute(Access access)
        {
            Read = access;
            Write = access;
        }
        public ProfileAccessAttribute(Access read, Access write)
        {
            Read = read;
            Write = write;
        }

        public void OnAuthorization(AuthorizationFilterContext context)
        {
            // only the nearest attribute decides, the action's metadata comes after its controller's
            ProfileAccessAttribute? nearest = context.ActionDescriptor.EndpointMetadata.OfType<ProfileAccessAttribute>().LastOrDefault();
            if (!ReferenceEquals(nearest, this))
                return;

            UserAccess access = context.HttpContext.RequestServices.GetRequiredService<UserAccessService>().Get(context.HttpContext);
            if (access.UserName == "")
            {
                context.Result = new ChallengeResult();
                return;
            }
            string? profileId = context.RouteData.Values.TryGetValue("profileId", out object? routeProfileId) ? routeProfileId?.ToString() : null;
            if (string.IsNullOrEmpty(profileId))
                profileId = context.HttpContext.Request.Query["profileId"].FirstOrDefault();

            Access need = HttpMethods.IsGet(context.HttpContext.Request.Method) ? Read : Write;
            if (!access.Can(need, profileId))
                context.Result = new ContentResult { StatusCode = StatusCodes.Status403Forbidden, Content = GetRefusal(need), ContentType = "text/plain" };
        }
        private static string GetRefusal(Access need)
        {
            switch (need)
            {
                case Access.Site:
                    return "Only a site owner can do this";
                case Access.Designer:
                    return "Only a site owner or a project owner can do this";
                case Access.ManageUsers:
                case Access.EditModel:
                    return "Only the project's owners can change this";
                case Access.Solve:
                    return "Only the project's owners and solvers can do this";
                default:
                    return "You don't have access to this part of the project";
            }
        }
    }

    // Every action has to say what it needs, one that doesn't is refused rather than left open
    public class AccessRequiredFilter : IAuthorizationFilter
    {
        public void OnAuthorization(AuthorizationFilterContext context)
        {
            IList<object> metadata = context.ActionDescriptor.EndpointMetadata;
            if (!metadata.OfType<ProfileAccessAttribute>().Any() && !metadata.OfType<IAllowAnonymous>().Any())
                context.Result = new ContentResult { StatusCode = StatusCodes.Status403Forbidden, Content = "This call has no access rule", ContentType = "text/plain" };
        }
    }
}
