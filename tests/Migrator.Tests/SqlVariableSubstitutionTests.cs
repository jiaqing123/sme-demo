namespace SmeDemo.Migrator.Tests;

[TestClass]
public sealed class SqlVariableSubstitutionTests
{
    [TestMethod]
    public void ReplacesAllTokens()
    {
        const string script =
            "LOGIN [sme_api_app] WITH PASSWORD = N'${SME_API_DB_PASSWORD}';\n" +
            "LOGIN [sme_auth_app] WITH PASSWORD = N'${SME_AUTH_DB_PASSWORD}';";
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SME_API_DB_PASSWORD"] = "api-password",
            ["SME_AUTH_DB_PASSWORD"] = "auth-password",
        };

        var result = SqlVariableSubstitution.Substitute(script, name => Resolve(values, name));

        Assert.AreEqual(
            "LOGIN [sme_api_app] WITH PASSWORD = N'api-password';\n" +
            "LOGIN [sme_auth_app] WITH PASSWORD = N'auth-password';",
            result);
    }

    [TestMethod]
    public void ReplacesRepeatedTokenEveryTime()
    {
        const string script = "SELECT N'${PASSWORD}'; SELECT N'${PASSWORD}';";

        var result = SqlVariableSubstitution.Substitute(script, _ => "s3cret");

        Assert.AreEqual("SELECT N's3cret'; SELECT N's3cret';", result);
    }

    [TestMethod]
    public void UnknownVariableThrowsAndNamesIt()
    {
        const string script = "SELECT N'${MISSING_SECRET}';";

        var exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => SqlVariableSubstitution.Substitute(script, _ => null));

        StringAssert.Contains(exception.Message, "MISSING_SECRET");
    }

    [TestMethod]
    public void EmptyVariableValueThrowsAndNamesIt()
    {
        const string script = "SELECT N'${EMPTY_SECRET}';";

        var exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => SqlVariableSubstitution.Substitute(script, _ => string.Empty));

        StringAssert.Contains(exception.Message, "EMPTY_SECRET");
    }

    [TestMethod]
    public void DollarWithoutBracesIsUntouched()
    {
        const string script = "SELECT '$HOME', '$1', '${not a name}', '$X';";

        var result = SqlVariableSubstitution.Substitute(script, _ => "unused");

        Assert.AreEqual(script, result);
    }

    [TestMethod]
    public void EmptyInputReturnsEmptyString()
    {
        var result = SqlVariableSubstitution.Substitute(string.Empty, _ => "unused");

        Assert.AreEqual(string.Empty, result);
    }

    [TestMethod]
    public void DollarInSubstitutedValueIsNotReplaced()
    {
        const string script = "SELECT N'${PASSWORD}';";

        var result = SqlVariableSubstitution.Substitute(script, _ => "${OTHER}$1");

        Assert.AreEqual("SELECT N'${OTHER}$1';", result);
    }

    [TestMethod]
    public void SubstituteFromEnvironmentReadsProcessEnvironment()
    {
        var name = $"SME_TEST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(name, "from-environment");

        try
        {
            var result = SqlVariableSubstitution.SubstituteFromEnvironment($"SELECT N'${{{name}}}';");

            Assert.AreEqual("SELECT N'from-environment';", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    private static string? Resolve(IReadOnlyDictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) ? value : null;
}
