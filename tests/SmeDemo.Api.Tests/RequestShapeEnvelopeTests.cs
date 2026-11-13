using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using SmeDemo.Api.Errors;

namespace SmeDemo.Api.Tests;

/// <summary>
/// Request-shape failures that the framework raises as
/// <see cref="BadHttpRequestException"/> must keep their own status code and the
/// shared envelope. Flattening 413/415 to 400 would hide why the body was
/// rejected from every generated client.
/// </summary>
[TestClass]
public sealed class RequestShapeEnvelopeTests
{
    [TestMethod]
    [DataRow(StatusCodes.Status413PayloadTooLarge, "PAYLOAD_TOO_LARGE")]
    [DataRow(StatusCodes.Status415UnsupportedMediaType, "UNSUPPORTED_MEDIA_TYPE")]
    [DataRow(StatusCodes.Status400BadRequest, "VALIDATION_ERROR")]
    public async Task BadHttpRequest_KeepsItsStatusAndCarriesTheEnvelope(int statusCode, string errorCode)
    {
        var handler = new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/me";
        context.TraceIdentifier = "request-shape-trace";
        context.Response.Body = new MemoryStream();

        var handled = await handler.TryHandleAsync(
            context,
            new BadHttpRequestException("request rejected by the framework", statusCode),
            CancellationToken.None);

        Assert.IsTrue(handled, "The exception handler must claim the request.");
        Assert.AreEqual(statusCode, context.Response.StatusCode);
        Assert.AreEqual(
            "application/problem+json",
            context.Response.ContentType,
            "Every request-shape failure must use the shared content type.");

        context.Response.Body.Position = 0;
        using var document = JsonDocument.Parse(context.Response.Body);
        var root = document.RootElement;

        Assert.AreEqual(statusCode, root.GetProperty("status").GetInt32());
        Assert.AreEqual(errorCode, root.GetProperty("title").GetString());
        Assert.AreEqual("/api/v1/me", root.GetProperty("instance").GetString());
        Assert.AreEqual("request-shape-trace", root.GetProperty("traceId").GetString());
        Assert.AreEqual(errorCode, root.GetProperty("error").GetProperty("code").GetString());
        Assert.IsFalse(
            string.IsNullOrWhiteSpace(root.GetProperty("error").GetProperty("message").GetString()));
    }
}
