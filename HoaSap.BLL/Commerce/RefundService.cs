using System.Data;
using HoaSap.BLL;
using HoaSap.DAL;
using HoaSap.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace HoaSap.BLL.Commerce;

public sealed record RefundDto(Guid Id, Guid? OrderId, Guid? BookingId, decimal RefundAmount, string RefundReason, string RefundStatus, DateTime RequestedAt, string? ApprovedBy, DateTime? ProcessedAt, string? TransactionCode);
public sealed record RefundPage(IReadOnlyList<RefundDto> Items, int Page, int PageSize, int Total);

public sealed class RefundRequestDto
{
    public decimal Amount { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class CustomerRefundRequestDto
{
    public string Reason { get; set; } = "";
    public decimal? Amount { get; set; }
}

public sealed class RefundDecisionDto
{
    public string? Reason { get; set; }
}

public sealed class RefundProcessDto
{
    public string TransactionCode { get; set; } = "";
}
public sealed class RefundProcessGatewayDto
{
    public string OriginalTransactionCode { get; set; } = "";
}

/// <summary>Manual, ledger-only refunds. This service never calls a payment provider.</summary>
public sealed class RefundService(HoaSapDbContext db)
{
    public async Task<RefundDto> GetForGatewayAsync(Guid id, CancellationToken ct = default)
    {
        var refund = await db.Refunds.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new BusinessException(404, "Không tìm thấy yêu cầu hoàn tiền.");
        return ToDto(refund);
    }
    public async Task<RefundPage> ListAsync(int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
        var query = db.Refunds.AsNoTracking().OrderByDescending(x => x.RequestedAt).ThenByDescending(x => x.Id);
        var total = await query.CountAsync(ct);
        var offset = (long)(page - 1) * pageSize;
        var entities = await query.Skip(offset > int.MaxValue ? int.MaxValue : (int)offset).Take(pageSize).ToListAsync(ct);
        return new(entities.Select(ToDto).ToList(), page, pageSize, total);
    }

    public async Task<RefundDto> RequestOrderAsync(Guid orderId, RefundRequestDto request, CancellationToken ct = default)
    {
        ValidateAmount(request.Amount, request.Reason);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var order = await db.Orders.Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == orderId, ct)
            ?? throw new BusinessException(404, "Không tìm thấy order.");
        if (order.OrderStatus is not ("Cancelled" or "Expired" or "Confirmed" or "Processing"))
            throw new BusinessException(409, "Order đã giao hoặc không còn đủ điều kiện hoàn tiền.");
        if (await db.Deliveries.AnyAsync(x => x.OrderId == orderId && x.DeliveryStatus != "Cancelled", ct))
            throw new BusinessException(409, "Order đã có giao hàng đang xử lý, không thể hoàn tiền.");
        if (order.PaymentStatus is not ("Paid" or "PartiallyPaid" or "PartiallyRefunded"))
            throw new BusinessException(409, "Order chưa có khoản thanh toán được ghi nhận.");
        var paid = await PaidOrderAsync(orderId, ct);
        var reserved = (await db.Refunds.Where(x => x.OrderId == orderId && x.RefundStatus != "Rejected")
            .Select(x => x.RefundAmount).ToListAsync(ct)).Sum();
        if (request.Amount > paid - reserved) throw new BusinessException(400, "Số tiền hoàn vượt quá số tiền đã thanh toán còn lại.");

        // A refund request is also the safe cancellation boundary for an unpaid shipment workflow.
        // Reservation release is performed only on the first transition to Cancelled.
        if (order.OrderStatus is not ("Cancelled" or "Expired"))
        {
            if (!await db.Set<CustomOrderCheckout>().AnyAsync(x => x.OrderId == order.Id, ct))
                await ReleaseReservedAsync(order.Items, ct);
            if (order.VoucherId is { } voucherId)
            {
                if (await db.ExecuteUpdateAuditedAsync(db.Vouchers.Where(v => v.Id == voucherId && v.UsedCount > 0), x => x.SetProperty(v => v.UsedCount, v => v.UsedCount - 1), ct) != 1)
                    throw new BusinessException(409, "Không thể hoàn lại lượt sử dụng voucher.");
                // Preserve the original voucher reference for accounting history.
            }
            order.OrderStatus = "Cancelled";
            var link = await db.Set<CustomOrderCheckout>().SingleOrDefaultAsync(x => x.OrderId == order.Id, ct);
            if (link is not null)
            {
                var quote = await db.Set<CustomQuotation>().SingleAsync(x => x.Id == link.CustomQuotationId, ct);
                var custom = await db.Set<CustomOrderRequest>().SingleAsync(x => x.Id == quote.CustomOrderRequestId, ct);
                quote.Status = custom.Status = "Cancelled";
                quote.UpdatedAt = custom.UpdatedAt = DateTime.UtcNow;
            }
        }
        var refund = new Refund { OrderId = orderId, RefundAmount = request.Amount, RefundReason = request.Reason.Trim(), RefundStatus = "Requested", ApprovedBy = "", RequestedAt = DateTime.UtcNow, ConcurrencyToken = Guid.NewGuid() };
        db.Refunds.Add(refund);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return ToDto(refund);
    }

    public async Task<RefundDto> CustomerRequestRefundAsync(Guid customerId, Guid orderId, CustomerRefundRequestDto request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 500)
            throw new BusinessException(400, "Lý do hoàn tiền bắt buộc và không quá 500 ký tự.");

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var order = await db.Orders.Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == orderId && x.CustomerId == customerId, ct)
            ?? throw new BusinessException(404, "Không tìm thấy đơn hàng của bạn.");

        if (order.OrderStatus is "Cancelled" or "Expired")
            throw new BusinessException(409, "Đơn hàng đã ở trạng thái hủy hoặc hết hạn.");

        if (order.OrderStatus is "Delivering")
            throw new BusinessException(409, "Đơn hàng đang trong quá trình giao hàng. Quý khách không thể tự hoàn tiền khi bưu tá đang giao. Vui lòng liên hệ Admin/Cửa hàng để thu hồi hàng trước.");

        // BẢO VỆ CHẶT CHẼ LIÊN KẾT GIAO HÀNG (GHN):
        // Nếu shipper đang trên đường giao hàng (Picking/Delivering/InTransit/Shipped), chặn tuyệt đối không cho tự hoàn tiền.
        var activeDelivery = await db.Deliveries
            .AsNoTracking()
            .Where(x => x.OrderId == orderId && x.DeliveryStatus != "Cancelled")
            .OrderByDescending(x => x.ShippedAt)
            .FirstOrDefaultAsync(ct);

        if (activeDelivery is not null && activeDelivery.DeliveryStatus is "Picking" or "Delivering" or "InTransit" or "Shipped")
        {
            throw new BusinessException(409, "Đơn hàng đang trong quá trình vận chuyển bởi Giao Hàng Nhanh. Quý khách không thể tự hoàn tiền khi bưu tá đang giao. Vui lòng liên hệ Admin/Cửa hàng để thu hồi hàng trước.");
        }

        if (order.PaymentStatus is not ("Paid" or "PartiallyPaid" or "PartiallyRefunded"))
            throw new BusinessException(409, "Đơn hàng chưa có khoản thanh toán được ghi nhận để hoàn tiền.");

        var paid = await PaidOrderAsync(orderId, ct);
        var reserved = (await db.Refunds.Where(x => x.OrderId == orderId && x.RefundStatus != "Rejected")
            .Select(x => x.RefundAmount).ToListAsync(ct)).Sum();
        var refundable = paid - reserved;
        if (refundable <= 0) throw new BusinessException(400, "Đơn hàng không còn số tiền khả dụng để hoàn.");

        var refundAmount = request.Amount is { } reqAmt && reqAmt > 0 ? reqAmt : refundable;
        if (refundAmount > refundable)
            throw new BusinessException(400, $"Số tiền yêu cầu hoàn ({refundAmount:N0} đ) vượt quá số tiền đã thanh toán còn lại ({refundable:N0} đ).");

        // Nếu đơn hàng chưa xuất kho hoặc vận đơn GHN đã hủy: hủy đơn an toàn, giải phóng kho và hoàn voucher
        if (activeDelivery is null || activeDelivery.DeliveryStatus == "Cancelled")
        {
            if (order.OrderStatus is not ("Cancelled" or "Expired"))
            {
                if (!await db.Set<CustomOrderCheckout>().AnyAsync(x => x.OrderId == order.Id, ct))
                    await ReleaseReservedAsync(order.Items, ct);
                if (order.VoucherId is { } voucherId)
                {
                    if (await db.ExecuteUpdateAuditedAsync(db.Vouchers.Where(v => v.Id == voucherId && v.UsedCount > 0), x => x.SetProperty(v => v.UsedCount, v => v.UsedCount - 1), ct) != 1)
                        throw new BusinessException(409, "Không thể hoàn lại lượt sử dụng voucher.");
                }
                order.OrderStatus = "Cancelled";
                var link = await db.Set<CustomOrderCheckout>().SingleOrDefaultAsync(x => x.OrderId == order.Id, ct);
                if (link is not null)
                {
                    var quote = await db.Set<CustomQuotation>().SingleAsync(x => x.Id == link.CustomQuotationId, ct);
                    var custom = await db.Set<CustomOrderRequest>().SingleAsync(x => x.Id == quote.CustomOrderRequestId, ct);
                    quote.Status = custom.Status = "Cancelled";
                    quote.UpdatedAt = custom.UpdatedAt = DateTime.UtcNow;
                }
            }
        }

        var refund = new Refund
        {
            OrderId = orderId,
            RefundAmount = refundAmount,
            RefundReason = request.Reason.Trim(),
            RefundStatus = "Requested",
            ApprovedBy = "",
            RequestedAt = DateTime.UtcNow,
            ConcurrencyToken = Guid.NewGuid()
        };
        db.Refunds.Add(refund);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToDto(refund);
    }

    public async Task<IReadOnlyList<RefundDto>> GetOrderRefundsForCustomerAsync(Guid customerId, Guid orderId, CancellationToken ct = default)
    {
        var orderExists = await db.Orders.AnyAsync(x => x.Id == orderId && x.CustomerId == customerId, ct);
        if (!orderExists) throw new BusinessException(404, "Không tìm thấy đơn hàng của bạn.");
        var refunds = await db.Refunds.AsNoTracking().Where(x => x.OrderId == orderId).OrderByDescending(x => x.RequestedAt).ToListAsync(ct);
        return refunds.Select(ToDto).ToList();
    }

    public async Task<RefundDto> ApproveAsync(Guid id, string actor, CancellationToken ct = default) => await DecideAsync(id, "Approved", actor, ct);
    public async Task<RefundDto> RejectAsync(Guid id, string actor, CancellationToken ct = default) => await DecideAsync(id, "Rejected", actor, ct);

    public async Task<RefundDto> ClaimForGatewayAsync(Guid id, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var refund = await db.Refunds.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new BusinessException(404, "Không tìm thấy yêu cầu hoàn tiền.");
        if (refund.RefundStatus == "Processed") { await tx.CommitAsync(ct); return ToDto(refund); }
        if (refund.RefundStatus != "Approved") throw new BusinessException(409, "Refund phải được admin duyệt trước khi gửi gateway.");
        refund.RefundStatus = "Processing"; refund.ConcurrencyToken = Guid.NewGuid();
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return ToDto(refund);
    }

    public async Task<RefundDto> CompleteGatewayAsync(Guid id, string transactionCode, CancellationToken ct = default)
    {
        transactionCode = transactionCode?.Trim() ?? "";
        if (transactionCode.Length is 0 or > 100) throw new BusinessException(400, "TransactionCode bắt buộc và tối đa 100 ký tự.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var refund = await db.Refunds.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new BusinessException(404, "Không tìm thấy yêu cầu hoàn tiền.");
        if (refund.RefundStatus == "Processed") { if (refund.TransactionCode == transactionCode) { await tx.CommitAsync(ct); return ToDto(refund); } throw new BusinessException(409, "Refund đã được xử lý với mã khác."); }
        if (refund.RefundStatus != "Processing") throw new BusinessException(409, "Refund chưa được claim để hoàn tiền.");
        if (await db.Refunds.AnyAsync(x => x.Id != id && x.TransactionCode == transactionCode, ct)) throw new BusinessException(409, "Mã giao dịch hoàn tiền đã được sử dụng.");
        refund.TransactionCode = transactionCode; refund.RefundStatus = "Processed"; refund.ProcessedAt = DateTime.UtcNow; refund.ConcurrencyToken = Guid.NewGuid();
        if (refund.OrderId is { } orderId)
        {
            var order = await db.Orders.SingleOrDefaultAsync(x => x.Id == orderId, ct) ?? throw new BusinessException(404, "Không tìm thấy order.");
            var paid = await PaidOrderAsync(orderId, ct);
            var processed = (await db.Refunds.Where(x => x.OrderId == orderId && x.RefundStatus == "Processed" && x.Id != id).Select(x => x.RefundAmount).ToListAsync(ct)).Sum() + refund.RefundAmount;
            if (processed > paid) throw new BusinessException(409, "Tổng số tiền hoàn vượt quá số tiền đã thanh toán.");
            order.PaymentStatus = processed >= paid ? "Refunded" : "PartiallyRefunded"; order.ConcurrencyToken = Guid.NewGuid();
        }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return ToDto(refund);
    }

    public async Task<RefundDto> ProcessAsync(Guid id, string transactionCode, CancellationToken ct = default)
    {
        transactionCode = transactionCode?.Trim() ?? "";
        if (transactionCode.Length is 0 or > 100) throw new BusinessException(400, "TransactionCode bắt buộc và tối đa 100 ký tự.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var refund = await db.Refunds.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new BusinessException(404, "Không tìm thấy yêu cầu hoàn tiền.");
        if (refund.RefundStatus == "Processed")
        {
            if (string.Equals(refund.TransactionCode, transactionCode, StringComparison.Ordinal)) { await tx.CommitAsync(ct); return ToDto(refund); }
            throw new BusinessException(409, "Yêu cầu hoàn tiền đã được xử lý với mã giao dịch khác.");
        }
        if (refund.RefundStatus != "Approved") throw new BusinessException(409, "Chỉ yêu cầu đã duyệt mới được xử lý hoàn tiền.");
        var duplicate = await db.Refunds.AnyAsync(x => x.Id != id && x.TransactionCode == transactionCode, ct);
        if (duplicate) throw new BusinessException(409, "Mã giao dịch hoàn tiền đã được sử dụng.");
        refund.TransactionCode = transactionCode; refund.RefundStatus = "Processed"; refund.ProcessedAt = DateTime.UtcNow; refund.ConcurrencyToken = Guid.NewGuid();
        if (refund.OrderId is { } orderId)
        {
            var order = await db.Orders.SingleOrDefaultAsync(x => x.Id == orderId, ct) ?? throw new BusinessException(404, "Không tìm thấy order.");
            var paid = await PaidOrderAsync(orderId, ct);
            var processed = (await db.Refunds.Where(x => x.OrderId == orderId && x.RefundStatus == "Processed")
                .Select(x => x.RefundAmount).ToListAsync(ct)).Sum() + refund.RefundAmount;
            if (processed > paid) throw new BusinessException(409, "Tổng số tiền hoàn vượt quá số tiền đã thanh toán.");
            order.PaymentStatus = processed >= paid ? "Refunded" : "PartiallyRefunded";
            order.ConcurrencyToken = Guid.NewGuid();
        }
        else if (refund.BookingId is { } bookingId)
        {
            var booking = await db.WorkshopBookings.SingleOrDefaultAsync(x => x.Id == bookingId, ct)
                ?? throw new BusinessException(404, "Không tìm thấy booking.");
            if (!string.Equals(booking.BookingStatus, "cancelled", StringComparison.OrdinalIgnoreCase))
                throw new BusinessException(409, "Booking phải được hủy trước khi xử lý hoàn tiền.");
            var paid = await PaidBookingAsync(bookingId, ct);
            var processed = (await db.Refunds.Where(x => x.BookingId == bookingId && x.RefundStatus == "Processed")
                .Select(x => x.RefundAmount).ToListAsync(ct)).Sum() + refund.RefundAmount;
            if (processed > paid) throw new BusinessException(409, "Tổng số tiền hoàn vượt quá số tiền booking đã thanh toán.");
        }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return ToDto(refund);
    }

    private async Task<RefundDto> DecideAsync(Guid id, string status, string actor, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var refund = await db.Refunds.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new BusinessException(404, "Không tìm thấy yêu cầu hoàn tiền.");
        if (refund.RefundStatus != "Requested") throw new BusinessException(409, "Yêu cầu hoàn tiền không ở trạng thái Requested.");
        refund.RefundStatus = status; refund.ApprovedBy = actor; refund.ConcurrencyToken = Guid.NewGuid();
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return ToDto(refund);
    }

    private async Task<decimal> PaidOrderAsync(Guid id, CancellationToken ct) =>
        (await db.Payments.Where(x => x.OrderId == id && x.PaymentStatus == "Paid").Select(x => x.Amount).ToListAsync(ct)).Sum();

    private async Task<decimal> PaidBookingAsync(Guid id, CancellationToken ct) =>
        (await db.Payments.Where(x => x.BookingId == id && x.PaymentStatus == "Paid").Select(x => x.Amount).ToListAsync(ct)).Sum();

    private async Task ReleaseReservedAsync(IEnumerable<OrderItem> items, CancellationToken ct)
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
            if (affected != 1) throw new BusinessException(409, "Không thể giải phóng tồn kho cho order.");
        }
    }

    private static void ValidateAmount(decimal amount, string? reason)
    {
        if (amount <= 0 || decimal.Round(amount, 2) != amount) throw new BusinessException(400, "Amount phải dương và tối đa 2 chữ số thập phân.");
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 500) throw new BusinessException(400, "Reason bắt buộc và tối đa 500 ký tự.");
    }

    private static RefundDto ToDto(Refund x) => new(x.Id, x.OrderId, x.BookingId, x.RefundAmount, x.RefundReason, x.RefundStatus, x.RequestedAt, string.IsNullOrWhiteSpace(x.ApprovedBy) ? null : x.ApprovedBy, x.ProcessedAt, x.TransactionCode);
}
