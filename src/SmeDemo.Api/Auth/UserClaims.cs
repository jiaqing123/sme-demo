using System.Security.Claims;
using SmeDemo.Application.Errors;

namespace SmeDemo.Api.Auth;

public static class UserClaims
{
    public static string GetId(ClaimsPrincipal principal) =>
        principal.FindFirst("sub")?.Value
        ?? throw new UnauthenticatedException("A bearer token is required.");

    public static Guid GetUserId(ClaimsPrincipal principal) =>
        Guid.TryParse(GetId(principal), out var id)
            ? id
            : throw new UnauthenticatedException("The access token is not valid.");

    public static string GetOrg(ClaimsPrincipal principal) =>
        principal.FindFirst("org")?.Value
        ?? throw new UnauthenticatedException("The access token is not valid.");

    public static string GetName(ClaimsPrincipal principal) =>
        principal.FindFirst("name")?.Value ?? string.Empty;
}
