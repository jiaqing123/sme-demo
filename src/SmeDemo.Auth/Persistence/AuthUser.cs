namespace SmeDemo.Auth.Persistence;

/// <summary>
/// A credential holder for the issuer. This type deliberately lives in the auth
/// service: it has no shared domain model with the resource API.
/// </summary>
public sealed class AuthUser
{
    public const string ActiveStatus = "active";
    public const string DisabledStatus = "disabled";

    private AuthUser()
    {
        // EF Core.
    }

    public Guid Id { get; private set; }

    public string OrgId { get; private set; } = string.Empty;

    public string Username { get; private set; } = string.Empty;

    public string PasswordHash { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string Phone { get; private set; } = string.Empty;

    public string Status { get; private set; } = ActiveStatus;

    public DateTime CreatedAt { get; private set; }

    public bool IsActive => string.Equals(Status, ActiveStatus, StringComparison.Ordinal);

    public static AuthUser Create(
        Guid id,
        string orgId,
        string username,
        string name,
        string phone,
        DateTime createdAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orgId);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(phone);

        return new AuthUser
        {
            Id = id,
            OrgId = orgId,
            Username = username,
            Name = name,
            Phone = phone,
            Status = ActiveStatus,
            CreatedAt = createdAtUtc,
        };
    }

    public void SetPasswordHash(string passwordHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        PasswordHash = passwordHash;
    }

    /// <summary>
    /// Keeps the demo account in step with the environment values. Without this,
    /// changing <c>DEMO_USERNAME</c>, <c>DEMO_PHONE</c>, or <c>ORG_ID</c> would
    /// silently affect only a freshly seeded database.
    /// </summary>
    public void UpdateProfile(string orgId, string username, string name, string phone)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orgId);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(phone);

        OrgId = orgId;
        Username = username;
        Name = name;
        Phone = phone;
    }
}
