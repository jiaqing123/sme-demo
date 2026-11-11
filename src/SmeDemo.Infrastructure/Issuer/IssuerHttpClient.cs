using System.Globalization;
using System.Text.Json;
using SmeDemo.Application.Abstractions;

namespace SmeDemo.Infrastructure.Issuer;

/// <summary>
/// Adapter for the card issuer HTTP API. The issuer is untrusted input: this
/// client only reads the documented shape and keeps each transaction's raw JSON
/// for storage; semantic validation happens in <c>IssuerSyncService</c>.
/// </summary>
public sealed class IssuerHttpClient : IIssuerClient
{
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private const int MaxTransactionsPerCard = 500;
    private const int MaxPayloadBytes = 16 * 1024;
    private const int MaxMerchantLength = 256;

    private readonly HttpClient _http;

    public IssuerHttpClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<IReadOnlyList<IssuerTransaction>> GetTransactionsAsync(
        Guid cardId,
        CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(
            $"issuer/v1/cards/{cardId:D}/transactions",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // The body may echo request details; keep it out of the exception and
            // let the sync service record a fixed, safe message.
            throw new IssuerRequestException(
                $"The issuer returned HTTP {(int)response.StatusCode}.");
        }

        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
        {
            throw new InvalidIssuerPayloadException("The issuer response is too large.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("card_id", out var cardIdElement) ||
            !cardIdElement.TryGetGuid(out var responseCardId))
        {
            throw new InvalidIssuerPayloadException("The issuer response has no valid card_id.");
        }

        if (!root.TryGetProperty("transactions", out var transactionsElement) ||
            transactionsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidIssuerPayloadException("The issuer response has no transactions array.");
        }

        // The response must describe the card that was asked for, even when the
        // list is empty; otherwise a misrouted response could be recorded against
        // the wrong card.
        if (responseCardId != cardId)
        {
            throw new InvalidIssuerPayloadException(
                "The issuer returned a response for a different card.");
        }

        var transactions = new List<IssuerTransaction>();
        foreach (var element in transactionsElement.EnumerateArray())
        {
            // Count before materialising: a huge array must be rejected while it
            // is still being enumerated.
            if (transactions.Count == MaxTransactionsPerCard)
            {
                throw new InvalidIssuerPayloadException(
                    $"The issuer returned more than {MaxTransactionsPerCard} transactions for one card.");
            }

            transactions.Add(ToTransaction(cardId, element));
        }

        return transactions;
    }

    private static IssuerTransaction ToTransaction(Guid cardId, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidIssuerPayloadException("A transaction is not a JSON object.");
        }

        var rawPayload = element.GetRawText();
        if (rawPayload.Length > MaxPayloadBytes)
        {
            throw new InvalidIssuerPayloadException("A transaction payload is too large.");
        }

        return new IssuerTransaction(
            cardId,
            ReadGuid(element, "transaction_id"),
            ReadInt64(element, "amount_minor"),
            ReadString(element, "currency"),
            ReadString(element, "direction"),
            ReadString(element, "merchant"),
            ReadString(element, "status"),
            ReadDateTimeOffset(element, "occurred_at"),
            ReadDateTimeOffset(element, "posted_at"),
            rawPayload);
    }

    private static Guid ReadGuid(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || !value.TryGetGuid(out var parsed))
        {
            throw new InvalidIssuerPayloadException($"The issuer response has no valid {name}.");
        }

        return parsed;
    }

    private static long ReadInt64(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || !value.TryGetInt64(out var parsed))
        {
            throw new InvalidIssuerPayloadException($"The issuer response has no valid {name}.");
        }

        return parsed;
    }

    private static string ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidIssuerPayloadException($"The issuer response has no valid {name}.");
        }

        var text = value.GetString() ?? string.Empty;
        if (name == "merchant" && text.Length > MaxMerchantLength)
        {
            throw new InvalidIssuerPayloadException("A transaction merchant is too long.");
        }

        return text;
    }

    private static DateTimeOffset ReadDateTimeOffset(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            throw new InvalidIssuerPayloadException($"The issuer response has no valid {name}.");
        }

        // Normalise to UTC: an offset-less timestamp must not silently depend on
        // the API host's time zone, and RowKey is derived from the UTC ticks.
        return parsed.ToUniversalTime();
    }
}

/// <summary>
/// A non-success HTTP response from the issuer. Distinct from payload errors so
/// the sync service can record a stable, non-leaking message.
/// </summary>
public sealed class IssuerRequestException : Exception
{
    public IssuerRequestException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The issuer returned data that does not match the documented transaction shape.
/// </summary>
public sealed class InvalidIssuerPayloadException : Exception
{
    public InvalidIssuerPayloadException(string message)
        : base(message)
    {
    }
}
