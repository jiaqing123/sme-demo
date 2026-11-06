using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmeDemo.Domain.Entities;

namespace SmeDemo.Infrastructure.Persistence.Configurations;

internal sealed class IssuerSyncConfiguration : IEntityTypeConfiguration<IssuerSync>
{
    public void Configure(EntityTypeBuilder<IssuerSync> builder)
    {
        builder.ToTable("issuer_sync");

        builder.HasKey(sync => sync.CardId);
        builder.Property(sync => sync.CardId).ValueGeneratedNever();

        builder.Property(sync => sync.LastSyncedAt).IsRequired();

        builder.Property(sync => sync.LastStatus)
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(sync => sync.LastError)
            .HasMaxLength(512);
    }
}
