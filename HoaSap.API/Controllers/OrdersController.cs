using System.Security.Claims;
using HoaSap.BLL.Commerce;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace HoaSap.API.Controllers;

[ApiController, Route("api/orders")]
public sealed class OrdersController(CommerceService service) : ControllerBase
{
    Guid? Me() { var x = User.FindFirstValue(ClaimTypes.NameIdentifier); return Guid.TryParse(x, out var id) ? id : null; }
    [HttpPost("checkout")] public Task<OrderDto> Checkout(CheckoutRequest r, CancellationToken ct) => service.CheckoutAsync(Me(), r, ct);
    [HttpGet, Authorize] public Task<IReadOnlyList<OrderDto>> Mine(CancellationToken ct) => service.OwnOrdersAsync(Me() ?? throw new UnauthorizedAccessException(), ct);
    [HttpGet("{id:guid}")][Authorize] public Task<OrderDto> Get(Guid id, CancellationToken ct) => service.OwnOrderAsync(Me() ?? throw new UnauthorizedAccessException(), id, ct);
    [HttpGet("{id:guid}/deliveries")][Authorize] public Task<IReadOnlyList<DeliveryDto>> Deliveries(Guid id, CancellationToken ct) => service.DeliveriesAsync(Me() ?? throw new UnauthorizedAccessException(), id, ct);
    [HttpGet("staff/{id:guid}/deliveries"), Authorize(Roles = "Admin,Staff")] public Task<IReadOnlyList<DeliveryDto>> StaffDeliveries(Guid id, CancellationToken ct) => service.StaffDeliveriesAsync(id, ct);
    [HttpPost("{id:guid}/cancel")][Authorize] public async Task<IActionResult> Cancel(Guid id, CancellationToken ct) { await service.CancelAsync(Me() ?? throw new UnauthorizedAccessException(), id, ct); return NoContent(); }
    [HttpGet("staff"), Authorize(Roles = "Admin,Staff")] public Task<IReadOnlyList<OrderDto>> Staff(CancellationToken ct) => service.StaffOrdersAsync(ct);
    [HttpPost("{id:guid}/status"), Authorize(Roles = "Admin,Staff")] public async Task<IActionResult> Status(Guid id, StatusRequest r, CancellationToken ct) { await service.TransitionAsync(id, r.Status, ct); return NoContent(); }
    [HttpPost("{id:guid}/deliveries"), Authorize(Roles = "Admin,Staff")] public Task<DeliveryDto> Delivery(Guid id, DeliveryRequest r, CancellationToken ct) => service.DeliveryAsync(id, r, ct);
    [HttpPost("{id:guid}/deliveries/{deliveryId:guid}/status"), Authorize(Roles = "Admin,Staff")] public Task<DeliveryDto> DeliveryStatus(Guid id, Guid deliveryId, StatusRequest r, CancellationToken ct) => service.TransitionDeliveryAsync(id, deliveryId, r.Status, ct);
    [HttpGet("{id:guid}/deliveries/{deliveryId:guid}/tracking"), Authorize] public Task<CarrierTrackingResult> Tracking(Guid id, Guid deliveryId, CancellationToken ct) => service.TrackDeliveryAsync(id, deliveryId, Me(), User.IsInRole("Admin") || User.IsInRole("Staff"), ct);
}
