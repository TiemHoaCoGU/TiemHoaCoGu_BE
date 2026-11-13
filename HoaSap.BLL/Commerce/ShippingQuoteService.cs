using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HoaSap.DAL;
using HoaSap.Domain;
using HoaSap.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HoaSap.BLL.Commerce;

// Weight remains wire-compatible but is not trusted for pricing. Destination is derived from
// the street and canonical names selected from GHN's current two-level catalogue.
public sealed record ShippingQuoteRequest(string StreetAddress, int ProvinceId, string ProvinceName, int WardId, string WardName, decimal Weight = 0, List<CartItemRequest>? Items = null)
{
    public string Destination => $"{StreetAddress.Trim()}, {WardName.Trim()}, {ProvinceName.Trim()}";
}
public sealed record ShippingQuoteDto(Guid Id, string Origin, string Destination, decimal Weight, decimal Amount, DateTime ExpiresAt, string? TrackingNumber);

public sealed class ShippingQuoteService(HoaSapDbContext db, ICarrierAdapter carrier, IOptions<CarrierOptions> options)
{
    public async Task<ShippingQuoteDto> QuoteAsync(Guid customerId, ShippingQuoteRequest request, CancellationToken ct = default)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.StreetAddress) || request.StreetAddress.Trim().Length > 500 || request.ProvinceId <= 0 || string.IsNullOrWhiteSpace(request.ProvinceName) || request.ProvinceName.Trim().Length > 200 || request.WardId <= 0 || string.IsNullOrWhiteSpace(request.WardName) || request.WardName.Trim().Length > 200)
            throw new BusinessException(400, "Cần địa chỉ đường phố và tỉnh/phường hợp lệ từ danh mục GHN.");
        var destination = request.Destination;
        if (destination.Length > 2000) throw new BusinessException(400, "Địa chỉ giao hàng không hợp lệ.");
        var items = request.Items ?? [];
        if (items.Count == 0) items = await db.ShoppingCartItems.AsNoTracking().Where(x => x.CustomerId == customerId).Select(x => new CartItemRequest { ProductId = x.ProductId, Quantity = x.Quantity }).ToListAsync(ct);
        if (items.Count is 0 or > 100 || items.Any(x => x is null || x.Quantity is < 1 or > 1000) || items.GroupBy(x => x.ProductId).Any(x => x.Sum(y => (long)y.Quantity) > 1000)) throw new BusinessException(400, "Giỏ hàng không hợp lệ.");
        var groups = items.GroupBy(x => x.ProductId).Select(x => new { ProductId = x.Key, Quantity = x.Sum(y => y.Quantity) }).ToList();
        var ids = groups.Select(x => x.ProductId).ToArray();
        var products = await db.Products.AsNoTracking().Where(x => ids.Contains(x.Id) && x.IsActive && !x.IsPreOrder).ToDictionaryAsync(x => x.Id, ct);
        if (products.Count != groups.Count) throw new BusinessException(400, "Sản phẩm không còn bán hoặc cần quy trình đặt riêng.");
        if (products.Values.Any(x => !PackagingSpecifications.IsConfirmedAndComplete(x)))
            throw new BusinessException(503, "Thông số đóng gói thực tế của sản phẩm chưa đủ hoặc chưa được Admin xác nhận; chưa thể báo phí hay tạo đơn.");
        var snapshot = groups.Select(x =>
        {
            var product = products[x.ProductId];
            return (ProductId: x.ProductId, Quantity: x.Quantity, product.ShippingWeightGrams, product.PackageLengthCm, product.PackageWidthCm, product.PackageHeightCm, product.PackagingSpecsConfirmed);
        }).ToList();
        var weight = snapshot.Sum(x => (decimal)x.Quantity * x.ShippingWeightGrams!.Value) / 1000m;
        if (weight <= 0 || weight > 100000m) throw new BusinessException(400, "Trọng lượng kiện hàng không hợp lệ.");
        var cfg = options.Value;
        var origin = cfg.Origin?.Trim() ?? "";
        if (origin.Length == 0) throw new BusinessException(503, "Chưa cấu hình địa chỉ kho gửi hàng.");
        var customer = await db.Customers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == customerId, ct);
        CarrierRateResult result;
        var maxLength = snapshot.Max(x => (int)Math.Ceiling(x.PackageLengthCm ?? 10m));
        var maxWidth = snapshot.Max(x => (int)Math.Ceiling(x.PackageWidthCm ?? 10m));
        var totalHeight = snapshot.Sum(x => (int)Math.Ceiling((x.PackageHeightCm ?? 5m) * x.Quantity));
        var rateItems = groups.Select(x =>
        {
            var product = products[x.ProductId];
            return new CarrierShipmentItem(product.Name, product.Code, x.Quantity, product.Price, product.ShippingWeightGrams ?? 500,
                (int?)product.PackageLengthCm, (int?)product.PackageWidthCm, (int?)product.PackageHeightCm);
        }).ToList();
        var rateRequest = new CarrierRateRequest(origin, destination, weight, maxLength, maxWidth, totalHeight, request.ProvinceId, request.WardId,
            ToName: customer?.FullName, ToPhone: customer?.PhoneNumber, ToWardName: request.WardName.Trim(), ToProvinceName: request.ProvinceName.Trim(), Items: rateItems);
        try { result = await carrier.QuoteAsync(rateRequest, ct); }
        catch (InvalidOperationException) when (carrier is DisabledCarrier) { throw new BusinessException(503, "Nhà vận chuyển chưa được cấu hình; không tạo báo giá."); }
        catch (InvalidOperationException ex) { throw new BusinessException(502, ex.Message); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { throw new BusinessException(502, "Không kết nối được GHN staging; chưa tạo báo giá."); }
        if (!result.Accepted || result.Amount < 0 || result.Amount > 1_000_000_000m || decimal.Round(result.Amount, 2) != result.Amount) throw new BusinessException(400, "Nhà vận chuyển không cung cấp báo giá hợp lệ.");
        var lifetime = cfg.QuoteLifetimeMinutes is >= 1 and <= 1440 ? cfg.QuoteLifetimeMinutes : 15;
        var quote = new ShippingQuote { CustomerId = customerId, Origin = origin, Destination = destination, ProvinceId = request.ProvinceId, ProvinceName = request.ProvinceName.Trim(), WardId = request.WardId, WardName = request.WardName.Trim(), Weight = weight, CartFingerprint = Fingerprint(snapshot), Amount = result.Amount, TrackingNumber = result.TrackingNumber, ExpiresAt = DateTime.UtcNow.AddMinutes(lifetime) };
        db.Add(quote);
        await db.SaveChangesAsync(ct);
        return new(quote.Id, quote.Origin, quote.Destination, quote.Weight, quote.Amount, quote.ExpiresAt, quote.TrackingNumber);
    }

    internal static string Fingerprint(IEnumerable<(Guid ProductId, int Quantity, int? ShippingWeightGrams, decimal? PackageLengthCm, decimal? PackageWidthCm, decimal? PackageHeightCm, bool PackagingSpecsConfirmed)> lines) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", lines.OrderBy(x => x.ProductId).Select(x => string.Join(":",
            x.ProductId.ToString("N"),
            x.Quantity.ToString(CultureInfo.InvariantCulture),
            x.ShippingWeightGrams?.ToString(CultureInfo.InvariantCulture) ?? "-",
            FormatDimension(x.PackageLengthCm),
            FormatDimension(x.PackageWidthCm),
            FormatDimension(x.PackageHeightCm),
            x.PackagingSpecsConfirmed ? "1" : "0"))))));

    private static string FormatDimension(decimal? value) => value?.ToString("0.00", CultureInfo.InvariantCulture) ?? "-";
}
