using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace SmeDemo.Auth.Challenges;

/// <summary>
/// In-memory, thread-safe challenge store implementing requirements §6.3:
/// a 5 minute TTL, three verification attempts, a 60 second resend cooldown and
/// single use. A restart drops pending challenges, which is accepted for the
/// demo.
/// </summary>
public sealed class ChallengeStore
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ResendCooldown = TimeSpan.FromSeconds(60);
    public const int MaxAttempts = 3;

    private readonly ConcurrentDictionary<Guid, Challenge> _challenges = new();
    private readonly TimeProvider _clock;

    public ChallengeStore(TimeProvider clock) => _clock = clock;

    public ChallengeTicket Create(Guid userId, string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        var now = _clock.GetUtcNow();
        PruneExpired(now);

        var challenge = new Challenge(userId, code, now.Add(Ttl), now.Add(ResendCooldown));
        _challenges[challenge.Id] = challenge;

        return new ChallengeTicket(challenge.Id, code, Ttl, ResendCooldown);
    }

    public ChallengeVerification Verify(Guid challengeId, string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        if (!_challenges.TryGetValue(challengeId, out var challenge))
        {
            return new ChallengeVerification(ChallengeVerificationOutcome.NotFound);
        }

        lock (challenge.Sync)
        {
            // A concurrent verification may have consumed the challenge after
            // this thread read it from the dictionary. The flag makes success
            // single-use even when two requests overlap.
            if (challenge.Consumed)
            {
                return new ChallengeVerification(ChallengeVerificationOutcome.NotFound);
            }

            var now = _clock.GetUtcNow();
            if (now >= challenge.ExpiresAt)
            {
                _challenges.TryRemove(challenge.Id, out _);
                return new ChallengeVerification(ChallengeVerificationOutcome.Expired);
            }

            if (challenge.AttemptsRemaining <= 0)
            {
                _challenges.TryRemove(challenge.Id, out _);
                return new ChallengeVerification(ChallengeVerificationOutcome.TooManyAttempts);
            }

            if (!CodesEqual(challenge.Code, code))
            {
                challenge.AttemptsRemaining--;
                if (challenge.AttemptsRemaining == 0)
                {
                    _challenges.TryRemove(challenge.Id, out _);
                    return new ChallengeVerification(ChallengeVerificationOutcome.TooManyAttempts);
                }

                return new ChallengeVerification(
                    ChallengeVerificationOutcome.InvalidCode,
                    AttemptsRemaining: challenge.AttemptsRemaining);
            }

            // Single use: a successful verification destroys the challenge.
            challenge.Consumed = true;
            _challenges.TryRemove(challenge.Id, out _);
            return new ChallengeVerification(ChallengeVerificationOutcome.Success, challenge.UserId);
        }
    }

    public ChallengeResend Resend(Guid challengeId, string newCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newCode);

        if (!_challenges.TryGetValue(challengeId, out var challenge))
        {
            return new ChallengeResend(ChallengeResendOutcome.NotFound);
        }

        lock (challenge.Sync)
        {
            var now = _clock.GetUtcNow();
            if (now >= challenge.ExpiresAt)
            {
                _challenges.TryRemove(challenge.Id, out _);
                return new ChallengeResend(ChallengeResendOutcome.Expired);
            }

            if (now < challenge.ResendAvailableAt)
            {
                return new ChallengeResend(
                    ChallengeResendOutcome.Cooldown,
                    RetryAfter: challenge.ResendAvailableAt - now);
            }

            challenge.Code = newCode;
            challenge.ResendAvailableAt = now.Add(ResendCooldown);

            return new ChallengeResend(
                ChallengeResendOutcome.Success,
                newCode,
                UserId: challenge.UserId,
                ExpiresIn: challenge.ExpiresAt - now,
                RetryAfter: ResendCooldown);
        }
    }

    private void PruneExpired(DateTimeOffset now)
    {
        foreach (var entry in _challenges)
        {
            if (now >= entry.Value.ExpiresAt)
            {
                _challenges.TryRemove(entry.Key, out _);
            }
        }
    }

    private static bool CodesEqual(string expected, string actual)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);

        return expectedBytes.Length == actualBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private sealed class Challenge
    {
        public Challenge(Guid userId, string code, DateTimeOffset expiresAt, DateTimeOffset resendAvailableAt)
        {
            UserId = userId;
            Code = code;
            ExpiresAt = expiresAt;
            ResendAvailableAt = resendAvailableAt;
        }

        public object Sync { get; } = new();

        public Guid Id { get; } = Guid.NewGuid();

        public Guid UserId { get; }

        public string Code { get; set; }

        public bool Consumed { get; set; }

        public DateTimeOffset ExpiresAt { get; }

        public DateTimeOffset ResendAvailableAt { get; set; }

        public int AttemptsRemaining { get; set; } = MaxAttempts;
    }
}

public readonly record struct ChallengeTicket(
    Guid Id,
    string Code,
    TimeSpan ExpiresIn,
    TimeSpan ResendIn);

public enum ChallengeVerificationOutcome
{
    Success,
    NotFound,
    Expired,
    InvalidCode,
    TooManyAttempts,
}

public readonly record struct ChallengeVerification(
    ChallengeVerificationOutcome Outcome,
    Guid UserId = default,
    int AttemptsRemaining = 0);

public enum ChallengeResendOutcome
{
    Success,
    NotFound,
    Expired,
    Cooldown,
}

public readonly record struct ChallengeResend(
    ChallengeResendOutcome Outcome,
    string? Code = null,
    Guid UserId = default,
    TimeSpan ExpiresIn = default,
    TimeSpan RetryAfter = default);
