using SmeDemo.Application.Errors;

namespace SmeDemo.Api.Auth;

/// <summary>
/// The caller identity, bound once per request from the validated token. A
/// missing or malformed token is a 401 even if the authorization policy let the
/// request through, mirroring the old claim helpers.
/// </summary>
public sealed record CurrentUser(string Sub, Guid UserId, string Org, string Name)
{
    public static ValueTask<CurrentUser> BindAsync(HttpContext context)
    {
        var principal = context.User;
        if (principal.Identity?.IsAuthenticated != true)
        {
            throw new UnauthenticatedException("A bearer token is required.");
        }

        var sub = principal.FindFirst("sub")?.Value;
        if (!Guid.TryParse(sub, out var userId))
        {
            throw new UnauthenticatedException("The access token is not valid.");
        }

        return ValueTask.FromResult(new CurrentUser(
            sub!,
            userId,
            principal.FindFirst("org")?.Value ?? string.Empty,
            principal.FindFirst("name")?.Value ?? string.Empty));
    }
}
