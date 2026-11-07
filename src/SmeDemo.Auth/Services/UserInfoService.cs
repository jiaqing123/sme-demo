using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SmeDemo.Auth.Configuration;
using SmeDemo.Auth.Contracts;
using SmeDemo.Auth.Errors;
using SmeDemo.Auth.Persistence;
using SmeDemo.Auth.Security;

namespace SmeDemo.Auth.Services;

/// <summary>
/// Reads the profile behind a bearer token for the profile screen. The token is
/// verified here against the issuer's own public keys rather than by calling the
/// service back over HTTP; both the current and the previous key are accepted so
/// a key rotation cannot lock a valid token out.
/// </summary>
public sealed class UserInfoService
{
    private static readonly JsonWebTokenHandler Handler = new();

    private readonly AuthDbContext _db;
    private readonly TokenValidationParameters _validationParameters;

    public UserInfoService(AuthDbContext db, SigningKeyStore keys, AuthOptions options)
    {
        _db = db;
        _validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = options.JwtIssuer,
            ValidateAudience = true,
            ValidAudience = options.JwtAudience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = keys.ValidationKeys,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
        };
    }

    public async Task<OperationResult<UserInfoResponse>> GetAsync(
        string? authorizationHeader,
        CancellationToken cancellationToken)
    {
        if (!TryReadBearerToken(authorizationHeader, out var token))
        {
            return OperationResult<UserInfoResponse>.Failure(
                ApiProblem.Unauthenticated("A bearer token is required."));
        }

        var validation = await Handler.ValidateTokenAsync(token, _validationParameters);
        if (!validation.IsValid)
        {
            return OperationResult<UserInfoResponse>.Failure(
                validation.Exception is SecurityTokenExpiredException
                    ? ApiProblem.TokenExpired()
                    : ApiProblem.InvalidToken());
        }

        if (!validation.Claims.TryGetValue("sub", out var subject) ||
            subject is not string subjectText ||
            !Guid.TryParse(subjectText, out var userId))
        {
            return OperationResult<UserInfoResponse>.Failure(ApiProblem.InvalidToken());
        }

        var user = await _db.Users.AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == userId && candidate.Status == AuthUser.ActiveStatus,
                cancellationToken);

        if (user is null)
        {
            return OperationResult<UserInfoResponse>.Failure(ApiProblem.InvalidToken());
        }

        return OperationResult<UserInfoResponse>.Success(
            new UserInfoResponse(user.Id.ToString(), user.Name, user.Phone, user.OrgId));
    }

    private static bool TryReadBearerToken(string? authorizationHeader, out string token)
    {
        const string prefix = "Bearer ";
        token = string.Empty;

        if (authorizationHeader is null ||
            !authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        token = authorizationHeader[prefix.Length..].Trim();
        return token.Length > 0;
    }
}
