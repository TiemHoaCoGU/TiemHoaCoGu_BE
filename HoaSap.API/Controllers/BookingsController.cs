using System.Security.Claims;
using HoaSap.BLL.Workshops;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace HoaSap.API.Controllers;

[ApiController, Route("api/bookings")]
public sealed class BookingsController(WorkshopService service, VnPayBookingService vnPay) : ControllerBase
{
    private Guid? UserId => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
    private bool Staff => User.IsInRole("Admin") || User.IsInRole("Staff");
    [HttpPost("sessions/{sessionId:guid}")] public Task<BookingDto> Hold(Guid sessionId, BookingRequest r, CancellationToken ct) => service.HoldBookingAsync(sessionId, r, UserId, ct);
    [HttpGet][Authorize] public Task<IReadOnlyList<BookingDto>> Mine(CancellationToken ct) => service.MyBookingsAsync(UserId ?? throw new HoaSap.BLL.BusinessException(401, "Yêu cầu đăng nhập."), ct);
    [HttpGet("{id:guid}")][Authorize] public Task<BookingDto> Get(Guid id, CancellationToken ct) => service.BookingAsync(id, UserId, Staff, ct);
    [HttpPost("{id:guid}/vnpay")][Authorize] public Task<VnPayCheckoutDto> VnPayStart(Guid id, CancellationToken ct) => vnPay.StartAsync(id, UserId, ct);
    [HttpPost("{id:guid}/vnpay/return")][Authorize] public Task<VnPaySyncDto> VnPayReturn(Guid id, VnPayReturnRequest r, CancellationToken ct) => vnPay.ReturnAsync(id, UserId, Staff, r.Query, ct);
    [HttpPost("{id:guid}/vnpay/sync")][Authorize] public Task<VnPaySyncDto> VnPaySync(Guid id, CancellationToken ct) => vnPay.SyncBookingAsync(id, UserId, Staff, ct);
    [HttpGet("{id:guid}/ticket")][Authorize] public Task<TicketDto> Ticket(Guid id, CancellationToken ct) => service.TicketAsync(id, UserId, Staff, ct);
    [HttpGet("tickets/{token}"), Authorize(Roles = "Admin,Staff")] public Task<TicketScanDto> Scan(string token, CancellationToken ct) => service.ScanAsync(token, ct);
    [HttpPost("tickets/{token}/check-in"), Authorize(Roles = "Admin,Staff")] public Task<TicketScanDto> CheckInByToken(string token, CancellationToken ct) => service.CheckInByTokenAsync(token, ct);
    [HttpPost("{id:guid}/cancel")][Authorize] public Task Cancel(Guid id, [FromBody] string? reason, CancellationToken ct) => service.CancelBookingAsync(id, UserId, Staff, reason, ct);
    [HttpPost("{id:guid}/check-in"), Authorize(Roles = "Admin,Staff")] public Task CheckIn(Guid id, CancellationToken ct) => service.CheckInAsync(id, false, ct);
    [HttpPost("{id:guid}/no-show"), Authorize(Roles = "Admin,Staff")] public Task NoShow(Guid id, CancellationToken ct) => service.CheckInAsync(id, true, ct);
    [HttpPost("{id:guid}/refund"), Authorize(Roles = "Admin,Staff")] public Task Refund(Guid id, RefundRequest r, CancellationToken ct) => service.RefundAsync(id, r, UserId ?? Guid.Empty, ct);
}
