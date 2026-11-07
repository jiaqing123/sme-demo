using Microsoft.VisualStudio.TestTools.UnitTesting;
using SmeDemo.Application.Features.Cards;
using SmeDemo.Application.Features.Transactions;

namespace SmeDemo.Application.Tests;

[TestClass]
public sealed class GetCardsHandlerTests
{
    [TestMethod]
    public async Task Handle_ReturnsCardsInDisplayOrderWithArtUrl()
    {
        var repository = new FakeCardRepository();
        var third = TestData.CreateCard(Guid.NewGuid(), TestData.UserId, 2, "1881");
        var first = TestData.CreateCard(Guid.NewGuid(), TestData.UserId, 0, "4242");
        var second = TestData.CreateCard(Guid.NewGuid(), TestData.UserId, 1, "5454");
        repository.Cards.AddRange([third, first, second]);
        repository.Cards.Add(TestData.CreateCard(Guid.NewGuid(), TestData.OtherUserId, 0, "9999"));

        var handler = new GetCardsHandler(repository, TestData.CreateMapper(), TestData.Options());

        var response = await handler.Handle(new GetCardsQuery(TestData.UserId), CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { first.Id, second.Id, third.Id },
            response.Items.Select(card => card.Id).ToArray());
        Assert.AreEqual(
            $"http://localhost:8082/api/v1/cards/{first.Id}/art",
            response.Items[0].ArtUrl);
        Assert.AreEqual("4242", response.Items[0].Last4);
        Assert.AreEqual(3, response.Items.Count);
    }
}
