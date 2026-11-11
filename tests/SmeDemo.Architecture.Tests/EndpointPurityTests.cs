using System.Reflection.PortableExecutable;

namespace SmeDemo.Architecture.Tests;

/// <summary>
/// The endpoint layer may only orchestrate HTTP concerns. It must not reach for
/// EF Core, the Azure SDK or <see cref="System.Net.Http.HttpClient"/>, because
/// those are adapter details owned by the infrastructure layer.
/// </summary>
[TestClass]
public sealed class EndpointPurityTests
{
    [TestMethod]
    public void EndpointTypes_DoNotReferenceEntityFrameworkAzureOrHttpClient()
    {
        // CurrentUser is a public type in SmeDemo.Api; using it avoids the
        // global Program type, which both the API and Auth top-level programs
        // contribute to this compilation.
        var apiAssemblyPath = typeof(SmeDemo.Api.Auth.CurrentUser).Assembly.Location;
        using var stream = File.OpenRead(apiAssemblyPath);
        using var peReader = new PEReader(stream);
        var findings = new EndpointIlReferenceScanner(peReader).FindForbiddenReferences();

        Assert.AreEqual(
            0,
            findings.Count,
            "Types in SmeDemo.Api.Endpoints must not reference Microsoft.EntityFrameworkCore*, " +
            "Azure.* or System.Net.Http (including in method bodies, signatures, fields, " +
            "properties, base types and interfaces). Offending references: " +
            string.Join(" | ", findings.OrderBy(finding => finding, StringComparer.Ordinal)));
    }
}
