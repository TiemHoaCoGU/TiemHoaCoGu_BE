using System.Security.Claims;
using HoaSap.BLL.Workshops;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace HoaSap.API.Controllers;

[ApiController, Route("api/payments")]
public sealed class PaymentsController(WorkshopService service) : ControllerBase
{
    [HttpPost("booking/{bookingId:guid}"), Authorize]
    public async Task<IActionResult> Booking(Guid bookingId, ManualPaymentRequest r, CancellationToken ct)
    {
        var isStaff = User.IsInRole("Admin") || User.IsInRole("Staff");
        var userId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;
        await service.RecordBookingPaymentAsync(bookingId, r, userId, isStaff, ct);
        return Ok(new { recorded = true });
    }

    [HttpPost("booking/{bookingId:guid}/confirm"), Authorize(Roles = "Admin,Staff")]
    public async Task<IActionResult> ConfirmBooking(Guid bookingId, ConfirmPaymentRequest r, CancellationToken ct)
    {
        await service.ConfirmBookingPaymentAsync(bookingId, r.TransactionCode, StaffId(), ct);
        return Ok(new { confirmed = true });
    }

    [HttpPost("order/{orderId:guid}"), Authorize(Roles = "Admin,Staff")]
    public async Task<IActionResult> Order(Guid orderId, ManualPaymentRequest r, CancellationToken ct)
    {
        await service.RecordPaymentAsync(null, orderId, r, StaffId(), ct);
        return Ok(new { recorded = true });
    }

    private Guid StaffId() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : throw new HoaSap.BLL.BusinessException(401, "Yêu cầu đăng nhập staff.");
}
