using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SmeDemo.Auth.Configuration;
using SmeDemo.Auth.Persistence;

namespace SmeDemo.Auth.Security;

/// <summary>
/// Issues the exact claim set from requirements §6.6. There is deliberately no
/// phone number and no role claim.
/// </summary>
public sealed class JwtTokenIssuer
{
    private static readonly JsonWebTokenHandler Handler = new()
    {
        // The token contract is exactly iss/aud/sub/org/name/iat/exp/jti. Without
        // this, the handler also emits an nbf claim.
        SetDefaultTimesOnTokenCreation = false,
    };

    private readonly SigningKeyStore _keys;
    private readonly AuthOptions _options;
    private readonly TimeProvider _clock;
    private readonly Lock _signingLock = new();

    public JwtTokenIssuer(SigningKeyStore keys, AuthOptions options, TimeProvider clock)
    {
        _keys = keys;
        _options = options;
        _clock = clock;
    }

    public IssuedToken Issue(AuthUser user)
    {
        ArgumentNullException.ThrowIfNull(user);

        var issuedAt = _clock.GetUtcNow();
        var expiresAt = issuedAt.Add(_options.JwtTtl);
        var tokenId = Guid.NewGuid().ToString();

        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["sub"] = user.Id.ToString(),
            ["org"] = user.OrgId,
            ["name"] = user.Name,
            ["jti"] = tokenId,
        };

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.JwtIssuer,
            Audience = _options.JwtAudience,
            IssuedAt = issuedAt.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            Claims = claims,
            SigningCredentials = new SigningCredentials(
                _keys.SigningKey,
                SecurityAlgorithms.RsaSha256),
        };

        string token;
        lock (_signingLock)
        {
            token = Handler.CreateToken(descriptor);
        }

        return new IssuedToken(token, (int)Math.Ceiling(_options.JwtTtl.TotalSeconds), tokenId);
    }
}

public readonly record struct IssuedToken(string AccessToken, int ExpiresIn, string JwtId);
