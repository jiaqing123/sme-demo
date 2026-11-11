using Microsoft.AspNetCore.Authorization;

namespace SmeDemo.Api.Auth;

public sealed class OrgAuthorizationHandler : AuthorizationHandler<OrgRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        OrgRequirement requirement)
    {
        var org = context.User.FindFirst("org")?.Value;
        if (string.Equals(org, requirement.OrgId, StringComparison.Ordinal))
        {
            context.Succeed(requirement);
        }
        else
        {
            context.Fail(new AuthorizationFailureReason(
                this,
                "The token does not belong to this organisation."));
        }

        return Task.CompletedTask;
    }
}
