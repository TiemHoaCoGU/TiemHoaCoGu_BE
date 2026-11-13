using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HoaSap.BLL;
using HoaSap.DAL;
using HoaSap.Domain;
using HoaSap.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace HoaSap.BLL.Commerce;

public sealed record PageResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);
public sealed record ProductDto(Guid Id, Guid CategoryId, string Code, string Name, decimal Price, decimal? OriginalPrice, string? ThumbnailUrl, string? Description, string? Badge, string? Section, bool IsCustomizable, bool IsPreOrder, bool IsActive, int QuantityAvailable);
public sealed record CartLineDto(Guid Id, Guid ProductId, string ProductName, int Quantity, decimal UnitPrice, decimal LineTotal, string? CustomNote, string? ReferenceImageUrl);
public sealed record OrderDto(Guid Id, string OrderCode, decimal SubTotal, decimal DiscountAmount, decimal ShippingFee, decimal TotalAmount, string OrderStatus, string PaymentStatus, string ShippingAddress, DateTime CreatedAt, IReadOnlyList<OrderLineDto> Items);
public sealed record OrderLineDto(Guid Id, Guid ProductId, int Quantity, decimal UnitPrice, decimal FinalPrice, int ShippedQuantity, string? CustomNote, string? ReferenceImageUrl);
public sealed record DeliveryDto(Guid Id, string DeliveryCode, string? ShipperUnit, string? TrackingNumber, string DeliveryStatus, IReadOnlyList<DeliveryLineDto> Items, decimal ShippingCost = 0);
public sealed record DeliveryLineDto(Guid OrderItemId, int Quantity);

public sealed class CatalogQuery
{
    [Range(1, int.MaxValue)] public int Page { get; set; } = 1;
    [Range(1, 100)] public int PageSize { get; set; } = 20;
    public string? Search { get; set; }
    public Guid? CategoryId { get; set; }
    public string? Section { get; set; }
}

public sealed class CartItemRequest
{
    public Guid ProductId { get; set; }
    [Range(1, 1000)] public int Quantity { get; set; }
    [MaxLength(1000)] public string? CustomNote { get; set; }
    [Url, MaxLength(2000)] public string? ReferenceImageUrl { get; set; }
}

public sealed class CheckoutRequest
{
    [Required, MaxLength(2000)] public string ShippingAddress { get; set; } = "";
    [MaxLength(2000)] public string? ReceiverName { get; set; }
    [RegularExpression(@"^\+?[0-9]{9,15}$"), MaxLength(30)] public string? ReceiverPhone { get; set; }
    [EmailAddress, MaxLength(320)] public string? ReceiverEmail { get; set; }
    [MaxLength(1000)] public string? Note { get; set; }
    [MaxLength(40)] public string? VoucherCode { get; set; }
    [MaxLength(100)] public string? IdempotencyKey { get; set; }
    public List<CartItemRequest> Items { get; set; } = [];
    public Guid? ShippingQuoteId { get; set; }
}

public sealed class StatusRequest
{
    [Required] public string Status { get; set; } = "";
}

public sealed class DeliveryRequest
{
    [MaxLength(100)] public string? ShipperUnit { get; set; }
    [MaxLength(200)] public string? TrackingNumber { get; set; }
    [Required] public List<DeliveryLineRequest> Items { get; set; } = [];
}

public sealed class DeliveryLineRequest
{
    public Guid OrderItemId { get; set; }
    [Range(1, 100000)] public int Quantity { get; set; }
}

public sealed class CommerceService(HoaSapDbContext db, ICarrierAdapter? carrier = null)
{
    public static readonly TimeSpan UnpaidOrderLifetime = TimeSpan.FromMinutes(30);
    static BusinessException E(int code, string message) => new(code, message);
    static BusinessException Bad(string message) => E(400, message);
    static BusinessException NF(string message) => E(404, message);
    static BusinessException Conflict(string message) => E(409, message);

    public async Task<PageResult<ProductDto>> CatalogAsync(CatalogQuery q, CancellationToken ct = default)
    {
        q.Page = Math.Max(1, q.Page);
        q.PageSize = Math.Clamp(q.PageSize, 1, 100);
        var query = db.Products.AsNoTracking().Where(p => p.IsActive);
        if (q.CategoryId is { } categoryId) query = query.Where(p => p.CategoryId == categoryId);
        if (!string.IsNullOrWhiteSpace(q.Section)) query = query.Where(p => p.Section == q.Section);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var search = q.Search.Trim();
            query = query.Where(p => p.Name.Contains(search) || p.Code.Contains(search));
        }

        var total = await query.CountAsync(ct);
        if ((long)(q.Page - 1) * q.PageSize >= total) return new([], q.Page, q.PageSize, total);
        var items = await query.OrderByDescending(p => p.CreatedAt).Skip((q.Page - 1) * q.PageSize).Take(q.PageSize)
            .Select(p => new ProductDto(p.Id, p.CategoryId, p.Code, p.Name, p.Price, p.OriginalPrice, p.ThumbnailUrl, p.Description, p.Badge, p.Section, p.IsCustomizable, p.IsPreOrder, p.IsActive, p.Inventory == null ? 0 : p.Inventory.QuantityAvailable))
            .ToListAsync(ct);
        return new(items, q.Page, q.PageSize, total);
    }

    public async Task<IReadOnlyList<CartLineDto>> CartAsync(Guid customerId, CancellationToken ct = default) =>
        await db.ShoppingCartItems.AsNoTracking().Where(i => i.CustomerId == customerId)
            .Join(db.Products, i => i.ProductId, p => p.Id, (i, p) => new CartLineDto(i.Id, i.ProductId, p.Name, i.Quantity, p.Price, p.Price * i.Quantity, i.CustomNote, i.ReferenceImageUrl)).ToListAsync(ct);

    public async Task AddCartAsync(Guid customerId, CartItemRequest request, CancellationToken ct = default)
    {
        if (request.Quantity is < 1 or > 1000) throw Bad("Số lượng không hợp lệ");
        var product = await db.Products.SingleOrDefaultAsync(p => p.Id == request.ProductId && p.IsActive, ct) ?? throw NF("Sản phẩm không tồn tại");
        if (!product.IsCustomizable && (!string.IsNullOrWhiteSpace(request.CustomNote) || !string.IsNullOrWhiteSpace(request.ReferenceImageUrl)))
            throw Bad("Sản phẩm này không cho phép ghi chú tùy chỉnh");

        var item = await db.ShoppingCartItems.SingleOrDefaultAsync(x => x.CustomerId == customerId && x.ProductId == request.ProductId, ct);
        if (item is null)
        {
            db.ShoppingCartItems.Add(new ShoppingCartItem { Id = Guid.NewGuid(), CustomerId = customerId, ProductId = request.ProductId, Quantity = request.Quantity, CustomNote = request.CustomNote ?? "", ReferenceImageUrl = request.ReferenceImageUrl ?? "", CreatedAt = DateTime.UtcNow });
        }
        else
        {
            if (item.Quantity > 1000 - request.Quantity) throw Bad("Tổng số lượng vượt giới hạn");
            item.Quantity += request.Quantity;
            item.CustomNote = request.CustomNote ?? item.CustomNote ?? "";
            item.ReferenceImageUrl = request.ReferenceImageUrl ?? item.ReferenceImageUrl ?? "";
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveCartAsync(Guid customerId, Guid itemId, CancellationToken ct = default)
    {
        var item = await db.ShoppingCartItems.SingleOrDefaultAsync(x => x.Id == itemId && x.CustomerId == customerId, ct) ?? throw NF("Dòng giỏ hàng không tồn tại");
        db.ShoppingCartItems.Remove(item);
        await db.SaveChangesAsync(ct);
    }

    public async Task<OrderDto> CheckoutAsync(Guid? customerId, CheckoutRequest request, CancellationToken ct = default)
    {
        if (request is null) throw Bad("Dữ liệu checkout không hợp lệ");
        if (string.IsNullOrWhiteSpace(request.ShippingAddress)) throw Bad("Thiếu địa chỉ giao hàng");
        if (customerId is null && (string.IsNullOrWhiteSpace(request.ReceiverName) || string.IsNullOrWhiteSpace(request.ReceiverPhone)))
            throw Bad("Khách vãng lai phải cung cấp tên và số điện thoại người nhận");
        if (request.Items is null || request.Items.Count > 100 || request.Items.Any(x => x is null || x.Quantity is < 1 or > 1000))
            throw Bad("Danh sách sản phẩm không hợp lệ; tối đa 100 dòng");
        if (request.IdempotencyKey?.Length > 100) throw Bad("IdempotencyKey tối đa 100 ký tự");
        var key = string.IsNullOrWhiteSpace(request.IdempotencyKey) ? null : request.IdempotencyKey.Trim();
        var fingerprint = Fingerprint(customerId, request);

        // Idempotency is deliberately checked before reading the cart: a replay must work after its cart snapshot was deleted.
        if (key is not null)
        {
            var prior = await db.Orders.AsNoTracking().Include(x => x.Items)
                .SingleOrDefaultAsync(x => x.CustomerId == customerId && x.IdempotencyKey == key, ct);
            if (prior is not null)
            {
                if (!string.Equals(prior.RequestFingerprint, fingerprint, StringComparison.Ordinal))
                    throw Conflict("IdempotencyKey đã được dùng cho payload khác");
                return Map(prior, await ShippedQuantitiesAsync(prior.Items.Select(x => x.Id), ct));
            }
        }

        var requested = request.Items ?? throw Bad("Danh sách sản phẩm không hợp lệ");
        // Validate every raw line before grouping, including null/zero/negative/overflow input.
        if (requested.Any(x => x is null || x.Quantity < 1 || x.Quantity > 1000)) throw Bad("Số lượng không hợp lệ");
        var lines = new List<Line>();
        foreach (var group in requested.GroupBy(x => x.ProductId))
        {
            var total = group.Sum(x => (long)x.Quantity);
            if (total is < 1 or > 1000) throw Bad("Số lượng không hợp lệ");
            var last = group.Last(); lines.Add(new Line(group.Key, (int)total, last.CustomNote, last.ReferenceImageUrl));
        }
        if (customerId is { } id && lines.Count == 0)
            lines = (await db.ShoppingCartItems.AsNoTracking().Where(x => x.CustomerId == id).ToListAsync(ct)).Select(x => new Line(x.ProductId, x.Quantity, x.CustomNote, x.ReferenceImageUrl)).ToList();
        if (lines.Count == 0) throw Bad("Giỏ hàng trống");
        if (lines.Any(x => x.Quantity < 1 || x.Quantity > 1000)) throw Bad("Số lượng không hợp lệ");
        if (request.ShippingQuoteId is null) throw new BusinessException(503, "Chưa có báo giá vận chuyển hợp lệ; không thể tạo đơn.");

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (customerId is null) throw Bad("Báo giá vận chuyển yêu cầu đăng nhập");
        var quoteId = request.ShippingQuoteId.Value;
        var shippingQuote = await db.ShippingQuotes.SingleOrDefaultAsync(x => x.Id == quoteId, ct) ?? throw Bad("Báo giá vận chuyển không hợp lệ");
        if (shippingQuote.CustomerId != customerId.Value || shippingQuote.Destination != request.ShippingAddress.Trim() || shippingQuote.ExpiresAt <= DateTime.UtcNow || shippingQuote.UsedAt is not null || shippingQuote.OrderId is not null) throw Conflict("Báo giá vận chuyển không hợp lệ hoặc đã hết hạn");
        if (shippingQuote.ProvinceId <= 0 || string.IsNullOrWhiteSpace(shippingQuote.ProvinceName) || shippingQuote.WardId <= 0 || string.IsNullOrWhiteSpace(shippingQuote.WardName)) throw Conflict("Báo giá thiếu địa chỉ GHN 2 cấp; vui lòng lấy lại báo giá vận chuyển");
        var shippingFee = shippingQuote.Amount;
        // Re-check inside the serializable transaction to close the race between pre-check and insert.
        if (key is not null)
        {
            var prior = await db.Orders.Include(x => x.Items).SingleOrDefaultAsync(x => x.CustomerId == customerId && x.IdempotencyKey == key, ct);
            if (prior is not null)
            {
                if (!string.Equals(prior.RequestFingerprint, fingerprint, StringComparison.Ordinal)) throw Conflict("IdempotencyKey đã được dùng cho payload khác");
                await transaction.RollbackAsync(ct);
                return Map(prior, await ShippedQuantitiesAsync(prior.Items.Select(x => x.Id), ct));
            }
        }
        var products = await db.Products.Where(p => lines.Select(x => x.ProductId).Contains(p.Id) && p.IsActive).ToDictionaryAsync(p => p.Id, ct);
        if (products.Count != lines.Count) throw Bad("Có sản phẩm không còn bán");
        if (products.Values.Any(x => !PackagingSpecifications.IsConfirmedAndComplete(x))) throw Conflict("Thông số đóng gói chưa đủ hoặc chưa được xác nhận; cần xác minh trước khi tạo đơn");
        var packaging = lines.Select(x =>
        {
            var product = products[x.ProductId];
            return (x.ProductId, x.Quantity, product.ShippingWeightGrams, product.PackageLengthCm, product.PackageWidthCm, product.PackageHeightCm, product.PackagingSpecsConfirmed);
        });
        if (shippingQuote.CartFingerprint != ShippingQuoteService.Fingerprint(packaging)) throw Conflict("Giỏ hàng hoặc thông số đóng gói đã thay đổi, cần lấy lại báo giá vận chuyển");
        decimal subtotal = 0;
        foreach (var line in lines)
        {
            var product = products[line.ProductId];
            if (product.IsPreOrder) throw Conflict("Sản phẩm đặt trước cần tạo yêu cầu riêng và duyệt báo giá");
            if (!product.IsCustomizable && (!string.IsNullOrWhiteSpace(line.Note) || !string.IsNullOrWhiteSpace(line.Image))) throw Bad("Sản phẩm không cho phép tùy chỉnh");
            var inventoryToken = Guid.NewGuid();
            var affected = await db.ExecuteUpdateAuditedAsync(
                db.Inventories.Where(i => i.ProductId == line.ProductId && i.QuantityAvailable >= line.Quantity),
                x => x.SetProperty(i => i.QuantityAvailable, i => i.QuantityAvailable - line.Quantity)
                    .SetProperty(i => i.QuantityReserved, i => i.QuantityReserved + line.Quantity)
                    .SetProperty(i => i.LastUpdated, DateTime.UtcNow)
                    .SetProperty(i => i.ConcurrencyToken, inventoryToken), ct);
            if (affected != 1) throw Conflict("Tồn kho vừa thay đổi, vui lòng thử lại");
            subtotal += product.Price * line.Quantity;
        }
        decimal discount = 0; Voucher? voucher = null;
        if (!string.IsNullOrWhiteSpace(request.VoucherCode))
        {
            voucher = await db.Vouchers.AsNoTracking().SingleOrDefaultAsync(x => x.Code == request.VoucherCode.Trim() && x.IsActive, ct) ?? throw Bad("Voucher không hợp lệ");
            var now = DateTime.UtcNow;
            if (now < voucher.StartDate || now > voucher.EndDate || voucher.UsedCount >= voucher.UsageLimit || subtotal < voucher.MinOrderAmount) throw Bad("Voucher không đủ điều kiện");
            discount = voucher.DiscountType == "Percentage" ? subtotal * voucher.DiscountValue / 100m : voucher.DiscountValue;
            if (voucher.MaxDiscountAmount is { } maximum) discount = Math.Min(discount, maximum);
            discount = Math.Round(Math.Min(discount, subtotal), 0, MidpointRounding.AwayFromZero);
            if (await db.ExecuteUpdateAuditedAsync(
                    db.Vouchers.Where(v => v.Id == voucher.Id && v.IsActive && v.UsedCount < v.UsageLimit),
                    x => x.SetProperty(v => v.UsedCount, v => v.UsedCount + 1), ct) != 1)
                throw Conflict("Voucher vừa hết lượt sử dụng");
        }
        var customer = customerId is { } buyerId ? await db.Customers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == buyerId, ct) : null;
        var address = $"ReceiverName: {(request.ReceiverName ?? customer?.FullName ?? "")}; ReceiverPhone: {(request.ReceiverPhone ?? customer?.PhoneNumber ?? "")}; ReceiverEmail: {(request.ReceiverEmail ?? customer?.Email ?? "")}; Address: {request.ShippingAddress.Trim()}";
        var order = new Order { Id = Guid.NewGuid(), CustomerId = customerId, IdempotencyKey = key, RequestFingerprint = fingerprint, VoucherId = voucher?.Id, OrderCode = $"ORD-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid():N}", SubTotal = subtotal, DiscountAmount = discount, ShippingFee = shippingFee, TotalAmount = subtotal - discount + shippingFee, ShippingQuoteId = shippingQuote.Id, OrderStatus = "Confirmed", PaymentStatus = "Unpaid", ShippingAddress = address, ShippingProvinceId = shippingQuote.ProvinceId, ShippingProvinceName = shippingQuote.ProvinceName, ShippingWardId = shippingQuote.WardId, ShippingWardName = shippingQuote.WardName, Note = request.Note ?? "", CreatedAt = DateTime.UtcNow };
        foreach (var line in lines) { var product = products[line.ProductId]; order.Items.Add(new OrderItem { Id = Guid.NewGuid(), OrderId = order.Id, ProductId = product.Id, Quantity = line.Quantity, UnitPrice = product.Price, FinalPrice = product.Price, CustomNote = line.Note ?? "", ReferenceImageUrl = line.Image ?? "" }); }
        shippingQuote.UsedAt = DateTime.UtcNow; shippingQuote.OrderId = order.Id;
        db.Orders.Add(order);
        if (customerId is { } ownerId) db.ShoppingCartItems.RemoveRange(await db.ShoppingCartItems.Where(x => x.CustomerId == ownerId && lines.Select(z => z.ProductId).Contains(x.ProductId)).ToListAsync(ct));
        try { await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); }
        catch (DbUpdateException) { throw Conflict("Yêu cầu checkout trùng hoặc vừa thay đổi, vui lòng thử lại"); }
        return Map(order);
    }

    public async Task<IReadOnlyList<OrderDto>> OwnOrdersAsync(Guid customerId, CancellationToken ct = default)
    {
        var orders = await db.Orders.AsNoTracking().Include(x => x.Items).Where(x => x.CustomerId == customerId).OrderByDescending(x => x.CreatedAt).Take(200).ToListAsync(ct);
        var shipped = await ShippedQuantitiesAsync(orders.SelectMany(x => x.Items).Select(x => x.Id), ct);
        return orders.Select(x => Map(x, shipped)).ToList();
    }

    public async Task<OrderDto> OwnOrderAsync(Guid customerId, Guid orderId, CancellationToken ct = default)
    {
        var order = await db.Orders.Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == orderId && x.CustomerId == customerId, ct) ?? throw NF("Không tìm thấy đơn");
        return Map(order, await ShippedQuantitiesAsync(order.Items.Select(x => x.Id), ct));
    }

    public async Task<IReadOnlyList<DeliveryDto>> DeliveriesAsync(Guid customerId, Guid orderId, CancellationToken ct = default)
    {
        if (!await db.Orders.AnyAsync(x => x.Id == orderId && x.CustomerId == customerId, ct)) throw NF("Không tìm thấy đơn");
        return await db.Deliveries.AsNoTracking().Include(x => x.Items).Where(x => x.OrderId == orderId).OrderBy(x => x.DeliveryCode)
            .Select(x => new DeliveryDto(x.Id, x.DeliveryCode, x.ShipperUnit, x.TrackingNumber, x.DeliveryStatus, x.Items.Select(i => new DeliveryLineDto(i.OrderItemId, i.ShippedQuantity)).ToList(), x.ShippingCost)).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<DeliveryDto>> StaffDeliveriesAsync(Guid orderId, CancellationToken ct = default) =>
        await db.Deliveries.AsNoTracking().Include(x => x.Items).Where(x => x.OrderId == orderId).OrderBy(x => x.DeliveryCode)
            .Select(x => new DeliveryDto(x.Id, x.DeliveryCode, x.ShipperUnit, x.TrackingNumber, x.DeliveryStatus, x.Items.Select(i => new DeliveryLineDto(i.OrderItemId, i.ShippedQuantity)).ToList(), x.ShippingCost)).ToListAsync(ct);

    public async Task CancelAsync(Guid customerId, Guid orderId, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var order = await db.Orders.Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == orderId && x.CustomerId == customerId, ct) ?? throw NF("Không tìm thấy đơn");
        if (order.OrderStatus is "Cancelled" or "Expired") return;
        if (order.PaymentStatus != "Unpaid") throw Conflict("Đơn đã có khoản thanh toán; cần quy trình hoàn tiền trước khi hủy");
        if (order.OrderStatus is "Delivering" or "Completed") throw Conflict("Đơn đã giao, không thể hủy");
        if (await db.Deliveries.AnyAsync(x => x.OrderId == orderId && x.DeliveryStatus != "Cancelled" && x.DeliveryStatus != "Returned", ct)) throw Conflict("Đơn có giao hàng đang xử lý, không thể hủy");
        if (!await IsCustomOrderAsync(order.Id, ct)) await ReleaseReservedAsync(order.Items, ct); await RestoreVoucherAsync(order, ct); order.OrderStatus = "Cancelled"; await SetCustomStatusAsync(order.Id, "Cancelled", ct); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
    }

    /// <summary>Expires unpaid holds atomically. Status is the idempotency marker: a second run cannot release stock twice.</summary>
    public async Task<int> ExpireUnpaidOrdersAsync(DateTime? nowUtc = null, CancellationToken ct = default)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var orders = await db.Orders.Include(x => x.Items)
            .Where(x => x.PaymentStatus == "Unpaid" && x.OrderStatus == "Confirmed" && x.CreatedAt <= now.Subtract(UnpaidOrderLifetime))
            .OrderBy(x => x.CreatedAt).Take(500).ToListAsync(ct);
        foreach (var order in orders)
        {
            if (!await IsCustomOrderAsync(order.Id, ct)) await ReleaseReservedAsync(order.Items, ct);
            await RestoreVoucherAsync(order, ct);
            order.OrderStatus = "Expired";
            await SetCustomStatusAsync(order.Id, "Expired", ct);
        }
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return orders.Count;
    }

    public async Task<IReadOnlyList<OrderDto>> StaffOrdersAsync(CancellationToken ct = default)
    {
        var orders = await db.Orders.Include(x => x.Items).OrderByDescending(x => x.CreatedAt).Take(200).ToListAsync(ct);
        var shipped = await ShippedQuantitiesAsync(orders.SelectMany(x => x.Items).Select(x => x.Id), ct); return orders.Select(x => Map(x, shipped)).ToList();
    }

    public async Task TransitionAsync(Guid orderId, string status, CancellationToken ct = default)
    {
        status = status?.Trim() ?? ""; await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var order = await db.Orders.Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == orderId, ct) ?? throw NF("Không tìm thấy đơn");
        if (order.OrderStatus == status) { await transaction.RollbackAsync(ct); return; }
        if (status == "Cancelled") { if (order.PaymentStatus != "Unpaid" || order.OrderStatus is not ("Confirmed" or "Processing" or "Delivering")) throw Conflict("Chuyển trạng thái không hợp lệ"); if (await db.Deliveries.AnyAsync(x => x.OrderId == orderId && x.DeliveryStatus != "Cancelled" && x.DeliveryStatus != "Returned", ct)) throw Conflict("Đơn còn phiếu giao đang xử lý, không thể hủy"); if (!await IsCustomOrderAsync(order.Id, ct)) await ReleaseReservedAsync(order.Items, ct); await RestoreVoucherAsync(order, ct); }
        else if ((order.OrderStatus, status) is not ("Confirmed", "Processing") and not ("Processing", "Delivering") and not ("Delivering", "Completed")) throw Conflict("Chuyển trạng thái không hợp lệ");
        if (status == "Processing" && await IsCustomOrderAsync(order.Id, ct))
        {
            var custom = await db.Set<CustomOrderCheckout>().SingleAsync(x => x.OrderId == order.Id, ct);
            var quote = await db.Set<CustomQuotation>().SingleAsync(x => x.Id == custom.CustomQuotationId, ct);
            var request = await db.Set<CustomOrderRequest>().SingleAsync(x => x.Id == quote.CustomOrderRequestId, ct);
            if (request.ProductionStartedAt is null) throw Conflict("Đơn đặt riêng phải bắt đầu sản xuất qua quy trình kiểm tra cọc");
        }
        if (status == "Completed")
        {
            if (order.PaymentStatus != "Paid") throw Conflict("Chỉ hoàn tất đơn đã thanh toán");
            var shipped = await DeliveredQuantitiesAsync(order.Items.Select(i => i.Id), ct);
            if (order.Items.Any(i => !shipped.TryGetValue(i.Id, out var quantity) || quantity < i.Quantity)) throw Conflict("Chưa giao đủ tất cả sản phẩm");
            foreach (var group in (await IsCustomOrderAsync(order.Id, ct) ? Enumerable.Empty<OrderItem>() : order.Items).GroupBy(i => i.ProductId))
            {
                var quantity = group.Sum(i => i.Quantity);
                var inventoryToken = Guid.NewGuid();
                var affected = await db.ExecuteUpdateAuditedAsync(
                    db.Inventories.Where(i => i.ProductId == group.Key && i.QuantityReserved >= quantity),
                    x => x.SetProperty(i => i.QuantityReserved, i => i.QuantityReserved - quantity)
                        .SetProperty(i => i.LastUpdated, DateTime.UtcNow)
                        .SetProperty(i => i.ConcurrencyToken, inventoryToken), ct);
                if (affected != 1) throw Conflict("Không thể trừ tồn kho đã giữ");
            }
        }
        order.OrderStatus = status;
        if (status is "Cancelled" or "Completed") await SetCustomStatusAsync(order.Id, status, ct);
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
    }

    public async Task<DeliveryDto> DeliveryAsync(Guid orderId, DeliveryRequest request, CancellationToken ct = default)
    {
        if (request is null || request.Items is null || request.Items.Count == 0) throw Bad("Delivery trống");
        if (request.Items.Any(x => x is null || x.Quantity <= 0)) throw Bad("Số lượng giao phải lớn hơn 0");
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var order = await db.Orders.Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == orderId, ct) ?? throw NF("Không tìm thấy đơn");
        if (order.OrderStatus is not ("Processing" or "Delivering")) throw Conflict("Chỉ tạo giao hàng cho đơn Processing hoặc Delivering");
        var grouped = new List<DeliveryLineRequest>();
        foreach (var group in request.Items.GroupBy(x => x.OrderItemId))
        {
            var quantity = group.Sum(x => (long)x.Quantity);
            if (quantity > int.MaxValue) throw Bad("Tổng số lượng giao vượt giới hạn");
            grouped.Add(new DeliveryLineRequest { OrderItemId = group.Key, Quantity = (int)quantity });
        }
        var delivery = new Delivery { Id = Guid.NewGuid(), OrderId = orderId, DeliveryCode = $"DEL-{Guid.NewGuid():N}", ShipperUnit = request.ShipperUnit ?? "", TrackingNumber = request.TrackingNumber ?? "", DeliveryStatus = "Preparing" };
        foreach (var requested in grouped) { var line = order.Items.SingleOrDefault(i => i.Id == requested.OrderItemId) ?? throw Bad("OrderItem không thuộc đơn"); var used = await db.DeliveryItems.Where(y => y.OrderItemId == line.Id).Where(y => db.Deliveries.Any(d => d.Id == y.DeliveryId && d.DeliveryStatus != "Cancelled" && d.DeliveryStatus != "Returned")).SumAsync(y => (int?)y.ShippedQuantity, ct) ?? 0; if (requested.Quantity > line.Quantity - used) throw Conflict("Vượt quá lượng chưa giao"); delivery.Items.Add(new DeliveryItem { Id = Guid.NewGuid(), DeliveryId = delivery.Id, OrderItemId = line.Id, ShippedQuantity = requested.Quantity }); }
        if (string.IsNullOrWhiteSpace(delivery.TrackingNumber) && carrier is not null and not DisabledCarrier)
        {
            try
            {
                var recName = "Khách hàng"; var recPhone = "0900000000"; var recAddress = order.ShippingAddress;
                if (order.ShippingAddress.Contains("ReceiverName:") && order.ShippingAddress.Contains("Address:"))
                {
                    foreach (var p in order.ShippingAddress.Split(';', StringSplitOptions.TrimEntries))
                    {
                        if (p.StartsWith("ReceiverName:", StringComparison.OrdinalIgnoreCase)) recName = p["ReceiverName:".Length..].Trim();
                        else if (p.StartsWith("ReceiverPhone:", StringComparison.OrdinalIgnoreCase)) recPhone = p["ReceiverPhone:".Length..].Trim();
                        else if (p.StartsWith("Address:", StringComparison.OrdinalIgnoreCase)) recAddress = p["Address:".Length..].Trim();
                    }
                }
                var itemIds = grouped.Select(x => x.OrderItemId).ToList();
                var orderItems = order.Items.Where(i => itemIds.Contains(i.Id)).ToList();
                var productIds = orderItems.Select(i => i.ProductId).Distinct().ToList();
                var products = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
                var shipmentItems = new List<CarrierShipmentItem>();
                var totalGrams = 0;
                var maxLengthCm = 0;
                var maxWidthCm = 0;
                var totalHeightCm = 0;
                foreach (var req in grouped)
                {
                    var oi = orderItems.FirstOrDefault(x => x.Id == req.OrderItemId);
                    if (oi is not null && products.TryGetValue(oi.ProductId, out var prod))
                    {
                        var itemWeight = (prod.ShippingWeightGrams ?? 500) * req.Quantity;
                        totalGrams += itemWeight;
                        // Same packaging model as ShippingQuoteService: largest footprint, stacked heights.
                        maxLengthCm = Math.Max(maxLengthCm, (int)Math.Ceiling(prod.PackageLengthCm ?? 10m));
                        maxWidthCm = Math.Max(maxWidthCm, (int)Math.Ceiling(prod.PackageWidthCm ?? 10m));
                        totalHeightCm += (int)Math.Ceiling((prod.PackageHeightCm ?? 5m) * req.Quantity);
                        shipmentItems.Add(new CarrierShipmentItem(prod.Name, prod.Code, req.Quantity, oi.UnitPrice, prod.ShippingWeightGrams ?? 500, (int?)prod.PackageLengthCm, (int?)prod.PackageWidthCm, (int?)prod.PackageHeightCm));
                    }
                }
                var shipWeightKg = Math.Max(0.1m, totalGrams / 1000m);
                var codAmount = order.PaymentStatus == "Paid" ? 0m : delivery.Items.Sum(x => (orderItems.FirstOrDefault(i => i.Id == x.OrderItemId)?.FinalPrice ?? 0m) * x.ShippedQuantity);
                var shipRes = await carrier.CreateShipmentAsync(new CarrierShipmentRequest(order.OrderCode, delivery.DeliveryCode, recName, recPhone, recAddress, null, null, shipWeightKg, Math.Max(1, maxLengthCm), Math.Max(1, maxWidthCm), Math.Max(1, totalHeightCm), codAmount, "Tiệm Hoa Cô Gu - Giao hoa cẩn thận", shipmentItems, order.ShippingWardName, order.ShippingProvinceName), ct);
                if (!shipRes.Accepted || string.IsNullOrWhiteSpace(shipRes.TrackingNumber))
                    throw new BusinessException(502, shipRes.ErrorMessage ?? "GHN không trả về mã vận đơn; chưa tạo phiếu giao.");
                delivery.TrackingNumber = shipRes.TrackingNumber;
                if (string.IsNullOrWhiteSpace(delivery.ShipperUnit)) delivery.ShipperUnit = "GHN";
                delivery.ShippingCost = shipRes.ShippingFee;
            }
            catch (BusinessException) { throw; }
            catch (Exception ex) { throw new BusinessException(502, $"Không tạo được vận đơn GHN: {ex.Message}"); }
        }
        db.Deliveries.Add(delivery); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return ToDto(delivery);
    }

    public async Task<DeliveryDto> TransitionDeliveryAsync(Guid orderId, Guid deliveryId, string status, CancellationToken ct = default)
    {
        status = status?.Trim() ?? ""; await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct); var delivery = await db.Deliveries.Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == deliveryId && x.OrderId == orderId, ct) ?? throw NF("Không tìm thấy giao hàng");
        if (delivery.DeliveryStatus == status) { await tx.RollbackAsync(ct); return ToDto(delivery); }
        if ((delivery.DeliveryStatus, status) is not ("Preparing", "Shipped") and not ("Shipped", "Delivered") and not ("Shipped", "Failed") and not ("Failed", "Shipped") and not ("Shipped", "Returned") and not ("Failed", "Returned") and not ("Preparing", "Cancelled")) throw Conflict("Chuyển trạng thái giao hàng không hợp lệ");
        delivery.DeliveryStatus = status; if (status == "Shipped") delivery.ShippedAt ??= DateTime.UtcNow; if (status == "Delivered") delivery.DeliveredAt ??= DateTime.UtcNow;
        if (status == "Cancelled" && !string.IsNullOrWhiteSpace(delivery.TrackingNumber) && carrier is not null and not DisabledCarrier)
        {
            try { await carrier.CancelShipmentAsync(delivery.TrackingNumber, ct); } catch { /* Non-blocking remote cancel */ }
        }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return ToDto(delivery);
    }

    public async Task<CarrierTrackingResult> TrackDeliveryAsync(Guid orderId, Guid deliveryId, Guid? customerId, bool staff, CancellationToken ct = default)
    {
        var delivery = await db.Deliveries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == deliveryId && x.OrderId == orderId, ct) ?? throw NF("Không tìm thấy giao hàng");
        if (!staff && !await db.Orders.AnyAsync(x => x.Id == orderId && x.CustomerId == customerId, ct)) throw NF("Không tìm thấy giao hàng");
        if (string.IsNullOrWhiteSpace(delivery.TrackingNumber) || carrier is null or DisabledCarrier)
            return new CarrierTrackingResult(true, delivery.TrackingNumber, delivery.DeliveryStatus, "Local tracking", DateTime.UtcNow, null);
        return await carrier.TrackShipmentAsync(delivery.TrackingNumber, ct);
    }

    async Task SetCustomStatusAsync(Guid orderId, string status, CancellationToken ct)
    {
        var link = await db.Set<CustomOrderCheckout>().SingleOrDefaultAsync(x => x.OrderId == orderId, ct);
        if (link is null) return;
        var quote = await db.Set<CustomQuotation>().SingleAsync(x => x.Id == link.CustomQuotationId, ct);
        var request = await db.Set<CustomOrderRequest>().SingleAsync(x => x.Id == quote.CustomOrderRequestId, ct);
        quote.Status = request.Status = status;
        quote.UpdatedAt = request.UpdatedAt = DateTime.UtcNow;
        if (status == "Completed") request.CompletedAt ??= DateTime.UtcNow;
    }

    Task<bool> IsCustomOrderAsync(Guid orderId, CancellationToken ct) => db.Set<CustomOrderCheckout>().AnyAsync(x => x.OrderId == orderId, ct);

    async Task ReleaseReservedAsync(IEnumerable<OrderItem> items, CancellationToken ct)
    {
        foreach (var group in items.GroupBy(x => x.ProductId))
        {
            var quantity = group.Sum(x => x.Quantity);
            var inventoryToken = Guid.NewGuid();
            var affected = await db.ExecuteUpdateAuditedAsync(
                db.Inventories.Where(x => x.ProductId == group.Key && x.QuantityReserved >= quantity),
                x => x.SetProperty(i => i.QuantityAvailable, i => i.QuantityAvailable + quantity)
                    .SetProperty(i => i.QuantityReserved, i => i.QuantityReserved - quantity)
                    .SetProperty(i => i.LastUpdated, DateTime.UtcNow)
                    .SetProperty(i => i.ConcurrencyToken, inventoryToken), ct);
            if (affected != 1) throw Conflict("Không thể giải phóng tồn kho");
        }
    }
    async Task RestoreVoucherAsync(Order order, CancellationToken ct)
    {
        if (order.VoucherId is not { } voucherId) return;
        // The order status transition is the once-only boundary; this guarded update also prevents negatives.
        if (await db.ExecuteUpdateAuditedAsync(db.Vouchers.Where(v => v.Id == voucherId && v.UsedCount > 0), x => x.SetProperty(v => v.UsedCount, v => v.UsedCount - 1), ct) != 1)
            throw Conflict("Không thể hoàn lại lượt sử dụng voucher");
        // Keep VoucherId for immutable commercial history; the terminal status is the restoration marker.
    }
    async Task<Dictionary<Guid, int>> ShippedQuantitiesAsync(IEnumerable<Guid> ids, CancellationToken ct) => await db.DeliveryItems.Where(x => ids.Contains(x.OrderItemId)).Where(x => db.Deliveries.Any(d => d.Id == x.DeliveryId && d.DeliveryStatus != "Cancelled" && d.DeliveryStatus != "Returned")).GroupBy(x => x.OrderItemId).Select(g => new { g.Key, Quantity = g.Sum(x => x.ShippedQuantity) }).ToDictionaryAsync(x => x.Key, x => x.Quantity, ct);
    async Task<Dictionary<Guid, int>> DeliveredQuantitiesAsync(IEnumerable<Guid> ids, CancellationToken ct) => await db.DeliveryItems.Where(x => ids.Contains(x.OrderItemId)).Where(x => db.Deliveries.Any(d => d.Id == x.DeliveryId && d.DeliveryStatus == "Delivered")).GroupBy(x => x.OrderItemId).Select(g => new { g.Key, Quantity = g.Sum(x => x.ShippedQuantity) }).ToDictionaryAsync(x => x.Key, x => x.Quantity, ct);
    static string Fingerprint(Guid? customerId, CheckoutRequest r) { var canonical = new { customerId, r.ShippingAddress, r.ReceiverName, r.ReceiverPhone, r.ReceiverEmail, r.Note, r.VoucherCode, r.ShippingQuoteId, Items = (r.Items ?? []).Select(x => new { x.ProductId, x.Quantity, x.CustomNote, x.ReferenceImageUrl }).ToList() }; return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical)))); }
    static OrderDto Map(Order order, IReadOnlyDictionary<Guid, int>? shipped = null) => new(order.Id, order.OrderCode, order.SubTotal, order.DiscountAmount, order.ShippingFee, order.TotalAmount, order.OrderStatus, order.PaymentStatus, order.ShippingAddress, order.CreatedAt, order.Items.Select(i => new OrderLineDto(i.Id, i.ProductId, i.Quantity, i.UnitPrice, i.FinalPrice, shipped is not null && shipped.TryGetValue(i.Id, out var quantity) ? quantity : 0, i.CustomNote, i.ReferenceImageUrl)).ToList());
    static DeliveryDto ToDto(Delivery d) => new(d.Id, d.DeliveryCode, d.ShipperUnit, d.TrackingNumber, d.DeliveryStatus, d.Items.Select(x => new DeliveryLineDto(x.OrderItemId, x.ShippedQuantity)).ToList(), d.ShippingCost);

    sealed record Line(Guid ProductId, int Quantity, string? Note, string? Image);
}
