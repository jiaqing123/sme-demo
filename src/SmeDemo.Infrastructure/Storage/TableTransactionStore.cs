using Azure;
using Azure.Data.Tables;
using SmeDemo.Application.Abstractions;
using SmeDemo.Application.Errors;

namespace SmeDemo.Infrastructure.Storage;

/// <summary>
/// Table Storage implementation of the feed. The partition is the card, the row
/// key is the reversed timestamp plus the transaction id, so an ascending range
/// scan is a newest-first page (requirements §8.5).
/// </summary>
public sealed class TableTransactionStore : ITransactionStore
{
    private const int MaxBatchOperations = 100;

    private readonly TableClient _table;
    private readonly InfrastructureOptions _options;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private bool _initialized;

    public TableTransactionStore(TableClient table, InfrastructureOptions options)
    {
        _table = table;
        _options = options;
    }

    public async Task<TransactionPage> ReadAsync(
        Guid cardId,
        string? afterRowKey,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await EnsureTableAsync(cancellationToken);

        var partition = cardId.ToString("D");
        var filter = afterRowKey is null
            ? TableClient.CreateQueryFilter($"PartitionKey eq {partition}")
            : TableClient.CreateQueryFilter($"PartitionKey eq {partition} and RowKey gt {afterRowKey}");

        var items = new List<TransactionRecord>(limit);
        string? nextRowKey = null;

        try
        {
            await foreach (var entity in _table.QueryAsync<TableEntity>(
                filter: filter,
                maxPerPage: limit + 1,
                cancellationToken: cancellationToken))
            {
                if (items.Count == limit)
                {
                    nextRowKey = items[^1].RowKey;
                    break;
                }

                items.Add(ToRecord(entity));
            }
        }
        catch (RequestFailedException exception)
        {
            throw new StoreUnavailableException("The transaction store is unavailable.", exception);
        }

        return new TransactionPage(items, nextRowKey);
    }

    public async Task UpsertAsync(
        IReadOnlyCollection<TransactionRecord> records,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            return;
        }

        await EnsureTableAsync(cancellationToken);

        // A Table Storage transaction is scoped to one partition, so group by
        // card and submit batches within the documented 100-operation limit.
        foreach (var partition in records.GroupBy(record => record.CardId))
        {
            var actions = partition
                .Select(record => new TableTransactionAction(
                    TableTransactionActionType.UpsertReplace,
                    ToEntity(record)))
                .Chunk(MaxBatchOperations);

            foreach (var batch in actions)
            {
                try
                {
                    await _table.SubmitTransactionAsync(batch, cancellationToken);
                }
                catch (RequestFailedException exception)
                {
                    throw new StoreUnavailableException("The transaction store is unavailable.", exception);
                }
            }
        }
    }

    private async Task EnsureTableAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (!_initialized)
            {
                await _table.CreateIfNotExistsAsync(cancellationToken);
                _initialized = true;
            }
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private TableEntity ToEntity(TransactionRecord record) => new(record.CardId.ToString("D"), record.RowKey)
    {
        ["OrgId"] = _options.OrgId,
        ["TransactionId"] = record.TransactionId,
        ["AmountMinor"] = record.AmountMinor,
        ["Currency"] = record.Currency,
        ["Direction"] = record.Direction,
        ["Merchant"] = record.Merchant,
        ["Status"] = record.Status,
        ["OccurredAt"] = record.OccurredAt,
        ["PostedAt"] = record.PostedAt,
        ["IssuerPayload"] = record.IssuerPayload,
        ["IngestedAt"] = DateTimeOffset.UtcNow,
    };

    private static TransactionRecord ToRecord(TableEntity entity) => new(
        entity.RowKey,
        Guid.Parse(entity.PartitionKey),
        entity.GetGuid("TransactionId") ?? Guid.Empty,
        entity.GetInt64("AmountMinor") ?? 0,
        entity.GetString("Currency") ?? string.Empty,
        entity.GetString("Direction") ?? string.Empty,
        entity.GetString("Merchant") ?? string.Empty,
        entity.GetString("Status") ?? string.Empty,
        entity.GetDateTimeOffset("OccurredAt") ?? DateTimeOffset.UnixEpoch,
        entity.GetDateTimeOffset("PostedAt")
            ?? entity.GetDateTimeOffset("OccurredAt")
            ?? DateTimeOffset.UnixEpoch,
        entity.GetString("IssuerPayload") ?? "{}");
}
