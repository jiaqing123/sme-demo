using Microsoft.VisualStudio.TestTools.UnitTesting;
using SmeDemo.Migrator;

namespace SmeDemo.Migrator.Tests;

[TestClass]
public sealed class SqlScriptTests
{
    [TestMethod]
    public void SplitsOnGoLines()
    {
        const string script = """
            CREATE TABLE a (id int);
            GO
            CREATE TABLE b (id int);
            GO
            """;

        var batches = SqlScript.SplitBatches(script);

        Assert.AreEqual(2, batches.Count);
        StringAssert.Contains(batches[0], "CREATE TABLE a");
        StringAssert.Contains(batches[1], "CREATE TABLE b");
    }

    [TestMethod]
    public void AcceptsLowerCaseAndPaddedGo()
    {
        var batches = SqlScript.SplitBatches("SELECT 1;\n  go  \nSELECT 2;");

        Assert.AreEqual(2, batches.Count);
    }

    [TestMethod]
    public void ReturnsSingleBatchWhenNoGoPresent()
    {
        var batches = SqlScript.SplitBatches("SELECT 1;\nSELECT 2;");

        Assert.AreEqual(1, batches.Count);
    }

    [TestMethod]
    public void DropsEmptySegments()
    {
        var batches = SqlScript.SplitBatches("GO\n\nGO\nSELECT 1;\nGO\nGO");

        Assert.AreEqual(1, batches.Count);
        StringAssert.Contains(batches[0], "SELECT 1");
    }

    [TestMethod]
    public void DoesNotSplitOnGoInsideText()
    {
        const string script = "INSERT INTO log VALUES ('GO');\nSELECT 1;";

        var batches = SqlScript.SplitBatches(script);

        Assert.AreEqual(1, batches.Count);
    }

    [TestMethod]
    public void TrimsWhitespaceAroundBatches()
    {
        var batches = SqlScript.SplitBatches("\n\nSELECT 1;\n\nGO\n\n");

        Assert.AreEqual(1, batches.Count);
        Assert.AreEqual("SELECT 1;", batches[0]);
    }
}
