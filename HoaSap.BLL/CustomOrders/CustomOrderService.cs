using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Text.RegularExpressions;
using HoaSap.DAL;
using HoaSap.BLL.Commerce;
using HoaSap.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HoaSap.BLL.CustomOrders;

public sealed record CustomRequestDto(Guid Id, Guid ProductId, int Quantity, string Description, string ReferenceImageUrl, DateTime? RequestedCompletionDate, string Status, DateTime CreatedAt,
    string ProductName, DateTime UpdatedAt, int QuoteVersion, Guid? OrderId, string? OrderCode, bool DepositReady, bool FullyPaid, bool DeliveryComplete,
    int? DeliveryProvinceId = null, string? DeliveryProvinceName = null, int? DeliveryWardId = null, string? DeliveryWardName = null, BouquetSnapshot? Bouquet = null);
public sealed record CustomQuoteRevisionDto(int Version, decimal? UnitPrice, decimal? TotalAmount, decimal? DepositAmount, decimal PaidAmount, decimal BalanceAmount, DateTime CompletionDate, DateTime ExpiresAt, string AdminNote, DateTime CreatedAt, string Status, BouquetSnapshot? Bouquet);
public sealed record CustomQuoteDto(Guid Id, Guid RequestId, int CurrentRevision, string Status, DateTime? ExpiresAt, IReadOnlyList<CustomQuoteRevisionDto> Revisions, Guid? OrderId, decimal PaidAmount, decimal BalanceAmount);
public sealed class CustomRequestInput
{
    public Guid ProductId { get; set; }
    [Range(1, 1000)] public int Quantity { get; set; } = 1;
    [Required, MaxLength(4000)] public string Description { get; set; } = "";
    [Url, MaxLength(2000)] public string? ReferenceImageUrl { get; set; }
    public DateTime? RequestedCompletionDate { get; set; }
    public int? QuoteVersion { get; set; }
    public BouquetInput? Bouquet { get; set; }
    /// <summary>Khu vực giao hoa theo danh mục GHN 2 cấp; bỏ trống nếu khách chưa muốn chọn.</summary>
    [Range(1, int.MaxValue)] public int? ProvinceId { get; set; }
    [Range(1, int.MaxValue)] public int? WardId { get; set; }
}
public sealed class CustomQuoteInput
{
    public DateTime? RequestUpdatedAt { get; set; }
    [Range(0, 1000000000000d)] public decimal UnitPrice { get; set; }
    [Range(0, 100)] public decimal DepositPercent { get; set; } = 50;
    public DateTime CompletionDate { get; set; }
    [Required, MaxLength(2000)] public string AdminNote { get; set; } = "";
    public DateTime? ExpiresAt { get; set; }
}
public sealed class CustomApproveInput { [Range(1, int.MaxValue)] public int Version { get; set; } }
public sealed class CustomCheckoutInput
{
    public int? QuoteVersion { get; set; }
    [Required, MaxLength(2000)] public string ShippingAddress { get; set; } = "";
    [Range(1, int.MaxValue)] public int? ProvinceId { get; set; }
    [Range(1, int.MaxValue)] public int? WardId { get; set; }
    [MaxLength(100)] public string? ReceiverName { get; set; }
    [MaxLength(20)] public string? ReceiverPhone { get; set; }
    [MaxLength(1000)] public string? Note { get; set; }
    [Required, MaxLength(100)] public string IdempotencyKey { get; set; } = "";
}
public sealed record CustomCheckoutDto(Guid OrderId, string OrderCode, int QuoteVersion, decimal TotalAmount, decimal DepositAmount, decimal PaidAmount, decimal BalanceAmount, string PaymentStatus);
public sealed record CustomLifecycleDto(Guid RequestId, Guid OrderId, string Status, DateTime? ScheduledCompletionDate, DateTime? ProductionStartedAt, DateTime? CompletedAt, decimal RequiredDeposit, decimal PaidAmount, decimal BalanceAmount, string DeliveryStatus);

/// <summary>Made-to-order quotations do not reserve stock from the ready-made catalog.</summary>
public sealed class CustomOrderService(HoaSapDbContext db, IGhnStagingAddressCatalog? addressCatalog = null, IOptions<BouquetCatalog>? catalog = null)
{
    private static readonly Regex PhonePattern = new(@"^[+0-9 ()\-]{9,20}$", RegexOptions.CultureInvariant);
    private static BusinessException Error(int code, string message) => new(code, message);
    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();

    public async Task<BouquetCatalog> GetBouquetCatalogAsync(CancellationToken ct)
    {
        var options = catalog?.Value;
        if (options?.IsConfigured != true || options.ProductId == Guid.Empty ||
            !await db.Products.AnyAsync(product => product.Id == options.ProductId && product.IsActive && product.IsCustomizable, ct))
            throw Error(503, "Tiệm chưa mở bộ phối hoa.");
        return options;
    }

    private async Task<string> SnapshotAsync(CustomRequestInput input, CancellationToken ct)
    {
        var options = await GetBouquetCatalogAsync(ct);
        if (input.ProductId != options.ProductId) throw Error(400, "Sản phẩm nền không hỗ trợ bộ phối hoa.");
        return options.Snapshot(input.Bouquet!);
    }

    public async Task<CustomRequestDto> CreateAsync(Guid customerId, CustomRequestInput input, CancellationToken ct = default)
    {
        var area = await ValidateRequestAsync(input, ct);
        var bouquet = input.Bouquet is null ? null : await SnapshotAsync(input, ct);
        if (bouquet is not null && area.IsEmpty) throw Error(400, "Chọn tỉnh và phường nhận hoa.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var description = input.Description.Trim();
        var image = input.ReferenceImageUrl?.Trim() ?? "";
        var date = input.RequestedCompletionDate is { } value ? Utc(value) : (DateTime?)null;
        var since = DateTime.UtcNow.AddMinutes(-10);
        var existing = await db.Set<CustomOrderRequest>().AsNoTracking()
            .Where(x => x.CustomerId == customerId && x.ProductId == input.ProductId && x.Quantity == input.Quantity && x.Description == description &&
                        x.ReferenceImageUrl == image && x.RequestedCompletionDate == date && x.Status == "Requested" && x.CreatedAt >= since &&
                        x.DeliveryProvinceId == area.ProvinceId && x.DeliveryWardId == area.WardId && x.BouquetSnapshot == bouquet)
            .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        if (existing is not null) { await tx.CommitAsync(ct); return (await MapAsync([existing], ct))[0]; }
        var request = new CustomOrderRequest { CustomerId = customerId };
        Apply(request, input);
        ApplyDeliveryArea(request, area);
        request.BouquetSnapshot = bouquet;
        db.Add(request);
        db.Add(new CustomQuotation { CustomOrderRequestId = request.Id });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (await MapAsync([request], ct))[0];
    }

    public async Task<IReadOnlyList<CustomRequestDto>> MineAsync(Guid customerId, CancellationToken ct = default) =>
        await MapAsync(await db.Set<CustomOrderRequest>().AsNoTracking().Where(x => x.CustomerId == customerId).OrderByDescending(x => x.CreatedAt).Take(200).ToListAsync(ct), ct);

    public async Task<IReadOnlyList<CustomRequestDto>> StaffRequestsAsync(bool hideMoney, CancellationToken ct = default) =>
        await MapAsync(await db.Set<CustomOrderRequest>().AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(200).ToListAsync(ct), ct, hideMoney);

    public async Task<CustomRequestDto> ReviseRequestAsync(Guid id, Guid customerId, CustomRequestInput input, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var request = await OwnedAsync(id, customerId, ct);
        var quote = await db.Set<CustomQuotation>().SingleAsync(x => x.CustomOrderRequestId == id, ct);
        if (await db.Set<CustomOrderCheckout>().AnyAsync(x => x.CustomQuotationId == quote.Id, ct) || request.Status is not ("Requested" or "Quoted" or "Approved"))
            throw Error(409, "Yêu cầu đã tạo đơn hoặc không thể sửa ở trạng thái hiện tại.");
        if (input.QuoteVersion is { } version && version != quote.CurrentRevision) throw Error(409, "Báo giá vừa thay đổi, vui lòng tải lại trước khi sửa.");
        if (request.BouquetSnapshot is not null && input.Bouquet is null && input.ProductId != request.ProductId)
            throw Error(400, "Cần gửi lại cấu hình bó hoa khi đổi sản phẩm nền.");
        var area = await ValidateRequestAsync(input, ct);
        if ((request.BouquetSnapshot is not null || input.Bouquet is not null) && area.IsEmpty && request.DeliveryProvinceId is null) throw Error(400, "Chọn tỉnh và phường nhận hoa.");
        var wasQuoted = request.Status is "Quoted" or "Approved";
        Apply(request, input);
        if (!area.IsEmpty) ApplyDeliveryArea(request, area);
        if (input.Bouquet is not null) request.BouquetSnapshot = await SnapshotAsync(input, ct);
        if (wasQuoted)
        {
            var current = await db.Set<CustomQuotationRevision>().SingleOrDefaultAsync(x => x.CustomQuotationId == quote.Id && x.Version == quote.CurrentRevision, ct);
            if (current is not null && current.Status is "Sent" or "Approved") current.Status = "Superseded";
            quote.Status = request.Status = "Requested";
            quote.ExpiresAt = null;
            quote.UpdatedAt = request.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (await MapAsync([request], ct))[0];
    }

    public async Task<CustomRequestDto> CancelRequestAsync(Guid id, Guid customerId, bool admin, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var request = await db.Set<CustomOrderRequest>().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw Error(404, "Không tìm thấy yêu cầu.");
        if (!admin && request.CustomerId != customerId) throw Error(403, "Bạn không có quyền với yêu cầu này.");
        var quote = await db.Set<CustomQuotation>().SingleAsync(x => x.CustomOrderRequestId == id, ct);
        if (request.Status == "Cancelled") { await tx.CommitAsync(ct); return (await MapAsync([request], ct))[0]; }
        if (await db.Set<CustomOrderCheckout>().AnyAsync(x => x.CustomQuotationId == quote.Id, ct))
            throw Error(409, "Yêu cầu đã tạo đơn; hãy hủy đơn hàng (chỉ khi chưa thanh toán).");
        if (request.Status is not ("Requested" or "Quoted" or "Approved")) throw Error(409, "Yêu cầu không thể hủy ở trạng thái hiện tại.");
        quote.Status = request.Status = "Cancelled";
        quote.UpdatedAt = request.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (await MapAsync([request], ct))[0];
    }

    public async Task<CustomQuoteDto> QuoteAsync(Guid id, Guid? customerId, bool staff, bool admin, CancellationToken ct = default)
    {
        var request = await db.Set<CustomOrderRequest>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw Error(404, "Không tìm thấy yêu cầu.");
        if (!staff && request.CustomerId != customerId) throw Error(403, "Bạn không có quyền xem yêu cầu này.");
        var quote = await db.Set<CustomQuotation>().AsNoTracking().SingleAsync(x => x.CustomOrderRequestId == id, ct);
        var revisions = await db.Set<CustomQuotationRevision>().AsNoTracking().Where(x => x.CustomQuotationId == quote.Id).OrderBy(x => x.Version).ToListAsync(ct);
        var checkout = await db.Set<CustomOrderCheckout>().AsNoTracking().SingleOrDefaultAsync(x => x.CustomQuotationId == quote.Id, ct);
        var paid = checkout is null ? 0 : await PaidAsync(checkout.OrderId, ct);
        var total = revisions.LastOrDefault()?.TotalAmount ?? 0;
        var hideMoney = staff && !admin;
        return new(quote.Id, id, quote.CurrentRevision, quote.Status, quote.ExpiresAt,
            revisions.Select(x => new CustomQuoteRevisionDto(x.Version, hideMoney ? null : x.UnitPrice, hideMoney ? null : x.TotalAmount, hideMoney ? null : x.DepositAmount,
                hideMoney ? 0 : paid, hideMoney ? 0 : Math.Max(0, x.TotalAmount - paid), x.CompletionDate, x.ExpiresAt, hideMoney ? "" : x.AdminNote, x.CreatedAt, x.Status, BouquetCatalog.Read(x.BouquetSnapshot, hideMoney))).ToList(),
            checkout?.OrderId, hideMoney ? 0 : paid, hideMoney ? 0 : Math.Max(0, total - paid));
    }

    public async Task<CustomQuoteDto> ReviseAsync(Guid requestId, Guid adminId, CustomQuoteInput input, CancellationToken ct = default)
    {
        if (input is null || input.UnitPrice < 0 || input.UnitPrice > 1_000_000_000_000m || decimal.Round(input.UnitPrice, 2) != input.UnitPrice || input.DepositPercent is < 0 or > 100 || string.IsNullOrWhiteSpace(input.AdminNote))
            throw Error(400, "Báo giá chưa hợp lệ.");
        var now = DateTime.UtcNow;
        var completion = Utc(input.CompletionDate);
        var expiry = Utc(input.ExpiresAt ?? (now.AddDays(7) < completion ? now.AddDays(7) : completion));
        if (completion <= now || expiry <= now || expiry > completion) throw Error(400, "Thời hạn báo giá phải trước ngày hoàn thành và ở tương lai.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var request = await db.Set<CustomOrderRequest>().SingleOrDefaultAsync(x => x.Id == requestId, ct) ?? throw Error(404, "Không tìm thấy yêu cầu.");
        if (request.BouquetSnapshot is not null && input.RequestUpdatedAt != request.UpdatedAt)
            throw Error(409, "Yêu cầu bó hoa đã thay đổi. Tải lại trước khi phát hành báo giá.");
        var quote = await db.Set<CustomQuotation>().SingleAsync(x => x.CustomOrderRequestId == requestId, ct);
        if (await db.Set<CustomOrderCheckout>().AnyAsync(x => x.CustomQuotationId == quote.Id, ct) || quote.Status is "Cancelled" or "Completed")
            throw Error(409, "Báo giá đã chốt, không thể thay đổi giá của đơn đã tạo.");
        var total = decimal.Round(input.UnitPrice * request.Quantity, 2);
        var revision = new CustomQuotationRevision
        {
            CustomQuotationId = quote.Id, Version = quote.CurrentRevision + 1, UnitPrice = input.UnitPrice, Quantity = request.Quantity,
            TotalAmount = total, DepositAmount = Math.Min(total, decimal.Round(total * input.DepositPercent / 100m, 0, MidpointRounding.AwayFromZero)),
            CompletionDate = completion, ExpiresAt = expiry, CreatedBy = adminId, AdminNote = input.AdminNote.Trim(), BouquetSnapshot = request.BouquetSnapshot
        };
        db.Add(revision);
        quote.CurrentRevision = revision.Version;
        quote.ExpiresAt = expiry;
        quote.Status = request.Status = "Quoted";
        quote.UpdatedAt = request.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await QuoteAsync(requestId, request.CustomerId, false, true, ct);
    }

    public async Task<CustomQuoteDto> ApproveAsync(Guid id, Guid customerId, CustomApproveInput input, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var request = await OwnedAsync(id, customerId, ct);
        var quote = await db.Set<CustomQuotation>().SingleAsync(x => x.CustomOrderRequestId == id, ct);
        var revision = await db.Set<CustomQuotationRevision>().SingleOrDefaultAsync(x => x.CustomQuotationId == quote.Id && x.Version == input.Version, ct) ?? throw Error(409, "Phiên bản báo giá không tồn tại.");
        if (quote.Status == "Approved" && quote.CurrentRevision == input.Version && revision.Status == "Approved")
        {
            await tx.CommitAsync(ct);
            return await QuoteAsync(id, customerId, false, true, ct);
        }
        if (quote.Status != "Quoted" || quote.CurrentRevision != input.Version || revision.Status != "Sent" || revision.ExpiresAt <= DateTime.UtcNow)
            throw Error(409, "Báo giá đã cũ hoặc hết hạn.");
        quote.Status = request.Status = revision.Status = "Approved";
        quote.UpdatedAt = request.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await QuoteAsync(id, customerId, false, true, ct);
    }

    public async Task<CustomCheckoutDto> CheckoutAsync(Guid id, Guid customerId, CustomCheckoutInput input, CancellationToken ct = default)
    {
        if (input is null || string.IsNullOrWhiteSpace(input.IdempotencyKey) || input.IdempotencyKey.Length > 100 || string.IsNullOrWhiteSpace(input.ShippingAddress)) throw Error(400, "Thiếu địa chỉ hoặc mã idempotency.");
        if (!string.IsNullOrWhiteSpace(input.ReceiverPhone) && !PhonePattern.IsMatch(input.ReceiverPhone.Trim())) throw Error(400, "Số điện thoại người nhận không hợp lệ.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var request = await OwnedAsync(id, customerId, ct);
        var quote = await db.Set<CustomQuotation>().SingleAsync(x => x.CustomOrderRequestId == id, ct);
        if (request.BouquetSnapshot is not null)
        {
            if (input.QuoteVersion is null || input.QuoteVersion != quote.CurrentRevision)
                throw Error(409, "Báo giá đã thay đổi, vui lòng tải lại trước khi tạo đơn.");
            if (input.ProvinceId != request.DeliveryProvinceId || input.WardId != request.DeliveryWardId)
                throw Error(400, "Khu vực giao phải khớp báo giá. Vui lòng yêu cầu báo giá lại nếu đổi khu vực.");
        }
        var customer = await db.Customers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == customerId, ct);
        var receiverName = string.IsNullOrWhiteSpace(input.ReceiverName) ? customer?.FullName ?? "" : input.ReceiverName.Trim();
        var receiverPhone = string.IsNullOrWhiteSpace(input.ReceiverPhone) ? customer?.PhoneNumber ?? "" : input.ReceiverPhone.Trim();
        var streetAddress = input.ShippingAddress.Trim();
        int? provinceId = null; int? wardId = null; string? provinceName = null; string? wardName = null;
        if (request.BouquetSnapshot is not null)
        {
            provinceId = request.DeliveryProvinceId; provinceName = request.DeliveryProvinceName;
            wardId = request.DeliveryWardId; wardName = request.DeliveryWardName;
        }
        else if (input.ProvinceId is not null || input.WardId is not null)
        {
            if (input.ProvinceId is not > 0 || input.WardId is not > 0) throw Error(400, "Chọn tỉnh và phường GHN hợp lệ.");
            if (addressCatalog is null) throw Error(503, "Danh mục địa chỉ GHN chưa sẵn sàng.");
            var province = (await addressCatalog.GetProvincesAsync(ct)).SingleOrDefault(x => x.Id == input.ProvinceId);
            if (province is null) throw Error(400, "Tỉnh không thuộc danh mục GHN hiện tại.");
            var ward = (await addressCatalog.GetWardsAsync(province.Id, ct)).SingleOrDefault(x => x.Id == input.WardId && x.ParentId == province.Id);
            if (ward is null) throw Error(400, "Phường không thuộc tỉnh đã chọn trong danh mục GHN.");
            provinceId = province.Id; provinceName = province.Name; wardId = ward.Id; wardName = ward.Name;
        }
        var fullAddress = wardName is null ? streetAddress : $"{streetAddress}, {wardName}, {provinceName}";
        var address = $"ReceiverName: {receiverName}; ReceiverPhone: {receiverPhone}; ReceiverEmail: {customer?.Email ?? ""}; Address: {fullAddress}";
        var key = input.IdempotencyKey.Trim();
        var old = await db.Set<CustomOrderCheckout>().SingleOrDefaultAsync(x => x.CustomerId == customerId && x.IdempotencyKey == key, ct);
        if (old is not null)
        {
            var order = await db.Orders.SingleAsync(x => x.Id == old.OrderId, ct);
            if (old.CustomQuotationId != quote.Id || (input.QuoteVersion is { } version && old.QuotationVersion != version) || order.ShippingAddress != address || order.Note != (input.Note?.Trim() ?? ""))
                throw Error(409, "Mã idempotency đã dùng cho nội dung khác.");
            var oldRevision = await db.Set<CustomQuotationRevision>().SingleAsync(x => x.CustomQuotationId == quote.Id && x.Version == old.QuotationVersion, ct);
            await tx.CommitAsync(ct);
            return await CheckoutDto(order, oldRevision, ct);
        }
        if (await db.Set<CustomOrderCheckout>().AnyAsync(x => x.CustomQuotationId == quote.Id, ct)) throw Error(409, "Báo giá này đã có đơn hàng.");
        var revision = await db.Set<CustomQuotationRevision>().SingleOrDefaultAsync(x => x.CustomQuotationId == quote.Id && x.Version == quote.CurrentRevision, ct) ?? throw Error(409, "Chưa có báo giá.");
        if (input.QuoteVersion is { } expectedVersion && revision.Version != expectedVersion) throw Error(409, "Báo giá đã thay đổi, vui lòng tải lại trước khi tạo đơn.");
        if (quote.Status != "Approved" || revision.Status != "Approved" || revision.ExpiresAt <= DateTime.UtcNow) throw Error(409, "Báo giá chưa được duyệt hoặc đã hết hạn.");
        var created = new Order
        {
            CustomerId = customerId, OrderCode = $"CUS-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid():N}", SubTotal = revision.TotalAmount, TotalAmount = revision.TotalAmount,
            OrderStatus = "Confirmed", PaymentStatus = revision.TotalAmount == 0 ? "Paid" : "Unpaid", ShippingAddress = address,
            ShippingProvinceId = provinceId, ShippingProvinceName = provinceName, ShippingWardId = wardId, ShippingWardName = wardName,
            Note = input.Note?.Trim() ?? ""
        };
        // The accepted quote is an all-inclusive price. Delivery is created by operations after production starts.
        created.Items.Add(new OrderItem { OrderId = created.Id, ProductId = request.ProductId, Quantity = request.Quantity, UnitPrice = revision.UnitPrice, FinalPrice = revision.UnitPrice, CustomNote = request.Description, ReferenceImageUrl = request.ReferenceImageUrl });
        db.Add(created);
        db.Add(new CustomOrderCheckout { CustomQuotationId = quote.Id, QuotationVersion = revision.Version, CustomerId = customerId, OrderId = created.Id, IdempotencyKey = key });
        quote.Status = request.Status = "CheckedOut";
        quote.UpdatedAt = request.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await CheckoutDto(created, revision, ct);
    }

    public async Task<CustomLifecycleDto> StartProductionAsync(Guid id, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var context = await LifecycleContext(id, ct);
        if (context.Order.OrderStatus is "Cancelled" or "Expired" or "Completed") throw Error(409, "Đơn đã kết thúc.");
        if (context.Paid < context.Revision.DepositAmount) throw Error(409, "Chưa đủ tiền cọc để bắt đầu sản xuất.");
        if (context.Quote.Status is not ("CheckedOut" or "Production")) throw Error(409, "Đơn chưa sẵn sàng sản xuất.");
        if (context.Quote.Status == "Production") { await tx.CommitAsync(ct); return ToLifecycle(context, "Production", context.Order.OrderStatus); }
        context.Quote.Status = context.Request.Status = "Production";
        context.Request.ProductionStartedAt ??= DateTime.UtcNow;
        context.Quote.UpdatedAt = context.Request.UpdatedAt = DateTime.UtcNow;
        context.Order.OrderStatus = "Processing";
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToLifecycle(context, "Production", "Preparing");
    }

    public async Task<CustomLifecycleDto> CompleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var context = await LifecycleContext(id, ct);
        if (context.Request.ProductionStartedAt is null || context.Order.OrderStatus is "Cancelled" or "Expired") throw Error(409, "Đơn chưa sản xuất hoặc đã hủy.");
        if (context.Paid < context.Revision.TotalAmount) throw Error(409, "Chưa thanh toán đủ tổng tiền.");
        var items = await db.OrderItems.Where(x => x.OrderId == context.Order.Id).ToListAsync(ct);
        var delivered = await db.DeliveryItems.Where(x => db.Deliveries.Any(d => d.Id == x.DeliveryId && d.OrderId == context.Order.Id && d.DeliveryStatus == "Delivered")).ToListAsync(ct);
        if (items.Any(x => delivered.Where(d => d.OrderItemId == x.Id).Sum(d => d.ShippedQuantity) < x.Quantity)) throw Error(409, "Chưa giao đủ sản phẩm.");
        context.Quote.Status = context.Request.Status = context.Order.OrderStatus = "Completed";
        context.Request.CompletedAt ??= DateTime.UtcNow;
        context.Quote.UpdatedAt = context.Request.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToLifecycle(context, "Completed", "Delivered");
    }

    private sealed record DeliveryArea(int? ProvinceId, string? ProvinceName, int? WardId, string? WardName)
    {
        public bool IsEmpty => ProvinceId is null && WardId is null;
    }

    private async Task<DeliveryArea> ValidateRequestAsync(CustomRequestInput input, CancellationToken ct)
    {
        if (input is null || input.Quantity is < 1 or > 1000 || string.IsNullOrWhiteSpace(input.Description) || input.Description.Length > 4000) throw Error(400, "Thông tin yêu cầu chưa hợp lệ.");
        var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(x => x.Id == input.ProductId && x.IsActive, ct) ?? throw Error(404, "Không tìm thấy sản phẩm.");
        if (!product.IsCustomizable && !product.IsPreOrder) throw Error(400, "Sản phẩm không hỗ trợ tùy biến hoặc đặt trước.");
        if (input.RequestedCompletionDate is { } date && Utc(date) <= DateTime.UtcNow) throw Error(400, "Ngày mong muốn phải ở tương lai.");
        return await ResolveDeliveryAreaAsync(input, ct);
    }

    /// <summary>Đối chiếu khu vực giao với danh mục GHN hiện tại; bỏ trống cả hai thì trả về rỗng.</summary>
    private async Task<DeliveryArea> ResolveDeliveryAreaAsync(CustomRequestInput input, CancellationToken ct)
    {
        if (input.ProvinceId is null && input.WardId is null) return new(null, null, null, null);
        if (input.ProvinceId is not > 0 || input.WardId is not > 0) throw Error(400, "Chọn cả tỉnh và phường GHN hợp lệ.");
        if (addressCatalog is null) throw Error(503, "Danh mục địa chỉ GHN chưa sẵn sàng.");
        var province = (await addressCatalog.GetProvincesAsync(ct)).SingleOrDefault(x => x.Id == input.ProvinceId);
        if (province is null) throw Error(400, "Tỉnh không thuộc danh mục GHN hiện tại.");
        var ward = (await addressCatalog.GetWardsAsync(province.Id, ct)).SingleOrDefault(x => x.Id == input.WardId && x.ParentId == province.Id);
        if (ward is null) throw Error(400, "Phường không thuộc tỉnh đã chọn trong danh mục GHN.");
        return new(province.Id, province.Name, ward.Id, ward.Name);
    }

    private static void ApplyDeliveryArea(CustomOrderRequest request, DeliveryArea area)
    {
        request.DeliveryProvinceId = area.ProvinceId; request.DeliveryProvinceName = area.ProvinceName;
        request.DeliveryWardId = area.WardId; request.DeliveryWardName = area.WardName;
    }

    private static void Apply(CustomOrderRequest request, CustomRequestInput input)
    {
        request.ProductId = input.ProductId; request.Quantity = input.Quantity; request.Description = input.Description.Trim(); request.ReferenceImageUrl = input.ReferenceImageUrl?.Trim() ?? "";
        request.RequestedCompletionDate = input.RequestedCompletionDate is { } date ? Utc(date) : null; request.UpdatedAt = DateTime.UtcNow;
    }
    private async Task<CustomOrderRequest> OwnedAsync(Guid id, Guid customerId, CancellationToken ct)
    {
        var request = await db.Set<CustomOrderRequest>().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw Error(404, "Không tìm thấy yêu cầu.");
        if (request.CustomerId != customerId) throw Error(403, "Bạn không có quyền với yêu cầu này.");
        return request;
    }
    private async Task<decimal> PaidAsync(Guid orderId, CancellationToken ct) => (await db.Payments.Where(x => x.OrderId == orderId && x.PaymentStatus == "Paid").Select(x => x.Amount).ToListAsync(ct)).Sum();
    private async Task<CustomCheckoutDto> CheckoutDto(Order order, CustomQuotationRevision revision, CancellationToken ct)
    {
        var paid = await PaidAsync(order.Id, ct);
        return new(order.Id, order.OrderCode, revision.Version, revision.TotalAmount, revision.DepositAmount, paid, Math.Max(0, revision.TotalAmount - paid), order.PaymentStatus);
    }
    private sealed record LifecycleState(CustomOrderRequest Request, CustomQuotation Quote, CustomQuotationRevision Revision, Order Order, decimal Paid);
    private async Task<LifecycleState> LifecycleContext(Guid id, CancellationToken ct)
    {
        var request = await db.Set<CustomOrderRequest>().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw Error(404, "Không tìm thấy yêu cầu.");
        var quote = await db.Set<CustomQuotation>().SingleAsync(x => x.CustomOrderRequestId == id, ct);
        var checkout = await db.Set<CustomOrderCheckout>().SingleOrDefaultAsync(x => x.CustomQuotationId == quote.Id, ct) ?? throw Error(409, "Chưa checkout.");
        var revision = await db.Set<CustomQuotationRevision>().SingleAsync(x => x.CustomQuotationId == quote.Id && x.Version == checkout.QuotationVersion, ct);
        var order = await db.Orders.SingleAsync(x => x.Id == checkout.OrderId, ct);
        return new(request, quote, revision, order, await PaidAsync(order.Id, ct));
    }
    private static CustomLifecycleDto ToLifecycle(LifecycleState state, string status, string delivery) => new(state.Request.Id, state.Order.Id, status, state.Revision.CompletionDate, state.Request.ProductionStartedAt, state.Request.CompletedAt, state.Revision.DepositAmount, state.Paid, Math.Max(0, state.Revision.TotalAmount - state.Paid), delivery);

    /// <summary>Batched projection: one small query per related table, never per request row.</summary>
    private async Task<IReadOnlyList<CustomRequestDto>> MapAsync(IReadOnlyList<CustomOrderRequest> requests, CancellationToken ct, bool hideMoney = false)
    {
        if (requests.Count == 0) return [];
        var requestIds = requests.Select(x => x.Id).ToList();
        var productIds = requests.Select(x => x.ProductId).Distinct().ToList();
        var names = await db.Products.AsNoTracking().Where(x => productIds.Contains(x.Id)).Select(x => new { x.Id, x.Name }).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var quotes = await db.Set<CustomQuotation>().AsNoTracking().Where(x => requestIds.Contains(x.CustomOrderRequestId)).ToDictionaryAsync(x => x.CustomOrderRequestId, ct);
        var quoteIds = quotes.Values.Select(x => x.Id).Distinct().ToList();
        var checkouts = new Dictionary<Guid, CustomOrderCheckout>();
        var orderCodes = new Dictionary<Guid, string>();
        var paid = new Dictionary<Guid, decimal>();
        var revisions = new Dictionary<(Guid QuoteId, int Version), (decimal Deposit, decimal Total)>();
        var items = new List<(Guid Id, Guid OrderId, int Quantity)>();
        var delivered = new Dictionary<Guid, int>();
        if (quoteIds.Count > 0)
        {
            foreach (var checkout in await db.Set<CustomOrderCheckout>().AsNoTracking().Where(x => quoteIds.Contains(x.CustomQuotationId)).ToListAsync(ct)) checkouts[checkout.CustomQuotationId] = checkout;
            foreach (var revision in await db.Set<CustomQuotationRevision>().AsNoTracking().Where(x => quoteIds.Contains(x.CustomQuotationId))
                         .Select(x => new { x.CustomQuotationId, x.Version, x.DepositAmount, x.TotalAmount }).ToListAsync(ct))
                revisions[(revision.CustomQuotationId, revision.Version)] = (revision.DepositAmount, revision.TotalAmount);
        }
        var orderIds = checkouts.Values.Select(x => x.OrderId).Distinct().ToList();
        if (orderIds.Count > 0)
        {
            foreach (var order in await db.Orders.AsNoTracking().Where(x => orderIds.Contains(x.Id)).Select(x => new { x.Id, x.OrderCode }).ToListAsync(ct)) orderCodes[order.Id] = order.OrderCode;
            // SQLite cannot sum decimals server-side; group in memory over the bounded order set.
            foreach (var payment in await db.Payments.AsNoTracking().Where(x => x.OrderId != null && orderIds.Contains(x.OrderId.Value) && x.PaymentStatus == "Paid")
                         .Select(x => new { OrderId = x.OrderId!.Value, x.Amount }).ToListAsync(ct)) paid[payment.OrderId] = paid.GetValueOrDefault(payment.OrderId) + payment.Amount;
            foreach (var item in await db.OrderItems.AsNoTracking().Where(x => orderIds.Contains(x.OrderId)).Select(x => new { x.Id, x.OrderId, x.Quantity }).ToListAsync(ct)) items.Add((item.Id, item.OrderId, item.Quantity));
        }
        var itemIds = items.Select(x => x.Id).ToList();
        if (itemIds.Count > 0)
            foreach (var line in await db.DeliveryItems.AsNoTracking().Where(x => itemIds.Contains(x.OrderItemId) && db.Deliveries.Any(d => d.Id == x.DeliveryId && d.DeliveryStatus == "Delivered"))
                         .GroupBy(x => x.OrderItemId).Select(g => new { OrderItemId = g.Key, Quantity = g.Sum(x => x.ShippedQuantity) }).ToListAsync(ct)) delivered[line.OrderItemId] = line.Quantity;
        return requests.Select(request =>
        {
            quotes.TryGetValue(request.Id, out var quote);
            var checkout = quote is not null && checkouts.TryGetValue(quote.Id, out var found) ? found : null;
            Guid? orderId = null; string? orderCode = null; bool depositReady = false, fullyPaid = false, deliveryComplete = false;
            if (checkout is not null)
            {
                orderId = checkout.OrderId;
                orderCode = orderCodes.GetValueOrDefault(checkout.OrderId);
                var paidAmount = paid.GetValueOrDefault(checkout.OrderId);
                if (revisions.TryGetValue((checkout.CustomQuotationId, checkout.QuotationVersion), out var revision))
                {
                    depositReady = paidAmount >= revision.Deposit;
                    fullyPaid = paidAmount >= revision.Total;
                }
                deliveryComplete = !items.Where(x => x.OrderId == checkout.OrderId).Any(x => delivered.GetValueOrDefault(x.Id) < x.Quantity);
            }
            return new CustomRequestDto(request.Id, request.ProductId, request.Quantity, request.Description, request.ReferenceImageUrl, request.RequestedCompletionDate, request.Status, request.CreatedAt,
                names.GetValueOrDefault(request.ProductId) ?? "", request.UpdatedAt, quote?.CurrentRevision ?? 0, orderId, orderCode, depositReady, fullyPaid, deliveryComplete,
                request.DeliveryProvinceId, request.DeliveryProvinceName, request.DeliveryWardId, request.DeliveryWardName, BouquetCatalog.Read(request.BouquetSnapshot, hideMoney));
        }).ToList();
    }
}
