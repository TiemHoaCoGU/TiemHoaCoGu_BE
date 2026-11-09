using HoaSap.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace HoaSap.DAL;

public static class DataSeeder
{
    public static async Task SeedAsync(HoaSapDbContext db, CancellationToken cancellationToken = default)
    {
        await db.Database.EnsureCreatedAsync(cancellationToken);
        if (!await db.Categories.AnyAsync(cancellationToken))
        {
            db.Categories.AddRange(
                new Category { Code = "bo", Name = "Bó hoa", Description = "Hoa sáp bó" },
                new Category { Code = "lo", Name = "Lọ/nến", Description = "Lọ hoa và nến thơm" },
                new Category { Code = "diy", Name = "Set DIY", Description = "Bộ tự làm hoa" });
            await db.SaveChangesAsync(cancellationToken);
        }
        if (!await db.Products.AnyAsync(cancellationToken))
        {
            var category = await db.Categories.OrderBy(c => c.Code).FirstAsync(cancellationToken);
            db.Products.AddRange(
                new Product { CategoryId = category.Id, Code = "HS-001", Name = "Bó hoa sáp thanh lịch", Price = 350000m, OriginalPrice = 400000m, Section = "arrivals", IsActive = true, ShippingWeightGrams = 500, PackageLengthCm = 30m, PackageWidthCm = 20m, PackageHeightCm = 15m, PackagingSpecsConfirmed = true },
                new Product { CategoryId = category.Id, Code = "HS-002", Name = "Bó hoa sáp pastel", Price = 420000m, Section = "discover", IsActive = true, ShippingWeightGrams = 600, PackageLengthCm = 35m, PackageWidthCm = 25m, PackageHeightCm = 18m, PackagingSpecsConfirmed = true });
            await db.SaveChangesAsync(cancellationToken);
        }
        // Each seeded product is backed by an inventory row.
        foreach (var product in await db.Products.Where(p => !db.Inventories.Any(i => i.ProductId == p.Id)).ToListAsync(cancellationToken))
            db.Inventories.Add(new Inventory { ProductId = product.Id, QuantityAvailable = 20 });
        await db.SaveChangesAsync(cancellationToken);
        if (!await db.Workshops.AnyAsync(cancellationToken))
        {
            var workshop = new Workshop { Code = "WS-BASIC", Name = "Workshop hoa sáp cơ bản", ShortName = "Cơ bản", Category = "Hoa sáp", BasePrice = 500000m, Duration = 150, MaxCapacity = 8, Description = "Tự tay hoàn thiện một bó hoa sáp.", IsActive = true };
            db.Workshops.Add(workshop);
            await db.SaveChangesAsync(cancellationToken);
            var date = DateTime.UtcNow.Date.AddDays(14);
            db.WorkshopSessions.Add(new WorkshopSession { WorkshopId = workshop.Id, SessionDate = date, StartTime = new TimeSpan(14, 0, 0), EndTime = new TimeSpan(16, 30, 0), Location = "Studio Hoa Sáp", Capacity = workshop.MaxCapacity, SessionStatus = "Open" });
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
