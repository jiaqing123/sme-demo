using Microsoft.EntityFrameworkCore;

namespace SmeDemo.Auth.Persistence;

/// <summary>
/// The relational store owned by the auth service. No other service reads or
/// writes this database.
/// </summary>
public sealed class AuthDbContext : DbContext
{
    public const string SchemaName = "auth";

    public AuthDbContext(DbContextOptions<AuthDbContext> options)
        : base(options)
    {
    }

    public DbSet<AuthUser> Users => Set<AuthUser>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SchemaName);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AuthDbContext).Assembly);
    }
}
