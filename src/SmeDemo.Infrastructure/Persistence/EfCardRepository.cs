using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SmeDemo.Application.Abstractions;
using SmeDemo.Application.Errors;
using SmeDemo.Domain.Entities;

namespace SmeDemo.Infrastructure.Persistence;

public sealed class EfCardRepository : ICardRepository
{
    private readonly AppDbContext _db;

    public EfCardRepository(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<Card>> GetByUserAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _db.Cards.AsNoTracking()
                .Where(card => card.UserId == userId)
                .OrderBy(card => card.DisplayOrder)
                .ThenBy(card => card.Id)
                .ToListAsync(cancellationToken);
        }
        catch (Exception exception) when (IsStoreFailure(exception))
        {
            throw new StoreUnavailableException("The card store is unavailable.", exception);
        }
    }

    public async Task<Card?> GetByIdAsync(Guid cardId, CancellationToken cancellationToken)
    {
        try
        {
            return await _db.Cards.AsNoTracking()
                .SingleOrDefaultAsync(card => card.Id == cardId, cancellationToken);
        }
        catch (Exception exception) when (IsStoreFailure(exception))
        {
            throw new StoreUnavailableException("The card store is unavailable.", exception);
        }
    }

    private static bool IsStoreFailure(Exception exception) =>
        exception is DbException or RetryLimitExceededException;
}
