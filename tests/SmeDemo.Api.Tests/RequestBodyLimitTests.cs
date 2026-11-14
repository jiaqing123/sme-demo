using SmeDemo.Api.Configuration;

namespace SmeDemo.Api.Tests;

[TestClass]
public sealed class RequestBodyLimitTests
{
    [TestMethod]
    public void Resolve_DefaultsToOneMiB()
    {
        Assert.AreEqual(1024 * 1024, RequestBodyLimit.Resolve(null));
        Assert.AreEqual(1024 * 1024, RequestBodyLimit.Resolve(string.Empty));
        Assert.AreEqual(1024 * 1024, RequestBodyLimit.Resolve("   "));
    }

    [TestMethod]
    public void Resolve_AcceptsAPositiveByteCount()
    {
        Assert.AreEqual(5_242_880, RequestBodyLimit.Resolve("5242880"));
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("-1")]
    [DataRow("abc")]
    [DataRow("1.5")]
    [DataRow("99999999999999999999")]
    public void Resolve_RejectsInvalidValues(string configured)
    {
        var exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => RequestBodyLimit.Resolve(configured));
        StringAssert.Contains(exception.Message, "MAX_REQUEST_BODY_BYTES");
    }
}
