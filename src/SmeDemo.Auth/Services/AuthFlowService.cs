using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SmeDemo.Auth.Challenges;
using SmeDemo.Auth.Configuration;
using SmeDemo.Auth.Contracts;
using SmeDemo.Auth.Errors;
using SmeDemo.Auth.Persistence;
using SmeDemo.Auth.RateLimiting;
using SmeDemo.Auth.Security;
using SmeDemo.Auth.Sms;

namespace SmeDemo.Auth.Services;

/// <summary>
/// The login, verify and resend use cases. Endpoints only bind and translate the
/// result; all flow logic lives here.
/// </summary>
public sealed class AuthFlowService
{
    // Verified whenever the username is unknown, so both branches pay the same
    // PBKDF2 cost and response time does not reveal whether an account exists.
    private static readonly AuthUser TimingDecoy = AuthUser.Create(
        Guid.Empty,
        "timing",
        "timing-decoy",
        "Timing Decoy",
        "+61000000000",
        DateTime.UnixEpoch);

    private static readonly string TimingDecoyHash =
        new PasswordHasher<AuthUser>().HashPassword(TimingDecoy, "not-a-real-password");

    private readonly AuthDbContext _db;
    private readonly ChallengeStore _challenges;
    private readonly IPasswordHasher<AuthUser> _passwordHasher;
    private readonly JwtTokenIssuer _tokenIssuer;
    private readonly ISmsProvider _smsProvider;
    private readonly AuthOptions _options;
    private readonly LoginAccountRateLimiter _accountRateLimiter;
    private readonly ILogger<AuthFlowService> _logger;

    public AuthFlowService(
        AuthDbContext db,
        ChallengeStore challenges,
        IPasswordHasher<AuthUser> passwordHasher,
        JwtTokenIssuer tokenIssuer,
        ISmsProvider smsProvider,
        AuthOptions options,
        LoginAccountRateLimiter accountRateLimiter,
        ILogger<AuthFlowService> logger)
    {
        _db = db;
        _challenges = challenges;
        _passwordHasher = passwordHasher;
        _tokenIssuer = tokenIssuer;
        _smsProvider = smsProvider;
        _options = options;
        _accountRateLimiter = accountRateLimiter;
        _logger = logger;
    }

    public async Task<OperationResult<ChallengeResponse>> LoginAsync(
        LoginRequest request,
        CancellationToken cancellationToken)
    {
        if (RequestValidation.Validate(request) is { } validationErrors)
        {
            return OperationResult<ChallengeResponse>.Failure(ApiProblem.Validation(validationErrors));
        }

        var username = request.Username!.Trim();

        // Per-account budget: checked only after the request shape is valid so a
        // malformed body cannot consume a real account's attempts. The key is
        // normalized here and never looked up or reflected in the response, so
        // the 429 does not reveal whether the account exists.
        if (!_accountRateLimiter.TryAcquire(username.ToLowerInvariant(), out var retryAfter))
        {
            _logger.LogWarning("Login rejected by the account rate limit.");
            return OperationResult<ChallengeResponse>.Failure(
                ApiProblem.RateLimited("Too many login attempts. Try again later.", retryAfter));
        }

        var user = await FindUserAsync(username, cancellationToken);

        // In demo mode only the configured username is accepted. The password is
        // still checked against the seeded PBKDF2 hash, never against plaintext.
        if (_options.DemoMode &&
            !string.Equals(username, _options.DemoUsername, StringComparison.Ordinal))
        {
            user = null;
        }

        var passwordMatches = _passwordHasher.VerifyHashedPassword(
            user ?? TimingDecoy,
            user?.PasswordHash ?? TimingDecoyHash,
            request.Password!) != PasswordVerificationResult.Failed;

        if (user is null || !user.IsActive || !passwordMatches)
        {
            _logger.LogWarning("Login rejected.");
            return OperationResult<ChallengeResponse>.Failure(
                ApiProblem.Unauthenticated("The username or password is incorrect."));
        }

        var code = _options.DemoMode ? _options.DemoOtp : GenerateCode();

        // Send before creating the challenge: a failed send must not leave an
        // orphan challenge the user can never complete.
        try
        {
            await SendCodeAsync(user.Phone, code, cancellationToken);
        }
        catch (SmsDeliveryException)
        {
            // No code, phone number, or provider body in the log.
            _logger.LogWarning("Login failed because the SMS provider is unavailable.");
            return OperationResult<ChallengeResponse>.Failure(ApiProblem.UpstreamUnavailable());
        }

        var ticket = _challenges.Create(user.Id, code);

        return OperationResult<ChallengeResponse>.Success(
            new ChallengeResponse(
                ticket.Id.ToString(),
                (int)Math.Ceiling(ticket.ExpiresIn.TotalSeconds),
                (int)Math.Ceiling(ticket.ResendIn.TotalSeconds)));
    }

    public async Task<OperationResult<TokenResponse>> VerifyAsync(
        string challengeId,
        VerifyRequest request,
        CancellationToken cancellationToken)
    {
        if (RequestValidation.Validate(request) is { } validationErrors)
        {
            return OperationResult<TokenResponse>.Failure(ApiProblem.Validation(validationErrors));
        }

        if (!Guid.TryParse(challengeId, out var id))
        {
            return OperationResult<TokenResponse>.Failure(
                ApiProblem.Unauthenticated("The challenge is no longer valid."));
        }

        var verification = _challenges.Verify(id, request.Code!);

        switch (verification.Outcome)
        {
            case ChallengeVerificationOutcome.Success:
                break;
            case ChallengeVerificationOutcome.InvalidCode:
                return OperationResult<TokenResponse>.Failure(
                    ApiProblem.Unauthenticated("The verification code is incorrect."));
            case ChallengeVerificationOutcome.TooManyAttempts:
                return OperationResult<TokenResponse>.Failure(
                    ApiProblem.RateLimited("Too many verification attempts. Request a new code."));
            case ChallengeVerificationOutcome.NotFound:
            case ChallengeVerificationOutcome.Expired:
            default:
                return OperationResult<TokenResponse>.Failure(
                    ApiProblem.Unauthenticated("The challenge has expired or is no longer valid."));
        }

        var user = await _db.Users.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == verification.UserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            return OperationResult<TokenResponse>.Failure(
                ApiProblem.Unauthenticated("The account is not available."));
        }

        var token = _tokenIssuer.Issue(user);

        return OperationResult<TokenResponse>.Success(
            new TokenResponse(token.AccessToken, "Bearer", token.ExpiresIn));
    }

    public async Task<OperationResult<ChallengeResponse>> ResendAsync(
        string challengeId,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(challengeId, out var id))
        {
            return OperationResult<ChallengeResponse>.Failure(
                ApiProblem.Unauthenticated("The challenge is no longer valid."));
        }

        var code = _options.DemoMode ? _options.DemoOtp : GenerateCode();
        var result = _challenges.Resend(id, code);

        switch (result.Outcome)
        {
            case ChallengeResendOutcome.Success:
                break;
            case ChallengeResendOutcome.Cooldown:
                return OperationResult<ChallengeResponse>.Failure(
                    ApiProblem.RateLimited(
                        $"Wait {Math.Ceiling(result.RetryAfter.TotalSeconds)} seconds before resending.",
                        result.RetryAfter));
            case ChallengeResendOutcome.NotFound:
            case ChallengeResendOutcome.Expired:
            default:
                return OperationResult<ChallengeResponse>.Failure(
                    ApiProblem.Unauthenticated("The challenge has expired or is no longer valid."));
        }

        var user = await _db.Users.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == result.UserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            return OperationResult<ChallengeResponse>.Failure(
                ApiProblem.Unauthenticated("The account is not available."));
        }

        try
        {
            await SendCodeAsync(user.Phone, result.Code!, cancellationToken);
        }
        catch (SmsDeliveryException)
        {
            // The challenge already holds the new code (ChallengeStore semantics
            // are unchanged); the user waits out the cooldown and resends.
            _logger.LogWarning("Resend failed because the SMS provider is unavailable.");
            return OperationResult<ChallengeResponse>.Failure(ApiProblem.UpstreamUnavailable());
        }

        return OperationResult<ChallengeResponse>.Success(
            new ChallengeResponse(
                id.ToString(),
                (int)Math.Ceiling(result.ExpiresIn.TotalSeconds),
                (int)Math.Ceiling(result.RetryAfter.TotalSeconds)));
    }

    private async Task<AuthUser?> FindUserAsync(string username, CancellationToken cancellationToken) =>
        await _db.Users.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Username == username, cancellationToken);

    private async Task SendCodeAsync(string phoneNumber, string code, CancellationToken cancellationToken)
    {
        var message = $"Your SME Demo verification code is {code}. It expires in 5 minutes.";
        await _smsProvider.Send(phoneNumber, message, cancellationToken);
    }

    private static string GenerateCode() =>
        RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
}
