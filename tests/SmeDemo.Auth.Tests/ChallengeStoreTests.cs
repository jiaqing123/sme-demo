using SmeDemo.Auth.Challenges;

namespace SmeDemo.Auth.Tests;

[TestClass]
public sealed class ChallengeStoreTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.Parse("7f3c2a10-0000-4000-8000-000000000001");

    [TestMethod]
    public void Create_UsesFiveMinuteTtlAndSixtySecondResendCooldown()
    {
        var (store, _) = CreateStore();

        var ticket = store.Create(UserId, "123456");

        Assert.AreEqual(TimeSpan.FromMinutes(5), ticket.ExpiresIn);
        Assert.AreEqual(TimeSpan.FromSeconds(60), ticket.ResendIn);
    }

    [TestMethod]
    public void Verify_WithCorrectCode_SucceedsAndCannotBeReused()
    {
        var (store, _) = CreateStore();
        var ticket = store.Create(UserId, "123456");

        var first = store.Verify(ticket.Id, "123456");
        var second = store.Verify(ticket.Id, "123456");

        Assert.AreEqual(ChallengeVerificationOutcome.Success, first.Outcome);
        Assert.AreEqual(UserId, first.UserId);
        Assert.AreEqual(ChallengeVerificationOutcome.NotFound, second.Outcome);
    }

    [TestMethod]
    public void Verify_ExpiresAfterFiveMinutes()
    {
        var (store, clock) = CreateStore();
        var ticket = store.Create(UserId, "123456");

        clock.Advance(TimeSpan.FromMinutes(5));

        Assert.AreEqual(ChallengeVerificationOutcome.Expired, store.Verify(ticket.Id, "123456").Outcome);
    }

    [TestMethod]
    public void Verify_ThreeWrongAttemptsDestroyChallenge()
    {
        var (store, _) = CreateStore();
        var ticket = store.Create(UserId, "123456");

        var first = store.Verify(ticket.Id, "000000");
        var second = store.Verify(ticket.Id, "000000");
        var third = store.Verify(ticket.Id, "000000");
        var correctAfterLockout = store.Verify(ticket.Id, "123456");

        Assert.AreEqual(ChallengeVerificationOutcome.InvalidCode, first.Outcome);
        Assert.AreEqual(2, first.AttemptsRemaining);
        Assert.AreEqual(ChallengeVerificationOutcome.InvalidCode, second.Outcome);
        Assert.AreEqual(1, second.AttemptsRemaining);
        Assert.AreEqual(ChallengeVerificationOutcome.TooManyAttempts, third.Outcome);
        Assert.AreEqual(ChallengeVerificationOutcome.NotFound, correctAfterLockout.Outcome);
    }

    [TestMethod]
    public void Resend_InsideCooldownIsRejected()
    {
        var (store, clock) = CreateStore();
        var ticket = store.Create(UserId, "123456");

        clock.Advance(TimeSpan.FromSeconds(59));
        var result = store.Resend(ticket.Id, "654321");

        Assert.AreEqual(ChallengeResendOutcome.Cooldown, result.Outcome);
        Assert.AreEqual(TimeSpan.FromSeconds(1), result.RetryAfter);
    }

    [TestMethod]
    public void Resend_AfterCooldownIssuesNewCodeAndResetsCooldown()
    {
        var (store, clock) = CreateStore();
        var ticket = store.Create(UserId, "123456");

        clock.Advance(TimeSpan.FromSeconds(60));
        var resent = store.Resend(ticket.Id, "654321");

        Assert.AreEqual(ChallengeResendOutcome.Success, resent.Outcome);
        Assert.AreEqual("654321", resent.Code);
        Assert.AreEqual(UserId, resent.UserId);
        Assert.AreEqual(TimeSpan.FromSeconds(60), resent.RetryAfter);

        var oldCode = store.Verify(ticket.Id, "123456");
        Assert.AreEqual(ChallengeVerificationOutcome.InvalidCode, oldCode.Outcome);

        var newCode = store.Verify(ticket.Id, "654321");
        Assert.AreEqual(ChallengeVerificationOutcome.Success, newCode.Outcome);
    }

    [TestMethod]
    public void Resend_AfterExpiryIsRejected()
    {
        var (store, clock) = CreateStore();
        var ticket = store.Create(UserId, "123456");

        clock.Advance(TimeSpan.FromMinutes(5));

        Assert.AreEqual(ChallengeResendOutcome.Expired, store.Resend(ticket.Id, "654321").Outcome);
    }

    [TestMethod]
    public async Task Verify_ConcurrentRequestsYieldExactlyOneSuccess()
    {
        var (store, _) = CreateStore();
        var ticket = store.Create(UserId, "123456");

        var results = await Task.WhenAll(
            Enumerable.Range(0, 32).Select(_ => Task.Run(() => store.Verify(ticket.Id, "123456"))));

        Assert.AreEqual(
            1,
            results.Count(result => result.Outcome == ChallengeVerificationOutcome.Success));
    }

    private static (ChallengeStore Store, ManualTimeProvider Clock) CreateStore()
    {
        var clock = new ManualTimeProvider(Start);
        return (new ChallengeStore(clock), clock);
    }
}
