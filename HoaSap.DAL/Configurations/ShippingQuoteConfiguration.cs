using HoaSap.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace HoaSap.DAL.Configurations;
public sealed class ShippingQuoteConfiguration : IEntityTypeConfiguration<ShippingQuote>
{
    public void Configure(EntityTypeBuilder<ShippingQuote> b)
    {
        b.ToTable("ShippingQuotes", t => t.HasCheckConstraint("CK_ShippingQuotes_Amounts", "Weight > 0 AND Amount >= 0"));
        b.Property(x => x.CartFingerprint).HasMaxLength(64).IsRequired(); b.HasKey(x => x.Id);
        b.Property(x => x.Origin).HasMaxLength(500).IsRequired(); b.Property(x => x.Destination).HasMaxLength(2000).IsRequired();
        b.Property(x => x.ProvinceName).HasMaxLength(200); b.Property(x => x.WardName).HasMaxLength(200);
        b.Property(x => x.Weight).HasPrecision(18, 3); b.Property(x => x.Amount).HasPrecision(18, 2); b.Property(x => x.TrackingNumber).HasMaxLength(200);
        b.HasIndex(x => new { x.CustomerId, x.Id }); b.HasIndex(x => x.OrderId).IsUnique().HasFilter("[OrderId] IS NOT NULL");

    }
}
