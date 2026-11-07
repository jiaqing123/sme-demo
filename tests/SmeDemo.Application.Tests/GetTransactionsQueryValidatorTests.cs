using Microsoft.VisualStudio.TestTools.UnitTesting;
using SmeDemo.Application.Features.Transactions;

namespace SmeDemo.Application.Tests;

[TestClass]
public sealed class GetTransactionsQueryValidatorTests
{
    private readonly GetTransactionsQueryValidator _validator = new();

    [TestMethod]
    [DataRow(0, false)]
    [DataRow(1, true)]
    [DataRow(20, true)]
    [DataRow(100, true)]
    [DataRow(101, false)]
    public void Limit_IsWithinContract(int limit, bool expectedValid)
    {
        var result = _validator.Validate(
            new GetTransactionsQuery(TestData.UserId, Guid.NewGuid(), null, limit));

        Assert.AreEqual(expectedValid, result.IsValid, string.Join(",", result.Errors.Select(error => error.ErrorMessage)));
    }

    [TestMethod]
    public void NullLimitAndCursor_AreValid()
    {
        var result = _validator.Validate(
            new GetTransactionsQuery(TestData.UserId, Guid.NewGuid(), null, null));

        Assert.IsTrue(result.IsValid);
    }

    [TestMethod]
    public void InvalidCursor_IsRejected()
    {
        var result = _validator.Validate(
            new GetTransactionsQuery(TestData.UserId, Guid.NewGuid(), "!!!not-base64!!!", 20));

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual(nameof(GetTransactionsQuery.Cursor), result.Errors.Single().PropertyName);
    }

    [TestMethod]
    public void EncodedRowKeyCursor_IsAccepted()
    {
        var record = TestData.CreateTransaction(Guid.NewGuid(), 1);

        var result = _validator.Validate(
            new GetTransactionsQuery(TestData.UserId, Guid.NewGuid(), CursorCodec.Encode(record.RowKey), 20));

        Assert.IsTrue(result.IsValid);
    }
}
