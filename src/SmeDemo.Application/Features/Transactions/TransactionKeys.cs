using System.Globalization;
using System.Text;

namespace SmeDemo.Application.Features.Transactions;

/// <summary>
/// The Table Storage row key design from requirements §8.5: a zero-padded
/// reversed tick count makes ascending row-key order equal newest-first order.
/// </summary>
public static class TransactionKeys
{
    public const int RowKeyLength = 19 + 1 + 36;

    public static string CreateRowKey(DateTimeOffset occurredAt, Guid transactionId)
    {
        var reverseTicks = DateTime.MaxValue.Ticks - occurredAt.UtcTicks;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{reverseTicks:D19}_{transactionId:D}");
    }

    public static bool IsValidRowKey(string? rowKey)
    {
        if (rowKey is not { Length: RowKeyLength } || rowKey[19] != '_')
        {
            return false;
        }

        for (var index = 0; index < 19; index++)
        {
            if (!char.IsAsciiDigit(rowKey[index]))
            {
                return false;
            }
        }

        return Guid.TryParse(rowKey[20..], out _);
    }
}

/// <summary>
/// Wraps a row key as a base64url token. The client never sees the storage key;
/// the API treats anything that does not decode to a valid row key as a
/// validation failure instead of a storage error.
/// </summary>
public static class CursorCodec
{
    public static string Encode(string rowKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rowKey);

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(rowKey))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static bool TryDecode(string? cursor, out string rowKey)
    {
        rowKey = string.Empty;
        if (string.IsNullOrEmpty(cursor))
        {
            return false;
        }

        try
        {
            var base64 = cursor.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            if (!TransactionKeys.IsValidRowKey(decoded))
            {
                return false;
            }

            rowKey = decoded;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
