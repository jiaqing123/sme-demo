using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmeDemo.Domain.Entities;

namespace SmeDemo.Infrastructure.Persistence.Configurations;

internal sealed class CardConfiguration : IEntityTypeConfiguration<Card>
{
    public void Configure(EntityTypeBuilder<Card> builder)
    {
        builder.ToTable("cards");

        builder.HasKey(card => card.Id);
        builder.Property(card => card.Id).ValueGeneratedNever();

        builder.Property(card => card.UserId).IsRequired();

        builder.Property(card => card.OrgId)
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(card => card.Brand)
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(card => card.Last4)
            .HasMaxLength(Card.Last4Length)
            .IsFixedLength()
            .IsRequired();

        builder.Property(card => card.ExpiryMonth).IsRequired();
        builder.Property(card => card.ExpiryYear).IsRequired();

        builder.Property(card => card.Status)
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(card => card.Currency)
            .HasMaxLength(3)
            .IsFixedLength()
            .IsRequired();

        builder.Property(card => card.DisplayOrder).IsRequired();

        builder.Property(card => card.ArtBlobName)
            .HasMaxLength(128)
            .IsRequired();

        builder.HasIndex(card => new { card.UserId, card.DisplayOrder });
    }
}
