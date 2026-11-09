using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using HoaSap.DAL;
using HoaSap.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HoaSap.BLL.Workshops;

public sealed class VnPayBookingService(HoaSapDbContext db, VnPayClient vnPay, IConfiguration config, ILogger<VnPayBookingService> logger)
{
    private static readonly TimeZoneInfo VietnamTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
    private const string Method = "VNPay";
    private string PublicUrl => (config["Site:PublicUrl"] ?? "http://localhost:3000").Trim().TrimEnd('/');

    public async Task<VnPayCheckoutDto> StartAsync(Guid bookingId, Guid? customerId, CancellationToken ct = default)
    {
        if (!vnPay.Enabled) throw new BusinessException(503, "Chưa bật thanh toán VNPAY.");
        if (customerId is null || customerId == Guid.Empty) throw new BusinessException(401, "Yêu cầu đăng nhập.");

        string txnRef = ""; int amount = 0; DateTime hold = default; DateTime created = default; string orderInfo = ""; string createDate = ""; Guid paymentId = Guid.Empty;
        await using (var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct))
        {
            var b = await db.WorkshopBookings.FirstOrDefaultAsync(x => x.Id == bookingId, ct) ?? throw new BusinessException(404, "Không tìm thấy booking.");
            if (b.CustomerId != customerId) throw new BusinessException(404, "Không tìm thấy booking.");
            var s = await db.WorkshopSessions.FirstOrDefaultAsync(x => x.Id == b.SessionId, ct) ?? throw new BusinessException(404, "Không tìm thấy ca học.");
            var w = await db.Workshops.FirstOrDefaultAsync(x => x.Id == s.WorkshopId, ct) ?? throw new BusinessException(404, "Không tìm thấy workshop.");
            if (!w.IsActive || s.SessionStatus is "Cancelled" or "Closed" || b.BookingStatus is "expired" or "cancelled" or "attended" or "no_show" || (b.BookingStatus == "pending" && b.HoldExpiresAt <= DateTime.UtcNow) || SessionStartUtc(s) <= DateTime.UtcNow)
                throw new BusinessException(409, "Booking không còn hiệu lực để thanh toán.");
            if (b.BookingStatus != "pending") throw new BusinessException(409, "Booking không còn ở trạng thái chờ thanh toán.");

            var paid = await PaidAmountAsync(b.Id, ct);
            var remaining = Math.Max(0, b.TotalAmount - paid);
            amount = checked((int)decimal.Round(remaining, 0));
            if (amount < 5000 || amount > 1_000_000_000) throw new BusinessException(400, "Số tiền thanh toán VNPAY không hợp lệ.");

            var existing = await db.Payments.Where(x => x.BookingId == b.Id && x.PaymentMethod == Method && x.PaymentStatus == "AwaitingPayment").OrderByDescending(x => x.PaidAt).ToListAsync(ct);
            foreach (var p in existing)
            {
                var info = ReadInfo(p.PaymentGatewayResponse);
                if (info is not null && info.Amount == amount && info.ExpiresAtUtc > DateTime.UtcNow.AddSeconds(60) && !string.IsNullOrWhiteSpace(info.PaymentUrl))
                {
                    await tx.CommitAsync(ct);
                    return new VnPayCheckoutDto(b.Id, info.TxnRef, info.Amount, info.PaymentUrl, Utc(info.ExpiresAtUtc));
                }
            }
            foreach (var p in existing) p.PaymentStatus = "Canceled";

            var now = DateTime.UtcNow;
            created = now;
            hold = now.AddMinutes(15);
            var maxHold = SessionStartUtc(s).AddHours(-1);
            if (hold > maxHold) hold = maxHold;
            if (hold > b.HoldExpiresAt) b.HoldExpiresAt = hold;
            if (hold <= now) throw new BusinessException(409, "Booking không còn đủ thời gian để thanh toán tự động.");

            txnRef = NewTxnRef();
            var codePart = b.BookingCode.Length > 4 ? b.BookingCode[^4..] : b.BookingCode;
            orderInfo = AsciiOrderInfo($"Thanh toan ve WS-{codePart}");
            createDate = Gmt7(now);

            var payment = new Payment { BookingId = b.Id, TransactionCode = $"VNP-{txnRef}", PaymentType = "Full", PaymentMethod = Method, Amount = amount, PaymentStatus = "AwaitingPayment", RecordedBy = b.CustomerId, PaymentGatewayResponse = JsonSerializer.Serialize(new { txnRef, createDate, orderInfo, expiresAtUtc = hold, amount }) };
            db.Payments.Add(payment);
            await db.SaveChangesAsync(ct);
            paymentId = payment.Id;
            await tx.CommitAsync(ct);
        }

        var ret = $"{PublicUrl}/thanh-toan?bookingId={bookingId}";
        try
        {
            var url = vnPay.BuildPaymentUrl(txnRef, amount, orderInfo, ret, "127.0.0.1", created, hold);
            var payment = await db.Payments.FirstAsync(x => x.Id == paymentId, ct);
            payment.PaymentGatewayResponse = JsonSerializer.Serialize(new { txnRef, createDate, orderInfo, paymentUrl = url, expiresAtUtc = hold, amount });
            await db.SaveChangesAsync(ct);
            return new VnPayCheckoutDto(bookingId, txnRef, amount, url, Utc(hold));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "VNPAY create link failed for booking {BookingId}", bookingId);
            var payment = await db.Payments.FirstOrDefaultAsync(x => x.Id == paymentId, ct);
            if (payment is not null && payment.PaymentStatus == "AwaitingPayment") { payment.PaymentStatus = "Failed"; await db.SaveChangesAsync(ct); }
            throw new BusinessException(502, "Không tạo được mã thanh toán VNPAY, thử lại sau.");
        }
    }

    public async Task<VnPaySyncDto> ReturnAsync(Guid bookingId, Guid? userId, bool staff, string? rawQuery, CancellationToken ct = default)
    {
        await EnsureAccessAsync(bookingId, userId, staff, ct);
        var parsed = VnPayClient.ParseQuery(rawQuery);
        if (!parsed.Any(x => x.Key.StartsWith("vnp_", StringComparison.Ordinal))) throw new BusinessException(400, "Thiếu dữ liệu thanh toán VNPAY.");
        if (!vnPay.VerifyReturn(parsed, out var result)) throw new BusinessException(400, "Chữ ký VNPAY không hợp lệ.");
        var payment = await db.Payments.AsNoTracking().FirstOrDefaultAsync(x => x.BookingId == bookingId && x.TransactionCode == $"VNP-{result.TxnRef}" && x.PaymentMethod == Method, ct);
        if (payment is not null) await ApplyResultAsync(payment.Id, result, ct);
        return await SyncSnapshotAsync(bookingId, ct);
    }

    public async Task<(string RspCode, string Message)> IpnAsync(string? rawQuery, CancellationToken ct = default)
    {
        try
        {
            var parsed = VnPayClient.ParseQuery(rawQuery);
            if (!vnPay.VerifyReturn(parsed, out var result)) return ("97", "Invalid signature");
            if (!string.Equals(result.TmnCode, vnPay.TmnCode, StringComparison.Ordinal)) return ("97", "Invalid signature");
            var payment = await db.Payments.AsNoTracking().FirstOrDefaultAsync(x => x.TransactionCode == $"VNP-{result.TxnRef}" && x.PaymentMethod == Method, ct);
            if (payment is null) return ("01", "Order not found");
            var info = ReadInfo(payment.PaymentGatewayResponse);
            if (info is not null && info.Amount > 0 && result.Amount != (long)info.Amount * 100) return ("04", "Invalid amount");
            var outcome = await ApplyResultAsync(payment.Id, result, ct);
            return outcome switch { ApplyOutcome.AlreadyConfirmed => ("02", "Order already confirmed"), ApplyOutcome.Confirmed => ("00", "Confirm Success"), _ => ("00", "Confirm Success") };
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "VNPAY IPN handling failed");
            return ("99", "Unknown error");
        }
    }

    public async Task<VnPaySyncDto> SyncBookingAsync(Guid bookingId, Guid? userId, bool staff, CancellationToken ct = default)
    {
        await EnsureAccessAsync(bookingId, userId, staff, ct);
        var awaiting = await db.Payments.AsNoTracking().Where(x => x.BookingId == bookingId && x.PaymentMethod == Method && x.PaymentStatus == "AwaitingPayment").Select(x => x.Id).ToListAsync(ct);
        foreach (var id in awaiting) await QueryAndApplyAsync(id, ct);
        return await SyncSnapshotAsync(bookingId, ct);
    }

    public async Task PollAsync(CancellationToken ct)
    {
        if (!vnPay.Enabled) return;
        var cutoff = DateTime.UtcNow.AddHours(-2);
        var candidates = await db.Payments.AsNoTracking().Where(x => x.PaymentMethod == Method && x.PaymentStatus == "AwaitingPayment").OrderBy(x => x.Id).Take(200).ToListAsync(ct);
        var ids = new List<Guid>();
        foreach (var p in candidates)
        {
            var info = ReadInfo(p.PaymentGatewayResponse);
            if (info is not null && info.ExpiresAtUtc > cutoff) ids.Add(p.Id);
            if (ids.Count >= 20) break;
        }
        foreach (var id in ids)
        {
            try { await QueryAndApplyAsync(id, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { logger.LogWarning(ex, "VNPAY poll sync failed for payment {PaymentId}", id); }
        }
    }

    private enum ApplyOutcome { Confirmed, AlreadyConfirmed, Canceled, Reconciliation, Ignored }

    private async Task<ApplyOutcome> QueryAndApplyAsync(Guid paymentId, CancellationToken ct)
    {
        var snap = await db.Payments.AsNoTracking().Where(x => x.Id == paymentId).Select(x => new { x.TransactionCode, x.PaymentGatewayResponse }).FirstOrDefaultAsync(ct);
        if (snap is null || !snap.TransactionCode.StartsWith("VNP-", StringComparison.Ordinal)) return ApplyOutcome.Ignored;
        var info = ReadInfo(snap.PaymentGatewayResponse);
        if (info is null || string.IsNullOrWhiteSpace(info.CreateDate) || string.IsNullOrWhiteSpace(info.OrderInfo)) return ApplyOutcome.Ignored;
        VnPayQueryResult qr;
        try { qr = await vnPay.QueryAsync(info.TxnRef, info.CreateDate, info.OrderInfo, "127.0.0.1", ct); }
        catch (Exception ex) when (ex is VnPayException or HttpRequestException or TaskCanceledException) { logger.LogWarning(ex, "VNPAY query failed for {TransactionCode}", snap.TransactionCode); return ApplyOutcome.Ignored; }
        if (!qr.HasSecureHash || (qr.ResponseCode != "00" && qr.ResponseCode != "91"))
        {
            logger.LogInformation("VNPAY query unknown/try later for {TransactionCode}: responseCode={ResponseCode}, hasHash={HasHash}", snap.TransactionCode, qr.ResponseCode, qr.HasSecureHash);
            return ApplyOutcome.Ignored;
        }
        if (!qr.SignatureValid) { logger.LogWarning("VNPAY query signature mismatch for {TransactionCode}", snap.TransactionCode); return ApplyOutcome.Ignored; }
        if (!qr.Ok && qr.ResponseCode == "91") return ApplyOutcome.Ignored;
        var result = new VnPayResult(vnPay.TmnCode, info.TxnRef, qr.Amount, qr.ResponseCode, qr.TransactionStatus, qr.TransactionNo, qr.BankCode, true, qr.RawJson);
        return await ApplyResultAsync(paymentId, result, ct);
    }

    private async Task<ApplyOutcome> ApplyResultAsync(Guid paymentId, VnPayResult result, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var payment = await db.Payments.FirstOrDefaultAsync(x => x.Id == paymentId, ct);
        if (payment is null) { await tx.CommitAsync(ct); return ApplyOutcome.Ignored; }
        if (payment.PaymentStatus == "Paid") { await tx.CommitAsync(ct); return ApplyOutcome.AlreadyConfirmed; }
        if (payment.PaymentStatus != "AwaitingPayment") { await tx.CommitAsync(ct); return ApplyOutcome.Ignored; }
        var info = ReadInfo(payment.PaymentGatewayResponse);
        var b = payment.BookingId is null ? null : await db.WorkshopBookings.FirstOrDefaultAsync(x => x.Id == payment.BookingId, ct);
        if (b is null || info is null) { await tx.CommitAsync(ct); return ApplyOutcome.Ignored; }

        if (result.ResponseCode == "00" && result.TransactionStatus == "00")
        {
            if (result.Amount != (long)info.Amount * 100) { await tx.CommitAsync(ct); return ApplyOutcome.Ignored; }
            if (b.BookingStatus is "expired" or "cancelled") { payment.PaymentStatus = "ReconciliationRequired"; await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return ApplyOutcome.Reconciliation; }
            var s = await db.WorkshopSessions.FirstOrDefaultAsync(x => x.Id == b.SessionId, ct) ?? throw new BusinessException(404, "Không tìm thấy ca học.");
            var w = await db.Workshops.FirstOrDefaultAsync(x => x.Id == s.WorkshopId, ct) ?? throw new BusinessException(404, "Không tìm thấy workshop.");
            if (!w.IsActive || s.SessionStatus is "Cancelled" or "Closed" || b.BookingStatus is "attended" or "no_show" || SessionStartUtc(s) <= DateTime.UtcNow)
            { payment.PaymentStatus = "ReconciliationRequired"; await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return ApplyOutcome.Reconciliation; }
            var paid = await PaidAmountAsync(b.Id, ct);
            WorkshopService.ApplyBookingPayment(b, paid, payment.Amount);
            payment.PaymentStatus = "Paid";
            payment.PaidAt = DateTime.UtcNow;
            payment.RecordedBy ??= b.CustomerId;
            payment.PaymentGatewayResponse = JsonSerializer.Serialize(new { txnRef = info.TxnRef, createDate = info.CreateDate, orderInfo = info.OrderInfo, paymentUrl = info.PaymentUrl, expiresAtUtc = info.ExpiresAtUtc, amount = info.Amount, transactionNo = result.TransactionNo, bankCode = result.BankCode, responseCode = result.ResponseCode });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return ApplyOutcome.Confirmed;
        }
        if (result.ResponseCode != "00")
        {
            payment.PaymentStatus = "Canceled";
            payment.PaymentGatewayResponse = JsonSerializer.Serialize(new { txnRef = info.TxnRef, createDate = info.CreateDate, orderInfo = info.OrderInfo, paymentUrl = info.PaymentUrl, expiresAtUtc = info.ExpiresAtUtc, amount = info.Amount, responseCode = result.ResponseCode });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return ApplyOutcome.Canceled;
        }
        await tx.CommitAsync(ct);
        return ApplyOutcome.Ignored;
    }

    private async Task EnsureAccessAsync(Guid bookingId, Guid? userId, bool staff, CancellationToken ct)
    {
        if (staff) return;
        if (userId is null || userId == Guid.Empty) throw new BusinessException(401, "Yêu cầu đăng nhập.");
        var owner = await db.WorkshopBookings.AsNoTracking().Where(x => x.Id == bookingId).Select(x => x.CustomerId).FirstOrDefaultAsync(ct);
        if (owner is null) throw new BusinessException(404, "Không tìm thấy booking.");
        if (owner != userId) throw new BusinessException(404, "Không tìm thấy booking.");
    }

    private async Task<VnPaySyncDto> SyncSnapshotAsync(Guid bookingId, CancellationToken ct)
    {
        var b = await db.WorkshopBookings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == bookingId, ct) ?? throw new BusinessException(404, "Không tìm thấy booking.");
        var paid = await PaidAmountAsync(b.Id, ct);
        var awaitingAmount = (await db.Payments.AsNoTracking().Where(x => x.BookingId == bookingId && x.PaymentMethod == Method && x.PaymentStatus == "AwaitingPayment").Select(x => x.Amount).ToListAsync(ct)).Sum();
        var last = await db.Payments.AsNoTracking().Where(x => x.BookingId == bookingId && x.PaymentMethod == Method).OrderByDescending(x => x.Id).Select(x => x.PaymentStatus).FirstOrDefaultAsync(ct);
        var lastResult = last switch { "Paid" => "paid", "Canceled" or "Failed" or "Expired" => "canceled", "AwaitingPayment" => "pending", _ => "pending" };
        return new VnPaySyncDto(b.BookingStatus, Math.Max(0, b.TotalAmount - paid), paid >= b.TotalAmount && b.RemainingAmount == 0, awaitingAmount, lastResult);
    }

    private async Task<decimal> PaidAmountAsync(Guid bookingId, CancellationToken ct) => (await db.Payments.Where(x => x.BookingId == bookingId && x.PaymentStatus == "Paid").Select(x => x.Amount).ToListAsync(ct)).Sum();
    private static DateTime SessionStartUtc(WorkshopSession s) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(s.SessionDate.Date.Add(s.StartTime), DateTimeKind.Unspecified), VietnamTimeZone);
    private static DateTime Utc(DateTime v) => v.Kind == DateTimeKind.Utc ? v : DateTime.SpecifyKind(v, DateTimeKind.Utc);
    private static string Gmt7(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), VietnamTimeZone).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
    private static string NewTxnRef() { Span<byte> b = stackalloc byte[10]; RandomNumberGenerator.Fill(b); var s = Convert.ToHexString(b); return "B" + s[..15]; }
    private static string AsciiOrderInfo(string value) { var chars = value.Normalize(System.Text.NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).Select(c => c <= 127 ? c : ' ').ToArray(); return new string(chars).Replace('đ', 'd').Replace('Đ', 'D'); }

    private sealed record LinkInfo(string TxnRef, int Amount, string CreateDate, string OrderInfo, string PaymentUrl, DateTime ExpiresAtUtc);
    private static LinkInfo? ReadInfo(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var refv = Str(r, "txnRef"); if (string.IsNullOrWhiteSpace(refv)) return null;
            var amount = r.TryGetProperty("amount", out var a) && a.TryGetInt32(out var av) ? av : 0;
            var exp = r.TryGetProperty("expiresAtUtc", out var e) && e.ValueKind == JsonValueKind.String && DateTime.TryParse(e.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt) ? dt : default;
            return new LinkInfo(refv, amount, Str(r, "createDate"), Str(r, "orderInfo"), Str(r, "paymentUrl"), exp.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(exp, DateTimeKind.Utc) : exp.ToUniversalTime());
        }
        catch { return null; }
    }
    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() ?? v.ToString() : "";
}
