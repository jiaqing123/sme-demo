using Microsoft.AspNetCore.Authorization;

namespace SmeDemo.Api.Auth;

/// <summary>
/// Requirements §5.3: a token is only a permission to see data once its
/// <c>org</c> equals the configured <c>ORG_ID</c>. Handlers still scope every
/// query by <c>sub</c>.
/// </summary>
public sealed record OrgRequirement(string OrgId) : IAuthorizationRequirement;
