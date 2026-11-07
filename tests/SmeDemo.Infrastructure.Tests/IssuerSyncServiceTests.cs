using Microsoft.VisualStudio.TestTools.UnitTesting;
using SmeDemo.Application.Abstractions;
using SmeDemo.Application.Features.Transactions;
using SmeDemo.Domain.Entities;

namespace SmeDemo.Infrastructure.Tests;

[TestClass]
public sealed class IssuerSyncServiceTests
{
    [TestMethod]
    public async Task SyncOnce_WritesValidatedRecordsAndMarksEveryCardOk()
    {
        await using var host = SyncTestHost.Create();
        var first = SyncTestHost.Transaction(SyncTestHost.FirstCardId, 1);
        var second = SyncTestHost.Transaction(SyncTestHost.SecondCardId, 15);
        var third = SyncTestHost.Transaction(SyncTestHost.ThirdCardId, 28);

        host.Issuer.Responses[SyncTestHost.FirstCardId] = [first];
        host.Issuer.Responses[SyncTestHost.SecondCardId] = [second];
        host.Issuer.Responses[SyncTestHost.ThirdCardId] = [third];

        await host.Service.SyncOnceAsync(CancellationToken.None);

        Assert.AreEqual(3, host.Store.Records.Count);
        var stored = host.Store.Records.Single(record => record.TransactionId == first.TransactionId);
        Assert.AreEqual(
            TransactionKeys.CreateRowKey(first.OccurredAt, first.TransactionId),
            stored.RowKey);
        Assert.AreEqual(first.PostedAt, stored.PostedAt);
        Assert.AreEqual(first.RawPayload, stored.IssuerPayload);

        var rows = await host.GetSyncRowsAsync();
        Assert.AreEqual(3, rows.Length);
        Assert.IsTrue(rows.All(row => row.LastStatus == IssuerSyncStatus.Ok));
        Assert.IsTrue(rows.All(row => row.LastError is null));
        Assert.IsTrue(rows.All(row => row.LastSyncedAt > DateTime.UtcNow.AddMinutes(-1)));
    }

    [TestMethod]
    public async Task SyncOnce_IssuerFailure_RecordsFailedRowAndDoesNotWriteStore()
    {
        await using var host = SyncTestHost.Create();
        host.Issuer.Failure = new InvalidOperationException(
            $"issuer exploded with key {SyncTestHost.ApiKey} " + new string('x', 600));

        // The contract: a failed issuer call is handled, not thrown.
        await host.Service.SyncOnceAsync(CancellationToken.None);

        Assert.AreEqual(0, host.Store.UpsertCalls);
        Assert.AreEqual(0, host.Store.Records.Count);

        var rows = await host.GetSyncRowsAsync();
        Assert.AreEqual(3, rows.Length);
        Assert.IsTrue(rows.All(row => row.LastStatus == IssuerSyncStatus.Failed));
        Assert.IsTrue(rows.All(row => row.LastError is not null));
        Assert.IsTrue(rows.All(row => row.LastError!.Length <= 512));
        Assert.IsTrue(rows.All(row => !row.LastError!.Contains(SyncTestHost.ApiKey, StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow("wrong-card")]
    [DataRow("bad-direction")]
    [DataRow("non-positive-amount")]
    [DataRow("empty-merchant")]
    [DataRow("too-many")]
    public async Task SyncOnce_InvalidPayload_FailsWholeCardWithoutWritingIt(string failure)
    {
        await using var host = SyncTestHost.Create();
        var valid = SyncTestHost.Transaction(SyncTestHost.FirstCardId, 1);

        IReadOnlyList<IssuerTransaction> invalid = failure switch
        {
            "wrong-card" => [valid with { CardId = SyncTestHost.SecondCardId }],
            "bad-direction" => [valid with { Direction = "sideways" }],
            "non-positive-amount" => [valid with { AmountMinor = 0 }],
            "empty-merchant" => [valid with { Merchant = "  " }],
            "too-many" => Enumerable.Range(1, 501)
                .Select(sequence => SyncTestHost.Transaction(SyncTestHost.FirstCardId, sequence))
                .ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };

        host.Issuer.Responses[SyncTestHost.FirstCardId] = invalid;
        host.Issuer.Responses[SyncTestHost.SecondCardId] =
            [SyncTestHost.Transaction(SyncTestHost.SecondCardId, 15)];
        host.Issuer.Responses[SyncTestHost.ThirdCardId] =
            [SyncTestHost.Transaction(SyncTestHost.ThirdCardId, 28)];

        await host.Service.SyncOnceAsync(CancellationToken.None);

        Assert.IsFalse(host.Store.Records.Any(record => record.CardId == SyncTestHost.FirstCardId));
        Assert.AreEqual(2, host.Store.Records.Count);

        var rows = await host.GetSyncRowsAsync();
        Assert.AreEqual(3, rows.Length);
        Assert.AreEqual(
            IssuerSyncStatus.Failed,
            rows.Single(row => row.CardId == SyncTestHost.FirstCardId).LastStatus);
        Assert.AreEqual(
            2,
            rows.Count(row => row.LastStatus == IssuerSyncStatus.Ok));
    }

    [TestMethod]
    public async Task SyncOnce_IsIdempotent_ForRepeatedPulls()
    {
        await using var host = SyncTestHost.Create();
        host.Issuer.Responses[SyncTestHost.FirstCardId] =
            [SyncTestHost.Transaction(SyncTestHost.FirstCardId, 1)];
        host.Issuer.Responses[SyncTestHost.SecondCardId] =
            [SyncTestHost.Transaction(SyncTestHost.SecondCardId, 15)];
        host.Issuer.Responses[SyncTestHost.ThirdCardId] =
            [SyncTestHost.Transaction(SyncTestHost.ThirdCardId, 28)];

        await host.Service.SyncOnceAsync(CancellationToken.None);
        await host.Service.SyncOnceAsync(CancellationToken.None);

        Assert.AreEqual(3, host.Store.Records.Count);
        Assert.AreEqual(3, host.Store.Records.Select(record => record.RowKey).Distinct().Count());

        var rows = await host.GetSyncRowsAsync();
        Assert.AreEqual(3, rows.Length);
        Assert.IsTrue(rows.All(row => row.LastStatus == IssuerSyncStatus.Ok));
    }

    [TestMethod]
    public async Task SyncOnce_UnreachableIssuer_ReturnsWithoutThrowing()
    {
        await using var host = SyncTestHost.Create();
        host.Issuer.Failure = new HttpRequestException("Connection refused (issuer-mock:8084).");

        await host.Service.SyncOnceAsync(CancellationToken.None);

        var rows = await host.GetSyncRowsAsync();
        Assert.AreEqual(3, rows.Length);
        Assert.IsTrue(rows.All(row => row.LastStatus == IssuerSyncStatus.Failed));
        Assert.IsTrue(rows.All(row => row.LastError == "The issuer could not be reached."));
    }
}
