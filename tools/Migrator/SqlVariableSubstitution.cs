using System.Text.RegularExpressions;

namespace SmeDemo.Migrator;

/// <summary>
/// Expands <c>${NAME}</c> tokens in a SQL script from a caller-supplied
/// resolver. Substitution is a single pass: text introduced by one token is
/// never scanned again, so a value that happens to contain <c>${...}</c> is
/// copied literally. Tokens are deliberately different from sqlcmd's
/// <c>$(NAME)</c> syntax because the Azure SQL bootstrap runs through the
/// migrator, not sqlcmd.
/// </summary>
public static partial class SqlVariableSubstitution
{
    /// <summary>
    /// Replaces every <c>${NAME}</c> token with the value returned by
    /// <paramref name="resolveVariable"/>. A missing or empty value throws an
    /// <see cref="InvalidOperationException"/> naming the variable; the value
    /// itself is never included in the message.
    /// </summary>
    /// <param name="script">The script to expand.</param>
    /// <param name="resolveVariable">Returns the value for a token name, or <see langword="null"/> when unset.</param>
    /// <returns>The expanded script.</returns>
    public static string Substitute(string script, Func<string, string?> resolveVariable)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(resolveVariable);

        return TokenPattern().Replace(
            script,
            match =>
            {
                var name = match.Groups[1].Value;
                var value = resolveVariable(name);
                if (string.IsNullOrEmpty(value))
                {
                    throw new InvalidOperationException(
                        $"bootstrap SQL references ${{{name}}} but environment variable {name} is not set.");
                }

                return value;
            });
    }

    /// <summary>
    /// Expands <c>${NAME}</c> tokens using the process environment.
    /// </summary>
    /// <param name="script">The script to expand.</param>
    /// <returns>The expanded script.</returns>
    public static string SubstituteFromEnvironment(string script) =>
        Substitute(script, Environment.GetEnvironmentVariable);

    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}
