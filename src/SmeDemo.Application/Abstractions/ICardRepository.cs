using SmeDemo.Domain.Entities;

namespace SmeDemo.Application.Abstractions;

/// <summary>
/// Read access to the cards owned by one user. Cards are relational and are
/// served from the API database; the application layer never sees EF Core.
/// </summary>
public interface ICardRepository
{
    Task<IReadOnlyList<Card>> GetByUserAsync(Guid userId, CancellationToken cancellationToken);

    Task<Card?> GetByIdAsync(Guid cardId, CancellationToken cancellationToken);
}
