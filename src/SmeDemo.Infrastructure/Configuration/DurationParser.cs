using System.Globalization;

namespace SmeDemo.Infrastructure.Configuration;

/// <summary>
/// Parses the short durations used by configuration variables, for example
/// <c>5m</c>, <c>1h</c> or <c>45s</c>. A bare number is read as seconds so a
/// value like <c>300</c> cannot silently become 300 days. This mirrors the
/// auth service's parser without sharing code with an independent service.
/// </summary>
public static class DurationParser
{
    public static TimeSpan Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var text = value.Trim();
        var unit = text[^1];
        var amountText = char.IsAsciiDigit(unit) ? text : text[..^1];

        if (!long.TryParse(amountText, NumberStyles.None, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
        {
            throw new FormatException($"'{value}' is not a positive duration.");
        }

        return char.IsAsciiDigit(unit)
            ? TimeSpan.FromSeconds(amount)
            : unit switch
            {
                's' or 'S' => TimeSpan.FromSeconds(amount),
                'm' or 'M' => TimeSpan.FromMinutes(amount),
                'h' or 'H' => TimeSpan.FromHours(amount),
                'd' or 'D' => TimeSpan.FromDays(amount),
                _ => throw new FormatException($"'{value}' has an unsupported duration unit."),
            };
    }
}
