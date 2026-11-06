using Microsoft.VisualStudio.TestTools.UnitTesting;
using SmeDemo.Auth.Configuration;

namespace SmeDemo.Auth.Tests;

[TestClass]
public sealed class DurationParserTests
{
    [TestMethod]
    [DataRow("30m", 1800)]
    [DataRow("1h", 3600)]
    [DataRow("45s", 45)]
    [DataRow("1800", 1800)]
    public void ParsesSupportedDurations(string value, int expectedSeconds)
    {
        Assert.AreEqual(TimeSpan.FromSeconds(expectedSeconds), DurationParser.Parse(value));
    }

    [TestMethod]
    public void RejectsUnsupportedOrNonPositiveValues()
    {
        Assert.ThrowsExactly<FormatException>(() => DurationParser.Parse("0m"));
        Assert.ThrowsExactly<FormatException>(() => DurationParser.Parse("soon"));
    }
}
