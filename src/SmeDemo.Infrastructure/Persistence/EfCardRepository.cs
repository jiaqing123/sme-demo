using Microsoft.EntityFrameworkCore;
using SmeDemo.Application.Abstractions;
using SmeDemo.Domain.Entities;

namespace SmeDemo.Infrastructure.Persistence;

public sealed class EfCardRepository : ICardRepository
{
    private readonly AppDbContext _db;

    public EfCardRepository(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<Card>> GetByUserAsync(
        Guid userId,
        CancellationToken cancellationToken) =>
        await _db.Cards.AsNoTracking()
            .Where(card => card.UserId == userId)
            .OrderBy(card => card.DisplayOrder)
            .ThenBy(card => card.Id)
            .ToListAsync(cancellationToken);

    public Task<Card?> GetByIdAsync(Guid cardId, CancellationToken cancellationToken) =>
        _db.Cards.AsNoTracking()
            .SingleOrDefaultAsync(card => card.Id == cardId, cancellationToken);
}
