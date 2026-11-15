using HoaSap.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace HoaSap.DAL.Configurations;
public sealed class CustomOrderRequestConfiguration : IEntityTypeConfiguration<CustomOrderRequest>
{
    public void Configure(EntityTypeBuilder<CustomOrderRequest> b)
    {
        b.ToTable("CustomOrderRequests", t => t.HasCheckConstraint("CK_CustomRequest_Quantity", "Quantity > 0"));
        b.HasKey(x => x.Id); b.Property(x => x.Description).HasMaxLength(4000).IsRequired(); b.Property(x => x.ReferenceImageUrl).HasMaxLength(2000); b.Property(x => x.ConcurrencyToken).IsConcurrencyToken();
        b.Property(x => x.DeliveryProvinceName).HasMaxLength(200); b.Property(x => x.DeliveryWardName).HasMaxLength(200);
        b.Property(x => x.Status).HasMaxLength(40).IsRequired(); b.HasIndex(x => new { x.CustomerId, x.CreatedAt });
    }
}
public sealed class CustomQuotationConfiguration : IEntityTypeConfiguration<CustomQuotation>
{
    public void Configure(EntityTypeBuilder<CustomQuotation> b)
    {
        b.ToTable("CustomQuotations"); b.HasKey(x => x.Id); b.Property(x => x.Status).HasMaxLength(40).IsRequired(); b.Property(x => x.ConcurrencyToken).IsConcurrencyToken();
        b.HasIndex(x => x.CustomOrderRequestId).IsUnique(); b.HasOne<CustomOrderRequest>().WithOne().HasForeignKey<CustomQuotation>(x => x.CustomOrderRequestId).OnDelete(DeleteBehavior.Restrict);
    }
}
public sealed class CustomQuotationRevisionConfiguration : IEntityTypeConfiguration<CustomQuotationRevision>
{
    public void Configure(EntityTypeBuilder<CustomQuotationRevision> b)
    {
        b.ToTable("CustomQuotationRevisions", t => t.HasCheckConstraint("CK_CustomQuote_Amounts", "UnitPrice >= 0 AND TotalAmount >= 0 AND DepositAmount >= 0 AND CAST(DepositAmount AS DECIMAL(18,2)) <= CAST(TotalAmount AS DECIMAL(18,2)) AND Quantity > 0"));
        b.HasKey(x => x.Id); b.Property(x => x.AdminNote).HasMaxLength(2000); b.Property(x => x.Status).HasMaxLength(40).IsRequired(); b.Property(x => x.UnitPrice).HasPrecision(18, 2); b.Property(x => x.TotalAmount).HasPrecision(18, 2); b.Property(x => x.DepositAmount).HasPrecision(18, 2);
        b.HasIndex(x => new { x.CustomQuotationId, x.Version }).IsUnique(); b.HasOne<CustomQuotation>().WithMany().HasForeignKey(x => x.CustomQuotationId).OnDelete(DeleteBehavior.Cascade);
    }
}
public sealed class CustomOrderCheckoutConfiguration : IEntityTypeConfiguration<CustomOrderCheckout>
{
    public void Configure(EntityTypeBuilder<CustomOrderCheckout> b)
    {
        b.ToTable("CustomOrderCheckouts"); b.HasKey(x => x.Id); b.Property(x => x.IdempotencyKey).HasMaxLength(100).IsRequired();
        b.HasIndex(x => new { x.CustomerId, x.IdempotencyKey }).IsUnique(); b.HasIndex(x => x.OrderId).IsUnique(); b.HasIndex(x => x.CustomQuotationId).IsUnique();
        b.HasOne<CustomQuotation>().WithMany().HasForeignKey(x => x.CustomQuotationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
    }
}
