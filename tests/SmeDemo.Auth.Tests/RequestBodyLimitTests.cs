using SmeDemo.Auth.Configuration;

namespace SmeDemo.Auth.Tests;

[TestClass]
public sealed class RequestBodyLimitTests
{
    [TestMethod]
    public void Resolve_DefaultsToOneMiB()
    {
        Assert.AreEqual(1024 * 1024, RequestBodyLimit.Resolve(null));
        Assert.AreEqual(1024 * 1024, RequestBodyLimit.Resolve("   "));
    }

    [TestMethod]
    public void Resolve_AcceptsAPositiveByteCount()
    {
        Assert.AreEqual(4096, RequestBodyLimit.Resolve("4096"));
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("-5")]
    [DataRow("not-a-number")]
    public void Resolve_RejectsInvalidValues(string configured)
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => RequestBodyLimit.Resolve(configured));
    }
}
