using System.Text;

namespace SmeDemo.Migrator;

/// <summary>
/// Splits a SQL Server script into batches the way SSMS and sqlcmd do: a line
/// containing only <c>GO</c> terminates a batch. Generated EF Core scripts use
/// <c>GO</c> as their only batch separator, so a line-based split is sufficient.
/// </summary>
public static class SqlScript
{
    public static IReadOnlyList<string> SplitBatches(string script)
    {
        ArgumentNullException.ThrowIfNull(script);

        var batches = new List<string>();
        var current = new StringBuilder();

        using var reader = new StringReader(script);
        while (reader.ReadLine() is { } line)
        {
            if (line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                Flush();
                continue;
            }

            current.AppendLine(line);
        }

        Flush();
        return batches;

        void Flush()
        {
            var batch = current.ToString().Trim();
            if (batch.Length > 0)
            {
                batches.Add(batch);
            }

            current.Clear();
        }
    }
}
