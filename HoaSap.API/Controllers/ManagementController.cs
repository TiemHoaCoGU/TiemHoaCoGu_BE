using System.ComponentModel.DataAnnotations;
using HoaSap.BLL;
using HoaSap.DAL;
using HoaSap.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace HoaSap.API.Controllers;

[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/management")]
public sealed class ManagementController(HoaSapDbContext db) : ControllerBase
{
    [HttpPost("products")]
    public async Task<Product> Product(AdminProductRequest request, CancellationToken ct)
    {
        if (request.Price < 0 || (request.OriginalPrice is < 0)) throw new BusinessException(400, "Price cannot be negative");
        if (decimal.Round(request.Price, 2) != request.Price || (request.OriginalPrice is { } original && decimal.Round(original, 2) != original))
            throw new BusinessException(400, "Price supports at most two decimal places");
        request.ValidatePackaging();
        if (!await db.Categories.AnyAsync(x => x.Id == request.CategoryId, ct)) throw new BusinessException(404, "Category not found");
        ValidateMediaUrl(request.ThumbnailUrl, "Thumbnail URL");
        var code = request.Code.Trim();
        var name = request.Name.Trim();
        if (code.Length == 0 || name.Length == 0) throw new BusinessException(400, "Product code/name is required");
        if (await db.Products.AnyAsync(x => x.Code == code, ct)) throw new BusinessException(409, "Product code exists");
        var product = new Product
        {
            Id = Guid.NewGuid(),
            CategoryId = request.CategoryId,
            Code = code,
            Name = name,
            Price = request.Price,
            OriginalPrice = request.OriginalPrice,
            ThumbnailUrl = request.ThumbnailUrl?.Trim() ?? "",
            Description = request.Description?.Trim() ?? "",
            Badge = request.Badge?.Trim() ?? "",
            Section = request.Section?.Trim() ?? "",
            IsCustomizable = request.IsCustomizable,
            IsPreOrder = request.IsPreOrder,
            ShippingWeightGrams = request.ShippingWeightGrams,
            PackageLengthCm = request.PackageLengthCm,
            PackageWidthCm = request.PackageWidthCm,
            PackageHeightCm = request.PackageHeightCm,
            PackagingSpecsConfirmed = request.PackagingSpecsConfirmed,
            IsActive = request.IsActive,
            CreatedAt = DateTime.UtcNow
        };
        db.Products.Add(product);
        await db.SaveChangesAsync(ct);
        return product;
    }

    [HttpPatch("products/{id:guid}/active")]
    public async Task<IActionResult> Active(Guid id, [FromQuery] bool value, CancellationToken ct)
    {
        var product = await db.Products.FindAsync([id], ct) ?? throw new BusinessException(404, "Product not found");
        product.IsActive = value;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPut("inventory/{productId:guid}")]
    public async Task<IActionResult> Stock(Guid productId, [FromQuery] int quantity, CancellationToken ct)
    {
        if (quantity < 0) return BadRequest("Quantity cannot be negative");
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        if (!await db.Products.AnyAsync(x => x.Id == productId, ct)) throw new BusinessException(404, "Product not found");
        var inventory = await db.Inventories.SingleOrDefaultAsync(x => x.ProductId == productId, ct);
        if (inventory is null)
        {
            db.Inventories.Add(new Inventory { Id = Guid.NewGuid(), ProductId = productId, QuantityAvailable = quantity, QuantityReserved = 0, LastUpdated = DateTime.UtcNow });
        }
        else
        {
            // Available and reserved are separate buckets; setting available never changes reserved.
            inventory.QuantityAvailable = quantity;
            inventory.LastUpdated = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return NoContent();
    }

    [HttpPost("vouchers")]
    public async Task<Voucher> Voucher(AdminVoucherRequest request, CancellationToken ct)
    {
        var code = request.Code.Trim();
        if (code.Length == 0 || request.StartDate >= request.EndDate || request.UsageLimit < 1) throw new BusinessException(400, "Voucher code/dates/limit invalid");
        if (request.DiscountType is not ("Percentage" or "FixedAmount") || request.DiscountValue <= 0 || request.MinOrderAmount < 0 || request.MaxDiscountAmount is < 0)
            throw new BusinessException(400, "Voucher discount is invalid");
        if (request.DiscountType == "Percentage" && request.DiscountValue > 100) throw new BusinessException(400, "Percentage must be between 0 and 100");
        if (decimal.Round(request.DiscountValue, 2) != request.DiscountValue || decimal.Round(request.MinOrderAmount, 2) != request.MinOrderAmount ||
            (request.MaxDiscountAmount is { } maximum && decimal.Round(maximum, 2) != maximum))
            throw new BusinessException(400, "Voucher amounts support at most two decimal places");
        if (await db.Vouchers.AnyAsync(x => x.Code == code, ct)) throw new BusinessException(409, "Voucher code exists");
        var voucher = new Voucher { Id = Guid.NewGuid(), Code = code, Description = request.Description?.Trim() ?? "", DiscountType = request.DiscountType, DiscountValue = request.DiscountValue, MinOrderAmount = request.MinOrderAmount, MaxDiscountAmount = request.MaxDiscountAmount, StartDate = request.StartDate, EndDate = request.EndDate, UsageLimit = request.UsageLimit, UsedCount = 0, IsActive = true };
        db.Vouchers.Add(voucher);
        await db.SaveChangesAsync(ct);
        return voucher;
    }

    private static void ValidateMediaUrl(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var candidate = value.Trim();
        if (Regex.IsMatch(candidate, @"^/api/media/[0-9a-fA-F]{32}\.(png|jpg|webp)$", RegexOptions.CultureInvariant)) return;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || candidate.Any(char.IsWhiteSpace))
            throw new BusinessException(400, $"{field} must be an HTTP(S) URL or managed media URL");
    }
}

public sealed class AdminCategoryRequest
{
    [Required, StringLength(40)] public string Code { get; set; } = "";
    [Required, StringLength(200)] public string Name { get; set; } = "";
    [StringLength(1000)] public string? Description { get; set; }
    [StringLength(2000)] public string? ImageUrl { get; set; }
}

public sealed class AdminProductRequest
{
    private int? _shippingWeightGrams;
    private decimal? _packageLengthCm;
    private decimal? _packageWidthCm;
    private decimal? _packageHeightCm;
    private bool _packagingSpecsConfirmed;

    [Required] public Guid CategoryId { get; set; }
    [Required, StringLength(80)] public string Code { get; set; } = "";
    [Required, StringLength(300)] public string Name { get; set; } = "";
    [Range(0, 1000000000)] public decimal Price { get; set; }
    [Range(0, 1000000000)] public decimal? OriginalPrice { get; set; }
    [StringLength(2000)] public string? ThumbnailUrl { get; set; }
    [StringLength(4000)] public string? Description { get; set; }
    [StringLength(100)] public string? Badge { get; set; }
    [StringLength(100)] public string? Section { get; set; }
    public bool IsCustomizable { get; set; }
    [Range(1, 1000000)] public int? ShippingWeightGrams { get => _shippingWeightGrams; set { _shippingWeightGrams = value; PackagingFieldsWereProvided = true; } }
    [Range(typeof(decimal), "0.01", "10000", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)] public decimal? PackageLengthCm { get => _packageLengthCm; set { _packageLengthCm = value; PackagingFieldsWereProvided = true; } }
    [Range(typeof(decimal), "0.01", "10000", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)] public decimal? PackageWidthCm { get => _packageWidthCm; set { _packageWidthCm = value; PackagingFieldsWereProvided = true; } }
    [Range(typeof(decimal), "0.01", "10000", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)] public decimal? PackageHeightCm { get => _packageHeightCm; set { _packageHeightCm = value; PackagingFieldsWereProvided = true; } }
    public bool PackagingSpecsConfirmed { get => _packagingSpecsConfirmed; set { _packagingSpecsConfirmed = value; PackagingFieldsWereProvided = true; } }
    [JsonIgnore] public bool PackagingFieldsWereProvided { get; private set; }
    public bool IsPreOrder { get; set; }
    public bool IsActive { get; set; } = true;

    public void ValidatePackaging()
    {
        if (new decimal?[] { PackageLengthCm, PackageWidthCm, PackageHeightCm }.Any(x => x is { } value && decimal.Round(value, 2) != value))
            throw new BusinessException(400, "Package dimensions support at most two decimal places.");
        if (PackagingSpecsConfirmed && (ShippingWeightGrams is null || PackageLengthCm is null || PackageWidthCm is null || PackageHeightCm is null))
            throw new BusinessException(400, "Cannot confirm packaging until weight, length, width, and height are all entered.");
    }
}

public sealed class AdminVoucherRequest
{
    [Required, StringLength(40)] public string Code { get; set; } = "";
    [StringLength(1000)] public string? Description { get; set; }
    [Required] public string DiscountType { get; set; } = "Percentage";
    [Range(typeof(decimal), "0.01", "1000000000", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)] public decimal DiscountValue { get; set; }
    [Range(typeof(decimal), "0", "1000000000", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)] public decimal MinOrderAmount { get; set; }
    [Range(typeof(decimal), "0", "1000000000", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)] public decimal? MaxDiscountAmount { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    [Range(1, int.MaxValue)] public int UsageLimit { get; set; }
}
