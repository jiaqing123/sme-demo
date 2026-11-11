using Microsoft.VisualStudio.TestTools.UnitTesting;
using SmeDemo.Application.Abstractions;
using SmeDemo.Application.Contracts;
using SmeDemo.Application.Decorators;
using SmeDemo.Application.Errors;
using SmeDemo.Application.Features.Transactions;

namespace SmeDemo.Application.Tests;

[TestClass]
public sealed class ValidationQueryHandlerTests
{
    [TestMethod]
    public async Task InvalidQuery_ThrowsValidationErrorBeforeCallingHandler()
    {
        var inner = new RecordingHandler();
        var decorator = CreateDecorator(inner);

        var exception = await Assert.ThrowsExactlyAsync<RequestValidationException>(() =>
            decorator.HandleAsync(
                new GetTransactionsQuery(TestData.UserId, Guid.NewGuid(), null, 101),
                CancellationToken.None));

        Assert.AreEqual(1, exception.Errors.Count);
        Assert.IsTrue(exception.Errors.ContainsKey("limit"));
        Assert.IsFalse(inner.Called);
    }

    [TestMethod]
    public async Task InvalidCursor_IsReportedUnderSnakeCaseFieldName()
    {
        var decorator = CreateDecorator(new RecordingHandler());

        var exception = await Assert.ThrowsExactlyAsync<RequestValidationException>(() =>
            decorator.HandleAsync(
                new GetTransactionsQuery(TestData.UserId, Guid.NewGuid(), "!!!not-base64!!!", null),
                CancellationToken.None));

        Assert.IsTrue(exception.Errors.ContainsKey("cursor"));
        Assert.IsFalse(exception.Errors.ContainsKey("Cursor"));
    }

    [TestMethod]
    public async Task ValidQuery_ReturnsTheInnerHandlerResult()
    {
        var inner = new RecordingHandler { Response = new TransactionListResponse([], "next") };
        var decorator = CreateDecorator(inner);

        var response = await decorator.HandleAsync(
            new GetTransactionsQuery(TestData.UserId, Guid.NewGuid(), null, 10),
            CancellationToken.None);

        Assert.IsTrue(inner.Called);
        Assert.AreSame(inner.Response, response);
    }

    [TestMethod]
    public async Task NoValidators_PassesStraightThrough()
    {
        var inner = new RecordingHandler();
        var decorator = new ValidationQueryHandler<GetTransactionsQuery, TransactionListResponse>(
            inner,
            []);

        await decorator.HandleAsync(
            new GetTransactionsQuery(TestData.UserId, Guid.NewGuid(), null, 101),
            CancellationToken.None);

        Assert.IsTrue(inner.Called);
    }

    private static ValidationQueryHandler<GetTransactionsQuery, TransactionListResponse> CreateDecorator(
        RecordingHandler inner) =>
        new(inner, [new GetTransactionsQueryValidator()]);

    private sealed class RecordingHandler : IQueryHandler<GetTransactionsQuery, TransactionListResponse>
    {
        public bool Called { get; private set; }

        public TransactionListResponse Response { get; init; } = new([], null);

        public Task<TransactionListResponse> HandleAsync(
            GetTransactionsQuery query,
            CancellationToken cancellationToken = default)
        {
            Called = true;
            return Task.FromResult(Response);
        }
    }
}
