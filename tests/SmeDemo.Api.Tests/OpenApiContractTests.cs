using System.Text.Json;
using System.Text.Json.Nodes;

namespace SmeDemo.Api.Tests;

/// <summary>
/// The OpenAPI document is a committed artefact that clients consume, so the
/// contract tests check both what the host serves and what is checked in.
/// </summary>
[TestClass]
public sealed class OpenApiContractTests
{
    private static readonly string[] ApiRoutes =
    [
        "/api/v1/me",
        "/api/v1/me/cards",
        "/api/v1/me/cards/{cardId}/transactions",
        "/api/v1/cards/{cardId}/art",
    ];

    [TestMethod]
    public async Task Document_DeclaresBearerScheme_AndRequiresItOnApiOperationsOnly()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        var json = await client.GetStringAsync("/openapi/v1.json");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var scheme = root.GetProperty("components")
            .GetProperty("securitySchemes")
            .GetProperty("Bearer");
        Assert.AreEqual("http", scheme.GetProperty("type").GetString());
        Assert.AreEqual("bearer", scheme.GetProperty("scheme").GetString());
        Assert.AreEqual("JWT", scheme.GetProperty("bearerFormat").GetString());

        foreach (var route in ApiRoutes)
        {
            var security = GetOperation(root, route).GetProperty("security");
            Assert.AreEqual(1, security.GetArrayLength(), $"{route} must require exactly one scheme.");
            Assert.IsTrue(
                security[0].TryGetProperty("Bearer", out _),
                $"{route} must require the Bearer scheme.");
        }

        // The liveness probe is anonymous, so the document must not claim a
        // token is needed. An empty security array is dropped by the writer, so
        // absence is the correct representation.
        var health = GetOperation(root, "/healthz");
        Assert.IsFalse(
            health.TryGetProperty("security", out _),
            "/healthz must not carry a security requirement.");
        Assert.IsFalse(
            root.TryGetProperty("security", out _),
            "The document must not apply a global requirement; it would also cover /healthz.");
    }

    [TestMethod]
    public async Task CommittedDocument_MatchesTheDocumentServedAtRuntime()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        var runtimeJson = await client.GetStringAsync("/openapi/v1.json");
        var committedJson = await File.ReadAllTextAsync(
            Path.Combine(FindRepositoryRoot(), "openapi", "v1.json"));

        // The runtime endpoint derives a `servers` entry from the request; the
        // build-time document has no request, so the host is the only expected
        // difference. Everything a client generates code from must match.
        Assert.IsTrue(
            JsonNode.DeepEquals(
                Normalize(runtimeJson),
                Normalize(committedJson)),
            "openapi/v1.json does not match the served document. Rebuild SmeDemo.Api and commit the regenerated file.");
    }

    private static JsonNode Normalize(string json)
    {
        var node = JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidOperationException("The OpenAPI document could not be parsed.");
        node.Remove("servers");
        return node;
    }

    private static JsonElement GetOperation(JsonElement root, string route)
    {
        Assert.IsTrue(
            root.GetProperty("paths").TryGetProperty(route, out var path),
            $"OpenAPI paths must contain {route}.");
        Assert.IsTrue(path.TryGetProperty("get", out var operation), $"{route} must be documented as GET.");
        return operation;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "SmeDemo.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("The repository root (SmeDemo.slnx) was not found.");
    }
}
