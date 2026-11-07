using System.Data;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using HoaSap.BLL;
using HoaSap.DAL;
using HoaSap.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace HoaSap.BLL.Workshops;

public sealed class WorkshopService(HoaSapDbContext db, WorkshopTicketCodec? tickets = null)
{
    private const decimal DepositRate = 0.30m;
    private static readonly string[] ManualMethods = ["Cash", "BankTransfer", "MoMo"];
    private static readonly TimeZoneInfo VietnamTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
    private WorkshopTicketCodec Tickets => tickets ?? throw new InvalidOperationException("Workshop ticket signing is not configured.");

    public async Task<IReadOnlyList<WorkshopListDto>> ListAsync(bool staff = false, CancellationToken ct = default) =>
        await db.Workshops.AsNoTracking().Where(x => staff || x.IsActive).OrderBy(x => x.Name)
            .Select(x => new WorkshopListDto(x.Id, x.Code, x.Name, x.ShortName, x.Category, x.BasePrice, x.Duration, x.MaxCapacity, x.Description, x.IsFeatured, x.IsActive)).ToListAsync(ct);

    public async Task<WorkshopDto> GetAsync(Guid id, bool staff = false, CancellationToken ct = default)
    {
        var x = await db.Workshops.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && (staff || x.IsActive), ct)
            ?? throw new BusinessException(404, "Không tìm thấy workshop.");
        return ToDto(x);
    }

    public async Task<WorkshopDto> CreateAsync(WorkshopUpsertDto r, CancellationToken ct = default)
    {
        await ValidateWorkshopAsync(r, ct);
        var x = new Workshop { Code = r.Code.Trim(), Name = r.Name.Trim(), ShortName = r.ShortName?.Trim() ?? "", Category = r.Category?.Trim() ?? "", BasePrice = r.BasePrice, Duration = r.Duration, MaxCapacity = r.MaxCapacity, Description = r.Description ?? "", LeadContent = r.LeadContent ?? "", GoalsJson = NormalizeArrayJson(r.GoalsJson), IncludesJson = NormalizeArrayJson(r.IncludesJson), ForWho = r.ForWho ?? "", IsFeatured = r.IsFeatured, IsActive = r.IsActive, InstructorId = r.InstructorId };
        db.Workshops.Add(x); await db.SaveChangesAsync(ct); return ToDto(x);
    }

    public async Task<WorkshopDto> UpdateAsync(Guid id, WorkshopUpsertDto r, CancellationToken ct = default)
    {
        await ValidateWorkshopAsync(r, ct);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var x = await db.Workshops.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new BusinessException(404, "Không tìm thấy workshop.");
        if (r.MaxCapacity < await db.WorkshopSessions.Where(s => s.WorkshopId == id).Select(s => (int?)s.BookedSlots).MaxAsync(ct)) throw new BusinessException(409, "Không thể giảm sức chứa workshop dưới số chỗ đã đặt.");
        x.Code = r.Code.Trim(); x.Name = r.Name.Trim(); x.ShortName = r.ShortName?.Trim() ?? ""; x.Category = r.Category?.Trim() ?? ""; x.BasePrice = r.BasePrice; x.Duration = r.Duration; x.MaxCapacity = r.MaxCapacity; x.Description = r.Description ?? ""; x.LeadContent = r.LeadContent ?? ""; x.GoalsJson = NormalizeArrayJson(r.GoalsJson); x.IncludesJson = NormalizeArrayJson(r.IncludesJson); x.ForWho = r.ForWho ?? ""; x.IsFeatured = r.IsFeatured; x.IsActive = r.IsActive; x.InstructorId = r.InstructorId;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return ToDto(x);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var x = await db.Workshops.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new BusinessException(404, "Không tìm thấy workshop.");
        x.IsActive = false; await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<SessionDto>> SessionsAsync(Guid workshopId, bool staff = false, CancellationToken ct = default)
    {
        // SQLite does not support ordering TimeSpan directly. Materialize before the time-of-day sort.
        var sessions = await db.WorkshopSessions.AsNoTracking()
            .Where(x => x.WorkshopId == workshopId && (staff || x.SessionStatus != "Cancelled"))
            .OrderBy(x => x.SessionDate).ToListAsync(ct);
        return sessions.OrderBy(x => x.SessionDate).ThenBy(x => x.StartTime).Select(ToDto).ToList();
    }

    public async Task<SessionDto> CreateSessionAsync(Guid workshopId, SessionUpsertDto r, CancellationToken ct = default)
    {
        ValidateSession(r);
        var w = await db.Workshops.FirstOrDefaultAsync(x => x.Id == workshopId && x.IsActive, ct) ?? throw new BusinessException(404, "Workshop không tồn tại hoặc đã ngừng hoạt động.");
        if (r.Capacity > w.MaxCapacity) throw new BusinessException(400, "Sức chứa ca học không được vượt quá workshop.");
        var x = new WorkshopSession { WorkshopId = workshopId, SessionDate = r.SessionDate.Date, StartTime = r.StartTime, EndTime = r.EndTime, Location = r.Location.Trim(), Capacity = r.Capacity, SessionStatus = "Open" };
        db.WorkshopSessions.Add(x); await db.SaveChangesAsync(ct); return ToDto(x);
    }

    public async Task<SessionDto> UpdateSessionAsync(Guid id, SessionUpsertDto r, CancellationToken ct = default)
    {
        ValidateSession(r);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var x = await db.WorkshopSessions.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new BusinessException(404, "Không tìm thấy ca học.");
        var w = await db.Workshops.AsNoTracking().FirstAsync(w => w.Id == x.WorkshopId, ct);
        if (x.SessionStatus is "Cancelled" or "Closed" || SessionEndUtc(x) <= DateTime.UtcNow) throw new BusinessException(409, "Không thể cập nhật ca học đã hủy, đã đóng hoặc đã kết thúc.");
        if (r.Capacity > w.MaxCapacity || r.Capacity < x.BookedSlots) throw new BusinessException(400, "Sức chứa ca học không hợp lệ.");
        var active = await db.WorkshopBookings.AnyAsync(b => b.SessionId == id && b.BookingStatus != "cancelled" && b.BookingStatus != "expired" && b.BookingStatus != "attended" && b.BookingStatus != "no_show", ct);
        if (active && (x.SessionDate.Date != r.SessionDate.Date || x.StartTime != r.StartTime || x.EndTime != r.EndTime || !string.Equals(x.Location.Trim(), r.Location.Trim(), StringComparison.Ordinal))) throw new BusinessException(409, "Không thể đổi thời gian hoặc địa điểm khi ca có booking hoạt động.");
        x.SessionDate = r.SessionDate.Date; x.StartTime = r.StartTime; x.EndTime = r.EndTime; x.Location = r.Location.Trim(); x.Capacity = r.Capacity; SetFullOrOpen(x);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return ToDto(x);
    }

    public async Task CancelSessionAsync(Guid id, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var x = await db.WorkshopSessions.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new BusinessException(404, "Không tìm thấy ca học.");
        if (x.SessionStatus == "Cancelled") { await tx.CommitAsync(ct); return; }
        var active = await db.WorkshopBookings.AnyAsync(b => b.SessionId == id && b.BookingStatus != "cancelled" && b.BookingStatus != "expired" && b.BookingStatus != "attended" && b.BookingStatus != "no_show", ct);
        if (active) throw new BusinessException(409, "Không thể hủy ca học đang có booking hoạt động.");
        x.SessionStatus = "Cancelled"; await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }

    public async Task<BookingDto> HoldBookingAsync(Guid sessionId, BookingRequest r, Guid? customerId, CancellationToken ct = default)
    {
        if (r.TicketCount is < 1 or > 100 || string.IsNullOrWhiteSpace(r.CustomerName) || string.IsNullOrWhiteSpace(r.CustomerPhone)) throw new BusinessException(400, "Thông tin khách hàng và số vé không hợp lệ.");
        var key = r.IdempotencyKey?.Trim();
        if (r.IdempotencyKey is not null && (string.IsNullOrWhiteSpace(key) || key.Length > 100)) throw new BusinessException(400, "Idempotency key không hợp lệ.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var requestFingerprint = Fingerprint(sessionId, customerId, r);
        if (!string.IsNullOrEmpty(key))
        {
            var match = await db.WorkshopBookings.SingleOrDefaultAsync(x => x.CustomerId == customerId && x.IdempotencyKey == key, ct);
            if (match is not null)
            {
                if (!string.Equals(match.RequestFingerprint, requestFingerprint, StringComparison.Ordinal)) throw new BusinessException(409, "Idempotency key đã được dùng cho payload khác.");
                if (match.BookingStatus == "pending" && match.HoldExpiresAt <= DateTime.UtcNow)
                    await ExpirePendingBookingsCoreAsync(ct);
                await tx.CommitAsync(ct); return await ToDtoAsync(match, ct);
            }
        }
        await ExpirePendingBookingsCoreAsync(ct);
        var s = await db.WorkshopSessions.FirstOrDefaultAsync(x => x.Id == sessionId, ct) ?? throw new BusinessException(404, "Không tìm thấy ca học.");
        var w = await db.Workshops.FirstOrDefaultAsync(x => x.Id == s.WorkshopId, ct) ?? throw new BusinessException(404, "Không tìm thấy workshop.");
        if (!w.IsActive || s.SessionStatus is "Cancelled" or "Closed" || SessionStartUtc(s) <= DateTime.UtcNow) throw new BusinessException(409, "Ca học không còn nhận booking.");
        if (s.BookedSlots + r.TicketCount > s.Capacity) throw new BusinessException(409, "Ca học không đủ chỗ.");
        s.BookedSlots += r.TicketCount; SetFullOrOpen(s);
        var now = DateTime.UtcNow; var total = decimal.Round(w.BasePrice * r.TicketCount, 0); var deposit = decimal.Round(total * DepositRate, 0);
        var b = new WorkshopBooking { SessionId = sessionId, CustomerId = customerId, BookingCode = "WS-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(), CustomerName = r.CustomerName?.Trim() ?? "", CustomerPhone = r.CustomerPhone?.Trim() ?? "", CustomerEmail = r.CustomerEmail?.Trim() ?? "", TicketCount = r.TicketCount, TotalAmount = total, DepositAmount = deposit, RemainingAmount = total, BookingStatus = total == 0 ? "confirmed" : "pending", HoldExpiresAt = now.AddMinutes(10), CreatedAt = now };
        b.IdempotencyKey = key; b.RequestFingerprint = requestFingerprint;
        db.WorkshopBookings.Add(b); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return ToDto(b, 0, null);
    }

    public async Task ExpirePendingBookingsAsync(CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct); await ExpirePendingBookingsCoreAsync(ct); await tx.CommitAsync(ct);
    }

    private async Task ExpirePendingBookingsCoreAsync(CancellationToken ct)
    {
        var list = await db.WorkshopBookings.Where(x => x.BookingStatus == "pending" && x.HoldExpiresAt <= DateTime.UtcNow).ToListAsync(ct);
        foreach (var b in list)
        {
            var s = await db.WorkshopSessions.FirstOrDefaultAsync(s => s.Id == b.SessionId, ct);
            if (s is null) throw new BusinessException(409, "Booking tham chiếu ca học không tồn tại.");
            if (s.BookedSlots < b.TicketCount) throw new BusinessException(409, "Số chỗ đã đặt không nhất quán, không thể hết hạn booking.");
            b.BookingStatus = "expired"; s.BookedSlots -= b.TicketCount; SetFullOrOpen(s);
        }
        if (list.Count > 0)
        {
            var ids = list.Select(x => x.Id).ToList();
            var reports = await db.Payments.Where(x => x.BookingId != null && ids.Contains(x.BookingId.Value) && x.PaymentStatus == "PendingVerification").ToListAsync(ct);
            foreach (var report in reports) report.PaymentStatus = "Rejected";
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<BookingDto>> MyBookingsAsync(Guid customerId, CancellationToken ct = default) => await ToDtosAsync(await db.WorkshopBookings.AsNoTracking().Where(x => x.CustomerId == customerId).OrderByDescending(x => x.CreatedAt).ToListAsync(ct), ct);
    public async Task<BookingDto> BookingAsync(Guid id, Guid? customerId, bool staff, CancellationToken ct = default)
    {
        if (!staff && customerId is null) throw new BusinessException(401, "Yêu cầu đăng nhập.");
        return await ToDtoAsync(await db.WorkshopBookings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && (staff || x.CustomerId == customerId), ct) ?? throw new BusinessException(404, "Không tìm thấy booking."), ct);
    }

    public async Task<IReadOnlyList<BookingDto>> SessionRosterAsync(Guid sessionId, CancellationToken ct = default) => await ToDtosAsync(await db.WorkshopBookings.AsNoTracking().Where(x => x.SessionId == sessionId && x.BookingStatus != "cancelled" && x.BookingStatus != "expired").OrderBy(x => x.CreatedAt).ToListAsync(ct), ct);
    public async Task CloseSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var s = await db.WorkshopSessions.FirstOrDefaultAsync(x => x.Id == sessionId, ct) ?? throw new BusinessException(404, "Không tìm thấy ca học.");
        if (s.SessionStatus == "Cancelled") throw new BusinessException(409, "Ca học đã hủy.");
        if (s.SessionStatus == "Closed") { await tx.CommitAsync(ct); return; }
        if (DateTime.UtcNow < SessionEndUtc(s)) throw new BusinessException(409, "Chỉ đóng ca sau khi đã kết thúc.");
        if (await db.WorkshopBookings.AnyAsync(b => b.SessionId == sessionId && (b.BookingStatus == "pending" || b.BookingStatus == "confirmed"), ct)) throw new BusinessException(409, "Không thể đóng ca khi còn booking chưa xử lý.");
        s.SessionStatus = "Closed"; await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }

    public async Task CancelBookingAsync(Guid id, Guid? customerId, bool staff, string? reason, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (!staff && customerId is null) throw new BusinessException(401, "Yêu cầu đăng nhập.");
        var b = await db.WorkshopBookings.FirstOrDefaultAsync(x => x.Id == id && (staff || x.CustomerId == customerId), ct) ?? throw new BusinessException(404, "Không tìm thấy booking.");
        if (b.BookingStatus == "cancelled") { await tx.CommitAsync(ct); return; }
        if (b.BookingStatus is "expired" or "attended" or "no_show") throw new BusinessException(409, "Booking không thể hủy ở trạng thái hiện tại.");
        var session = await db.WorkshopSessions.FirstOrDefaultAsync(x => x.Id == b.SessionId, ct) ?? throw new BusinessException(404, "Không tìm thấy ca học.");
        if (session.BookedSlots < b.TicketCount) throw new BusinessException(409, "Số chỗ đã đặt không nhất quán, không thể hủy booking.");
        if (SessionStartUtc(session) <= DateTime.UtcNow) throw new BusinessException(409, "Chỉ được hủy booking trước giờ bắt đầu.");
        if (staff && string.IsNullOrWhiteSpace(reason)) throw new BusinessException(400, "Nhân viên phải nêu lý do hủy booking.");
        b.BookingStatus = "cancelled"; b.CancelReason = reason?.Trim() ?? "";
        session.BookedSlots -= b.TicketCount; SetFullOrOpen(session);
        var paid = await PaidAmountAsync(b.Id, ct);
        var reservedRefunds = (await db.Refunds.Where(x => x.BookingId == b.Id && x.RefundStatus != "Rejected").Select(x => x.RefundAmount).ToListAsync(ct)).Sum();
        var refundable = paid - reservedRefunds;
        if (refundable > 0) db.Refunds.Add(new Refund { BookingId = b.Id, RefundAmount = refundable, RefundReason = "Yêu cầu hoàn do hủy booking", RefundStatus = "Requested", ApprovedBy = "", ProcessedAt = null });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }

    public async Task RecordBookingPaymentAsync(Guid bookingId, ManualPaymentRequest r, Guid userId, bool isStaff, CancellationToken ct = default)
    {
        var paymentMethod = r.PaymentMethod?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(r.TransactionCode) || r.TransactionCode.Trim().Length > 100 || !ManualMethods.Contains(paymentMethod, StringComparer.OrdinalIgnoreCase) || r.Amount <= 0 || r.Amount > 1000000000000m || decimal.Round(r.Amount, 2) != r.Amount)
            throw new BusinessException(400, "Thanh toán không hợp lệ.");

        var code = r.TransactionCode.Trim();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var existing = await db.Payments.FirstOrDefaultAsync(x => x.TransactionCode == code, ct);
        if (existing != null)
        {
            if (existing.BookingId == bookingId && existing.PaymentMethod.Equals(paymentMethod, StringComparison.OrdinalIgnoreCase) && existing.Amount == r.Amount)
            {
                await tx.CommitAsync(ct);
                return;
            }
            throw new BusinessException(409, "Mã giao dịch đã được dùng cho dữ liệu khác.");
        }

        var b = await db.WorkshopBookings.FirstOrDefaultAsync(x => x.Id == bookingId, ct) ?? throw new BusinessException(404, "Không tìm thấy booking.");

        if (!isStaff && b.CustomerId.HasValue && userId != Guid.Empty && b.CustomerId.Value != userId)
            throw new BusinessException(403, "Bạn không có quyền thanh toán booking của tài khoản khác.");

        var s = await db.WorkshopSessions.FirstOrDefaultAsync(x => x.Id == b.SessionId, ct) ?? throw new BusinessException(404, "Không tìm thấy ca học.");
        var w = await db.Workshops.FirstOrDefaultAsync(x => x.Id == s.WorkshopId, ct) ?? throw new BusinessException(404, "Không tìm thấy workshop.");
        if (!w.IsActive || s.SessionStatus is "Cancelled" or "Closed" || b.BookingStatus is "expired" or "cancelled" or "attended" or "no_show" || (b.BookingStatus == "pending" && b.HoldExpiresAt <= DateTime.UtcNow) || SessionStartUtc(s) <= DateTime.UtcNow)
            throw new BusinessException(409, "Booking không còn hiệu lực để thanh toán.");

        var paid = await PaidAmountAsync(b.Id, ct);
        ValidateBookingPayment(b, s, paid, r.Amount);

        if (isStaff)
        {
            db.Payments.Add(new Payment
            {
                BookingId = b.Id,
                TransactionCode = code,
                PaymentType = paid == 0 ? "Deposit" : "Remaining",
                PaymentMethod = paymentMethod,
                Amount = r.Amount,
                PaymentStatus = "Paid",
                PaidAt = DateTime.UtcNow,
                PaymentGatewayResponse = "Manual staff record",
                RecordedBy = userId != Guid.Empty ? userId : b.CustomerId
            });
            ApplyBookingPayment(b, paid, r.Amount);
        }
        else
        {
            if (await db.Payments.AnyAsync(x => x.BookingId == b.Id && x.PaymentStatus == "PendingVerification", ct))
                throw new BusinessException(409, "Booking đã có báo chuyển khoản đang chờ tiệm xác nhận.");
            db.Payments.Add(new Payment
            {
                BookingId = b.Id,
                TransactionCode = code,
                PaymentType = paid == 0 ? "Deposit" : "Remaining",
                PaymentMethod = paymentMethod,
                Amount = r.Amount,
                PaymentStatus = "PendingVerification",
                PaymentGatewayResponse = "Customer reported transfer",
                RecordedBy = b.CustomerId ?? (userId != Guid.Empty ? userId : null)
            });
            var now = DateTime.UtcNow;
            var extended = SessionStartUtc(s).AddHours(-1);
            var hold = extended < now.AddHours(24) ? extended : now.AddHours(24);
            if (hold > b.HoldExpiresAt) b.HoldExpiresAt = hold;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task ConfirmBookingPaymentAsync(Guid bookingId, string transactionCode, Guid staffId, CancellationToken ct = default)
    {
        var code = transactionCode?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(code) || code.Length > 100) throw new BusinessException(400, "Mã giao dịch không hợp lệ.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var b = await db.WorkshopBookings.FirstOrDefaultAsync(x => x.Id == bookingId, ct) ?? throw new BusinessException(404, "Không tìm thấy booking.");
        var payment = await db.Payments.FirstOrDefaultAsync(x => x.BookingId == bookingId && x.TransactionCode == code, ct) ?? throw new BusinessException(404, "Không tìm thấy giao dịch chờ xác nhận.");
        if (payment.PaymentStatus == "Paid") { await tx.CommitAsync(ct); return; }
        if (payment.PaymentStatus != "PendingVerification") throw new BusinessException(409, "Giao dịch không ở trạng thái chờ xác nhận.");
        var s = await db.WorkshopSessions.FirstOrDefaultAsync(x => x.Id == b.SessionId, ct) ?? throw new BusinessException(404, "Không tìm thấy ca học.");
        var w = await db.Workshops.FirstOrDefaultAsync(x => x.Id == s.WorkshopId, ct) ?? throw new BusinessException(404, "Không tìm thấy workshop.");
        if (!w.IsActive || s.SessionStatus is "Cancelled" or "Closed" || b.BookingStatus is "expired" or "cancelled" or "attended" or "no_show" || (b.BookingStatus == "pending" && b.HoldExpiresAt <= DateTime.UtcNow) || SessionStartUtc(s) <= DateTime.UtcNow)
            throw new BusinessException(409, "Booking không còn hiệu lực để thanh toán.");
        var paid = await PaidAmountAsync(b.Id, ct);
        ValidateBookingPayment(b, s, paid, payment.Amount);
        ApplyBookingPayment(b, paid, payment.Amount);
        payment.PaymentStatus = "Paid";
        payment.PaidAt = DateTime.UtcNow;
        payment.RecordedBy = staffId == Guid.Empty ? null : staffId;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task RecordPaymentAsync(Guid? bookingId, Guid? orderId, ManualPaymentRequest r, Guid staffId, CancellationToken ct = default)
    {
        var paymentMethod = r.PaymentMethod?.Trim() ?? "";
        if (staffId == Guid.Empty || (bookingId is null) == (orderId is null) || string.IsNullOrWhiteSpace(r.TransactionCode) || r.TransactionCode.Trim().Length > 100 || !ManualMethods.Contains(paymentMethod, StringComparer.OrdinalIgnoreCase) || r.Amount <= 0 || r.Amount > 1000000000000m || decimal.Round(r.Amount, 2) != r.Amount) throw new BusinessException(400, "Thanh toán thủ công không hợp lệ.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var existing = await db.Payments.FirstOrDefaultAsync(x => x.TransactionCode == r.TransactionCode.Trim(), ct);
        if (existing != null) { if ((bookingId != null && existing.BookingId == bookingId || orderId != null && existing.OrderId == orderId) && existing.PaymentMethod.Equals(paymentMethod, StringComparison.OrdinalIgnoreCase) && existing.Amount == r.Amount) { await tx.CommitAsync(ct); return; } throw new BusinessException(409, "Mã giao dịch đã được dùng cho dữ liệu khác."); }
        if (bookingId is not null)
        {
            var b = await db.WorkshopBookings.FirstOrDefaultAsync(x => x.Id == bookingId, ct) ?? throw new BusinessException(404, "Không tìm thấy booking.");
            var s = await db.WorkshopSessions.FirstOrDefaultAsync(x => x.Id == b.SessionId, ct) ?? throw new BusinessException(404, "Không tìm thấy ca học.");
            var w = await db.Workshops.FirstOrDefaultAsync(x => x.Id == s.WorkshopId, ct) ?? throw new BusinessException(404, "Không tìm thấy workshop.");
            if (!w.IsActive || s.SessionStatus is "Cancelled" or "Closed" || b.BookingStatus is "expired" or "cancelled" or "attended" or "no_show" || (b.BookingStatus == "pending" && b.HoldExpiresAt <= DateTime.UtcNow) || SessionStartUtc(s) <= DateTime.UtcNow) throw new BusinessException(409, "Booking không còn hiệu lực để thanh toán.");
            var paid = await PaidAmountAsync(b.Id, ct);
            ValidateBookingPayment(b, s, paid, r.Amount);
            var payment = new Payment { BookingId = b.Id, TransactionCode = r.TransactionCode.Trim(), PaymentType = paid == 0 ? "Deposit" : "Remaining", PaymentMethod = paymentMethod, Amount = r.Amount, PaymentStatus = "Paid", PaidAt = DateTime.UtcNow, PaymentGatewayResponse = "Manual staff record" };
            payment.RecordedBy = staffId; db.Payments.Add(payment);
            ApplyBookingPayment(b, paid, r.Amount);
        }
        else
        {
            var o = await db.Orders.FirstOrDefaultAsync(x => x.Id == orderId, ct) ?? throw new BusinessException(404, "Không tìm thấy order.");
            if (await db.Set<HoaSap.Domain.GatewayPaymentIntent>().AnyAsync(x => x.OrderId == o.Id && (x.Status == "Pending" || x.Status == "Accepted" || x.Status == "Unknown"), ct)) throw new BusinessException(409, "Order đang có giao dịch gateway hoạt động; không thể ghi nhận thanh toán thủ công.");
            if (o.OrderStatus.Equals("Cancelled", StringComparison.OrdinalIgnoreCase) || o.OrderStatus.Equals("Expired", StringComparison.OrdinalIgnoreCase) || o.PaymentStatus is "Refunded" or "PartiallyRefunded") throw new BusinessException(409, "Order đã hủy, hết hạn hoặc đã hoàn tiền không thể thanh toán.");
            var amounts = await db.Payments.Where(x => x.OrderId == o.Id && x.PaymentStatus == "Paid").Select(x => x.Amount).ToListAsync(ct); var paid = amounts.Sum(); if (paid + r.Amount > o.TotalAmount) throw new BusinessException(400, "Số tiền thanh toán vượt quá tổng order.");
            var quotedDeposit = await (from c in db.Set<HoaSap.Domain.Entities.CustomOrderCheckout>() join rev in db.Set<HoaSap.Domain.Entities.CustomQuotationRevision>() on new { c.CustomQuotationId, c.QuotationVersion } equals new { rev.CustomQuotationId, QuotationVersion = rev.Version } where c.OrderId == o.Id select (decimal?)rev.DepositAmount).SingleOrDefaultAsync(ct);
            if (paid == 0 && quotedDeposit is not null && r.Amount < quotedDeposit.Value) throw new BusinessException(400, "Lần thanh toán đầu phải đủ tiền cọc theo báo giá.");
            var payment = new Payment { OrderId = o.Id, TransactionCode = r.TransactionCode.Trim(), PaymentType = "Full", PaymentMethod = paymentMethod, Amount = r.Amount, PaymentStatus = "Paid", PaidAt = DateTime.UtcNow, PaymentGatewayResponse = "Manual staff record" };
            payment.RecordedBy = staffId; db.Payments.Add(payment); o.PaymentStatus = paid + r.Amount >= o.TotalAmount ? "Paid" : "PartiallyPaid";
        }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }

    public async Task CheckInAsync(Guid id, bool noShow, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var b = await db.WorkshopBookings.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new BusinessException(404, "Không tìm thấy booking.");
        var s = await db.WorkshopSessions.FirstOrDefaultAsync(x => x.Id == b.SessionId, ct) ?? throw new BusinessException(404, "Không tìm thấy ca học.");
        var now = DateTime.UtcNow; var start = SessionStartUtc(s); var end = SessionEndUtc(s);
        if (s.SessionStatus == "Cancelled") throw new BusinessException(409, "Ca học đã hủy không thể điểm danh.");
        if (b.BookingStatus != "confirmed") throw new BusinessException(409, "Chỉ booking đã xác nhận mới được check-in/no-show.");
        if (noShow && now < end) throw new BusinessException(409, "Chỉ được ghi no-show sau giờ kết thúc ca học.");
        if (!noShow && now < start.AddMinutes(-60)) throw new BusinessException(409, "Chỉ check-in từ 60 phút trước giờ bắt đầu.");
        if (!noShow && await PaidAmountAsync(id, ct) < b.TotalAmount) throw new BusinessException(409, "Chỉ booking đã thanh toán đủ mới được check-in.");
        if (noShow) b.BookingStatus = "no_show"; else { b.BookingStatus = "attended"; b.CheckedInAt = now; }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }

    public async Task<TicketDto> TicketAsync(Guid id, Guid? customerId, bool staff, CancellationToken ct = default)
    {
        if (!staff && customerId is null) throw new BusinessException(401, "Yêu cầu đăng nhập.");
        var b = await db.WorkshopBookings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && (staff || x.CustomerId == customerId), ct) ?? throw new BusinessException(404, "Không tìm thấy booking.");
        var s = await db.WorkshopSessions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == b.SessionId, ct) ?? throw new BusinessException(404, "Không tìm thấy ca học.");
        var w = await db.Workshops.AsNoTracking().FirstOrDefaultAsync(x => x.Id == s.WorkshopId, ct) ?? throw new BusinessException(404, "Không tìm thấy workshop.");
        var issued = WorkshopTicketCodec.Issued(b);
        string? qr = null, url = null;
        if (issued)
        {
            var token = Tickets.Create(b.Id);
            url = Tickets.CheckInUrl(token);
            qr = Convert.ToBase64String(Tickets.QrPng(url));
        }
        return new TicketDto(b.Id, b.BookingCode, b.CustomerName, b.TicketCount, w.Name, SessionStartUtc(s), SessionEndUtc(s), s.Location, b.BookingStatus, Utc(b.CheckedInAt), issued, qr, url);
    }

    public async Task<TicketScanDto> ScanAsync(string token, CancellationToken ct = default)
    {
        if (!Tickets.TryRead(token, out var id)) throw new BusinessException(404, "Vé không hợp lệ.");
        var b = await db.WorkshopBookings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new BusinessException(404, "Vé không hợp lệ.");
        var s = await db.WorkshopSessions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == b.SessionId, ct) ?? throw new BusinessException(404, "Không tìm thấy ca học.");
        var w = await db.Workshops.AsNoTracking().FirstOrDefaultAsync(x => x.Id == s.WorkshopId, ct) ?? throw new BusinessException(404, "Không tìm thấy workshop.");
        var fullyPaid = await PaidAmountAsync(b.Id, ct) >= b.TotalAmount;
        var reason = ScanReason(b, s, fullyPaid);
        return new TicketScanDto(b.Id, b.BookingCode, b.CustomerName, b.TicketCount, w.Name, s.Id, SessionStartUtc(s), SessionEndUtc(s), s.Location, b.BookingStatus, Utc(b.CheckedInAt), fullyPaid, reason is null, reason);
    }

    public async Task<TicketScanDto> CheckInByTokenAsync(string token, CancellationToken ct = default)
    {
        if (!Tickets.TryRead(token, out var id)) throw new BusinessException(404, "Vé không hợp lệ.");
        var b = await db.WorkshopBookings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new BusinessException(404, "Vé không hợp lệ.");
        if (b.BookingStatus == "attended" || b.CheckedInAt is not null)
        {
            var at = Utc(b.CheckedInAt ?? DateTime.UtcNow);
            var vn = TimeZoneInfo.ConvertTimeFromUtc(at, VietnamTimeZone);
            throw new BusinessException(409, $"Vé đã check-in lúc {vn:HH:mm dd/MM} (giờ VN).");
        }
        await CheckInAsync(id, false, ct);
        return await ScanAsync(token, ct);
    }

    public async Task RefundAsync(Guid id, RefundRequest r, Guid staffId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var booking = await db.WorkshopBookings.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new BusinessException(404, "Không tìm thấy booking.");
        var paid = await PaidAmountAsync(id, ct); var refunded = (await db.Refunds.Where(x => x.BookingId == id && x.RefundStatus != "Rejected").Select(x => x.RefundAmount).ToListAsync(ct)).Sum();
        if (r.Amount <= 0 || r.Amount > paid - refunded) throw new BusinessException(400, "Số tiền hoàn vượt quá số tiền đã thanh toán còn lại.");
        if (booking.BookingStatus != "cancelled") throw new BusinessException(409, "Chỉ booking đã hủy mới được hoàn tiền.");
        var duplicate = await db.Refunds.AnyAsync(x => x.BookingId == id && x.RefundStatus != "Rejected" && x.RefundAmount == r.Amount && x.RefundReason == r.Reason.Trim(), ct);
        if (duplicate) { await tx.CommitAsync(ct); return; }
        db.Refunds.Add(new Refund { BookingId = id, RefundAmount = r.Amount, RefundReason = r.Reason.Trim(), RefundStatus = "Requested", ApprovedBy = "" }); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }

    private async Task<decimal> PaidAmountAsync(Guid bookingId, CancellationToken ct) => (await db.Payments.Where(x => x.BookingId == bookingId && x.PaymentStatus == "Paid").Select(x => x.Amount).ToListAsync(ct)).Sum();
    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
    private static DateTime? Utc(DateTime? value) => value is { } v ? Utc(v) : null;
    private static DateTime SessionStartUtc(WorkshopSession s) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(s.SessionDate.Date.Add(s.StartTime), DateTimeKind.Unspecified), VietnamTimeZone);
    private static DateTime SessionEndUtc(WorkshopSession s) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(s.SessionDate.Date.Add(s.EndTime), DateTimeKind.Unspecified), VietnamTimeZone);
    private static string Fingerprint(Guid sessionId, Guid? customerId, BookingRequest r) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        sessionId, customerId, r.TicketCount,
        customerName = r.CustomerName.Trim(), customerPhone = r.CustomerPhone.Trim(), customerEmail = r.CustomerEmail?.Trim() ?? ""
    })))).ToLowerInvariant();
    private static void SetFullOrOpen(WorkshopSession s) { if (s.SessionStatus is "Cancelled" or "Closed") return; s.SessionStatus = s.BookedSlots >= s.Capacity ? "Full" : "Open"; }
    private async Task ValidateWorkshopAsync(WorkshopUpsertDto r, CancellationToken ct)
    {
        ValidateWorkshop(r); ValidateArrayJson(r.GoalsJson); ValidateArrayJson(r.IncludesJson);
        if (r.InstructorId is not null && !await db.Instructors.AnyAsync(x => x.Id == r.InstructorId, ct)) throw new BusinessException(400, "Instructor không tồn tại.");
    }
    private static void ValidateWorkshop(WorkshopUpsertDto r) { if (string.IsNullOrWhiteSpace(r.Code) || string.IsNullOrWhiteSpace(r.Name) || r.BasePrice < 0 || r.BasePrice > 1_000_000_000m || decimal.Round(r.BasePrice, 2) != r.BasePrice || r.Duration < 1 || r.Duration > 100_000 || r.MaxCapacity < 1 || r.MaxCapacity > 100_000) throw new BusinessException(400, "Thông tin workshop không hợp lệ."); }
    private static void ValidateSession(SessionUpsertDto r) { if (r.Capacity is < 1 or > 100000 || r.SessionDate == default || r.StartTime < TimeSpan.Zero || r.EndTime >= TimeSpan.FromDays(1) || r.EndTime <= r.StartTime || string.IsNullOrWhiteSpace(r.Location)) throw new BusinessException(400, "Thông tin ca học không hợp lệ."); }
    private static void ValidateArrayJson(string? json) { if (string.IsNullOrWhiteSpace(json)) return; try { using var doc = JsonDocument.Parse(json); if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new Exception(); } catch { throw new BusinessException(400, "GoalsJson và IncludesJson phải là JSON array hợp lệ."); } }
    private static string NormalizeArrayJson(string? json) => string.IsNullOrWhiteSpace(json) ? "[]" : json;
    private static WorkshopDto ToDto(Workshop x) => new(x.Id, x.Code, x.Name, x.ShortName, x.Category, x.BasePrice, x.Duration, x.MaxCapacity, x.Description, x.LeadContent, x.GoalsJson, x.IncludesJson, x.ForWho, x.IsFeatured, x.IsActive, x.InstructorId);
    private static BookingDto ToDto(WorkshopBooking x, decimal pendingAmount, string? pendingCode) => new(x.Id, x.SessionId, x.BookingCode, x.CustomerName, x.TicketCount, x.TotalAmount, x.DepositAmount, x.RemainingAmount, x.BookingStatus, Utc(x.HoldExpiresAt), Utc(x.CheckedInAt), Utc(x.CreatedAt), pendingAmount, pendingCode);
    private async Task<BookingDto> ToDtoAsync(WorkshopBooking x, CancellationToken ct)
    {
        var pending = await db.Payments.AsNoTracking().Where(p => p.BookingId == x.Id && p.PaymentStatus == "PendingVerification").Select(p => new { p.Amount, p.TransactionCode }).FirstOrDefaultAsync(ct);
        return ToDto(x, pending?.Amount ?? 0, pending?.TransactionCode);
    }
    private async Task<IReadOnlyList<BookingDto>> ToDtosAsync(List<WorkshopBooking> list, CancellationToken ct)
    {
        if (list.Count == 0) return [];
        var ids = list.Select(x => x.Id).ToList();
        var pending = await db.Payments.AsNoTracking().Where(p => p.BookingId != null && ids.Contains(p.BookingId.Value) && p.PaymentStatus == "PendingVerification").Select(p => new { BookingId = p.BookingId!.Value, p.Amount, p.TransactionCode }).ToListAsync(ct);
        var byBooking = pending.GroupBy(x => x.BookingId).ToDictionary(g => g.Key, g => g.First());
        return list.Select(x => byBooking.TryGetValue(x.Id, out var p) ? ToDto(x, p.Amount, p.TransactionCode) : ToDto(x, 0, null)).ToList();
    }
    private static void ValidateBookingPayment(WorkshopBooking b, WorkshopSession s, decimal paid, decimal amount)
    {
        var after = paid + amount;
        if (paid == 0 && amount < b.DepositAmount) throw new BusinessException(400, "Lần thanh toán đầu phải đủ tiền cọc tối thiểu.");
        if (paid > 0 && DateTime.UtcNow >= SessionStartUtc(s).AddHours(-24)) throw new BusinessException(409, "Đã quá hạn thanh toán phần còn lại trước buổi học 24 giờ; cần nhân viên xử lý ngoại lệ ngoài hệ thống.");
        if (paid == 0 && DateTime.UtcNow >= SessionStartUtc(s).AddHours(-24) && amount < b.TotalAmount) throw new BusinessException(409, "Booking trong 24 giờ trước buổi học phải thanh toán đủ ngay lần đầu.");
        if (after > b.TotalAmount) throw new BusinessException(400, "Số tiền thanh toán vượt quá tổng booking.");
    }
    private static void ApplyBookingPayment(WorkshopBooking b, decimal paid, decimal amount)
    {
        var after = paid + amount;
        b.RemainingAmount = Math.Max(0, b.TotalAmount - after);
        if (after >= b.DepositAmount) b.BookingStatus = "confirmed";
    }
    private static string? ScanReason(WorkshopBooking b, WorkshopSession s, bool fullyPaid)
    {
        var now = DateTime.UtcNow;
        if (s.SessionStatus == "Cancelled") return "Ca học đã hủy";
        if (b.BookingStatus == "attended" || b.CheckedInAt is not null) return "Vé đã check-in";
        if (b.BookingStatus == "cancelled") return "Booking đã hủy";
        if (b.BookingStatus == "expired") return "Booking đã hết hạn";
        if (b.BookingStatus == "no_show") return "Ca học đã kết thúc";
        if (!fullyPaid || b.BookingStatus != "confirmed") return "Chưa thanh toán đủ";
        if (now > SessionEndUtc(s)) return "Ca học đã kết thúc";
        if (now < SessionStartUtc(s).AddMinutes(-60)) return "Chưa đến giờ check-in";
        return null;
    }
    private static SessionDto ToDto(WorkshopSession x) => new(x.Id, x.WorkshopId, x.SessionDate, x.StartTime, x.EndTime, x.Location, x.Capacity, x.BookedSlots, x.SessionStatus);
}
