using Microsoft.VisualStudio.TestTools.UnitTesting;
using SmeDemo.Application.Errors;
using SmeDemo.Application.Features.Transactions;

namespace SmeDemo.Application.Tests;

[TestClass]
public sealed class GetTransactionsHandlerTests
{
    private FakeCardRepository _repository = null!;
    private FakeTransactionStore _store = null!;
    private GetTransactionsHandler _handler = null!;
    private Guid _cardId;

    [TestInitialize]
    public void Initialize()
    {
        _cardId = Guid.NewGuid();
        _repository = new FakeCardRepository();
        _repository.Cards.Add(TestData.CreateCard(_cardId, TestData.UserId, 0));
        _store = new FakeTransactionStore();
        _handler = new GetTransactionsHandler(_repository, _store, TestData.CreateMapper());
    }

    [TestMethod]
    public async Task Handle_UsesDefaultLimitAndWrapsNextCursor()
    {
        for (var sequence = 1; sequence <= 25; sequence++)
        {
            _store.Records.Add(TestData.CreateTransaction(_cardId, sequence));
        }

        var response = await _handler.Handle(
            new GetTransactionsQuery(TestData.UserId, _cardId, null, null),
            CancellationToken.None);

        Assert.AreEqual(GetTransactionsQueryValidator.DefaultLimit, response.Items.Count);
        Assert.AreEqual(GetTransactionsQueryValidator.DefaultLimit, _store.LastLimit);
        Assert.IsNotNull(response.NextCursor);
        Assert.IsTrue(CursorCodec.TryDecode(response.NextCursor, out var rowKey));
        Assert.AreEqual(_store.Records.OrderBy(r => r.RowKey, StringComparer.Ordinal).ElementAt(19).RowKey, rowKey);
        Assert.AreEqual(25, _store.Records.Count);
    }

    [TestMethod]
    public async Task Handle_LastPageHasNullNextCursor()
    {
        _store.Records.Add(TestData.CreateTransaction(_cardId, 1));
        _store.Records.Add(TestData.CreateTransaction(_cardId, 2));

        var response = await _handler.Handle(
            new GetTransactionsQuery(TestData.UserId, _cardId, null, 10),
            CancellationToken.None);

        Assert.AreEqual(2, response.Items.Count);
        Assert.IsNull(response.NextCursor);
    }

    [TestMethod]
    public async Task Handle_CursorResumesAfterRowKey()
    {
        for (var sequence = 1; sequence <= 5; sequence++)
        {
            _store.Records.Add(TestData.CreateTransaction(_cardId, sequence));
        }

        var ordered = _store.Records.OrderBy(record => record.RowKey, StringComparer.Ordinal).ToArray();
        var cursor = CursorCodec.Encode(ordered[1].RowKey);

        var response = await _handler.Handle(
            new GetTransactionsQuery(TestData.UserId, _cardId, cursor, 2),
            CancellationToken.None);

        Assert.AreEqual(2, response.Items.Count);
        Assert.AreEqual(ordered[2].TransactionId.ToString(), response.Items[0].Id);
        Assert.AreEqual(ordered[3].TransactionId.ToString(), response.Items[1].Id);
        Assert.IsNotNull(response.NextCursor);
        Assert.AreEqual(ordered[1].RowKey, _store.LastCursor);
    }

    [TestMethod]
    public async Task Handle_UnknownCardThrowsNotFound()
    {
        await Assert.ThrowsExactlyAsync<ResourceNotFoundException>(() =>
            _handler.Handle(
                new GetTransactionsQuery(TestData.UserId, Guid.NewGuid(), null, 20),
                CancellationToken.None));
    }

    [TestMethod]
    public async Task Handle_OtherUsersCardThrowsForbidden()
    {
        await Assert.ThrowsExactlyAsync<ResourceForbiddenException>(() =>
            _handler.Handle(
                new GetTransactionsQuery(TestData.OtherUserId, _cardId, null, 20),
                CancellationToken.None));
    }
}
