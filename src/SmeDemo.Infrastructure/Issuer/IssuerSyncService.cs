using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmeDemo.Application.Abstractions;
using SmeDemo.Application.Features.Transactions;
using SmeDemo.Domain.Entities;
using SmeDemo.Infrastructure.Persistence;

namespace SmeDemo.Infrastructure.Issuer;

/// <summary>
/// Pulls each card's transactions from the issuer and upserts them into the
/// local feed. A failed card is recorded in <c>app.issuer_sync</c> and does not
/// stop the host or the read path: the API keeps serving whatever is already in
/// Table Storage (requirements §8.7, acceptance 11).
/// </summary>
public sealed class IssuerSyncService : BackgroundService
{
    private const int MaxTransactionsPerCard = 500;
    private const int StartupAttempts = 5;
    private static readonly TimeSpan StartupRetryDelay = TimeSpan.FromSeconds(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly InfrastructureOptions _options;
    private readonly IIssuerClient _issuer;
    private readonly ILogger<IssuerSyncService> _logger;

    public IssuerSyncService(
        IServiceScopeFactory scopeFactory,
        InfrastructureOptions options,
        IIssuerClient issuer,
        ILogger<IssuerSyncService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _issuer = issuer;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunStartupSyncAsync(stoppingToken);

            using var timer = new PeriodicTimer(_options.IssuerSyncInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await SyncOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
        catch (Exception exception)
        {
            // A background integration failure must never take the API down.
            _logger.LogError(exception, "Issuer sync loop stopped unexpectedly.");
        }
    }

    /// <summary>
    /// Runs one full sync round. Never throws for issuer failures; it records
    /// them per card. This is the manual/test entry point.
    /// </summary>
    public async Task SyncOnceAsync(CancellationToken cancellationToken)
    {
        await SyncRoundAsync(cancellationToken);
    }

    private async Task RunStartupSyncAsync(CancellationToken cancellationToken)
    {
        // The issuer container may still be starting even though Compose reports
        // it healthy; retry a bounded number of times, then fall back to the
        // regular interval so startup is not held hostage.
        for (var attempt = 1; attempt <= StartupAttempts; attempt++)
        {
            var allSucceeded = await SyncRoundAsync(cancellationToken);
            if (allSucceeded)
            {
                return;
            }

            if (attempt == StartupAttempts)
            {
                _logger.LogWarning(
                    "Initial issuer sync did not succeed after {Attempts} attempts; continuing on the configured interval.",
                    StartupAttempts);
                return;
            }

            _logger.LogInformation(
                "Initial issuer sync attempt {Attempt}/{Attempts} did not fully succeed; retrying in {Delay}.",
                attempt,
                StartupAttempts,
                StartupRetryDelay);

            await Task.Delay(StartupRetryDelay, cancellationToken);
        }
    }

    /// <summary>
    /// Returns true when every card succeeded. Per-card failures are recorded and
    /// do not throw; an unexpected failure before the card loop is logged and
    /// also treated as a failed round.
    /// </summary>
    private async Task<bool> SyncRoundAsync(CancellationToken cancellationToken)
    {
        try
        {
            Guid[] cardIds;
            await using (var scope = _scopeFactory.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                cardIds = await db.Cards
                    .AsNoTracking()
                    .OrderBy(card => card.DisplayOrder)
                    .ThenBy(card => card.Id)
                    .Select(card => card.Id)
                    .ToArrayAsync(cancellationToken);
            }

            var allSucceeded = true;
            foreach (var cardId in cardIds)
            {
                if (await SyncCardAsync(cardId, cancellationToken) is false)
                {
                    allSucceeded = false;
                }
            }

            return allSucceeded;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Issuer sync round failed before processing cards.");
            return false;
        }
    }

    private async Task<bool> SyncCardAsync(Guid cardId, CancellationToken cancellationToken)
    {
        try
        {
            var transactions = await _issuer.GetTransactionsAsync(cardId, cancellationToken);
            var records = ValidateAndMap(cardId, transactions);

            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var store = scope.ServiceProvider.GetRequiredService<ITransactionStore>();

            // Validate the whole card before writing anything: one bad record
            // must not leave half a card in the feed.
            await store.UpsertAsync(records, cancellationToken);

            var syncedAt = DateTime.UtcNow;
            var sync = await db.IssuerSyncs
                .SingleOrDefaultAsync(row => row.CardId == cardId, cancellationToken);

            if (sync is null)
            {
                db.IssuerSyncs.Add(IssuerSync.Succeeded(cardId, syncedAt));
            }
            else
            {
                sync.MarkSucceeded(syncedAt);
            }

            await db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Issuer sync succeeded for card {CardId}: {Count} transactions.",
                cardId,
                records.Count);

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var error = DescribeFailure(exception);
            _logger.LogWarning(
                exception,
                "Issuer sync failed for card {CardId}: {Error}",
                cardId,
                error);

            await RecordFailureAsync(cardId, error, cancellationToken);
            return false;
        }
    }

    private async Task RecordFailureAsync(
        Guid cardId,
        string error,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var syncedAt = DateTime.UtcNow;
            var sync = await db.IssuerSyncs
                .SingleOrDefaultAsync(row => row.CardId == cardId, cancellationToken);

            if (sync is null)
            {
                db.IssuerSyncs.Add(IssuerSync.Failed(cardId, syncedAt, error));
            }
            else
            {
                sync.MarkFailed(syncedAt, error);
            }

            await db.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down; nothing else to do.
        }
        catch (Exception exception)
        {
            // The API must stay up even if the sync-state write also fails.
            _logger.LogWarning(exception, "Could not record issuer sync failure for card {CardId}.", cardId);
        }
    }

    private static IReadOnlyList<TransactionRecord> ValidateAndMap(
        Guid cardId,
        IReadOnlyList<IssuerTransaction>? transactions)
    {
        if (transactions is null)
        {
            throw new InvalidIssuerPayloadException("The issuer returned no transaction list.");
        }

        if (transactions.Count > MaxTransactionsPerCard)
        {
            throw new InvalidIssuerPayloadException(
                $"The issuer returned more than {MaxTransactionsPerCard} transactions for one card.");
        }

        var records = new List<TransactionRecord>(transactions.Count);
        foreach (var transaction in transactions)
        {
            if (transaction.CardId != cardId)
            {
                throw new InvalidIssuerPayloadException(
                    "The issuer returned a transaction for a different card.");
            }

            if (transaction.TransactionId == Guid.Empty)
            {
                throw new InvalidIssuerPayloadException("A transaction_id is missing or invalid.");
            }

            if (transaction.Currency is not { Length: 3 })
            {
                throw new InvalidIssuerPayloadException("A currency is not a three-character code.");
            }

            if (transaction.Direction is not ("debit" or "credit"))
            {
                throw new InvalidIssuerPayloadException("A direction is not debit or credit.");
            }

            if (transaction.Status is not ("posted" or "pending"))
            {
                throw new InvalidIssuerPayloadException("A status is not posted or pending.");
            }

            if (transaction.AmountMinor <= 0)
            {
                throw new InvalidIssuerPayloadException("A transaction amount is not positive.");
            }

            if (string.IsNullOrWhiteSpace(transaction.Merchant))
            {
                throw new InvalidIssuerPayloadException("A transaction has no merchant.");
            }

            if (transaction.OccurredAt == default || transaction.PostedAt == default)
            {
                throw new InvalidIssuerPayloadException("A transaction timestamp is missing.");
            }

            records.Add(new TransactionRecord(
                TransactionKeys.CreateRowKey(transaction.OccurredAt, transaction.TransactionId),
                transaction.CardId,
                transaction.TransactionId,
                transaction.AmountMinor,
                transaction.Currency,
                transaction.Direction,
                transaction.Merchant,
                transaction.Status,
                transaction.OccurredAt,
                transaction.PostedAt,
                transaction.RawPayload));
        }

        return records;
    }

    private string DescribeFailure(Exception exception)
    {
        var message = exception switch
        {
            IssuerRequestException => exception.Message,
            InvalidIssuerPayloadException => exception.Message,
            HttpRequestException => "The issuer could not be reached.",
            TaskCanceledException => "The issuer request timed out.",
            _ => $"{exception.GetType().Name}: {exception.Message}",
        };

        // Defence in depth: no configured secret may end up in the sync row.
        if (!string.IsNullOrEmpty(_options.IssuerApiKey))
        {
            message = message.Replace(_options.IssuerApiKey, "[redacted]", StringComparison.Ordinal);
        }

        return message;
    }
}
