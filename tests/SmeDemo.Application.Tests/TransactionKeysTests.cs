using SmeDemo.Application.Features.Transactions;

namespace SmeDemo.Application.Tests;

[TestClass]
public sealed class TransactionKeysTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void CreateRowKey_IsSortableNewestFirst()
    {
        var older = TransactionKeys.CreateRowKey(Now.AddHours(-1), Guid.NewGuid());
        var newer = TransactionKeys.CreateRowKey(Now, Guid.NewGuid());

        Assert.AreEqual(TransactionKeys.RowKeyLength, newer.Length);
        Assert.IsTrue(string.CompareOrdinal(newer, older) < 0);
    }

    [TestMethod]
    public void CreateRowKey_RoundTripsThroughValidationAndCursor()
    {
        var rowKey = TransactionKeys.CreateRowKey(
            Now,
            Guid.Parse("00000000-0000-4000-8000-000000000001"));

        Assert.IsTrue(TransactionKeys.IsValidRowKey(rowKey));

        var cursor = CursorCodec.Encode(rowKey);
        Assert.IsTrue(CursorCodec.TryDecode(cursor, out var decoded));
        Assert.AreEqual(rowKey, decoded);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("short")]
    [DataRow("00000000000000000000_00000000-0000-4000-8000-00000000000")]
    [DataRow("00000000000000000000_00000000-0000-4000-8000-000000000001")]
    public void IsValidRowKey_RejectsMalformedValues(string value)
    {
        Assert.IsFalse(TransactionKeys.IsValidRowKey(value));
    }

    [TestMethod]
    public void CursorCodec_RejectsGarbage()
    {
        Assert.IsFalse(CursorCodec.TryDecode("not base64!", out _));
        Assert.IsFalse(CursorCodec.TryDecode(null, out _));
    }
}
