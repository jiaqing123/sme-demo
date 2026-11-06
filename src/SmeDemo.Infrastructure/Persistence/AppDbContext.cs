using Microsoft.EntityFrameworkCore;
using SmeDemo.Domain.Entities;

namespace SmeDemo.Infrastructure.Persistence;

/// <summary>
/// The relational store owned by the API service: cards and the issuer sync
/// state. Transactions deliberately live in Table Storage, not here.
/// </summary>
public sealed class AppDbContext : DbContext
{
    public const string SchemaName = "app";

    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<Card> Cards => Set<Card>();

    public DbSet<IssuerSync> IssuerSyncs => Set<IssuerSync>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SchemaName);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
