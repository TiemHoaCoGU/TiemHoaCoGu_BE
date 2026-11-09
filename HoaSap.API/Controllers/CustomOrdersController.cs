using System.Security.Claims;
using HoaSap.BLL;
using HoaSap.BLL.CustomOrders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace HoaSap.API.Controllers;

[ApiController, Route("api/custom-orders"), Authorize]
public sealed class CustomOrdersController(CustomOrderService service) : ControllerBase
{
    Guid CustomerId => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : throw new BusinessException(401, "Yêu cầu đăng nhập.");
    Guid AdminId => CustomerId;
    bool Staff => User.IsInRole("Admin") || User.IsInRole("Staff");
    bool Admin => User.IsInRole("Admin");

    [HttpPost]
    public Task<CustomRequestDto> Create(CustomRequestInput input, CancellationToken ct) => service.CreateAsync(CustomerId, input, ct);
    [HttpPut("{id:guid}")]
    public Task<CustomRequestDto> Revise(Guid id, CustomRequestInput input, CancellationToken ct) => service.ReviseRequestAsync(id, CustomerId, input, ct);
    [HttpPost("{id:guid}/cancel")]
    public Task<CustomRequestDto> Cancel(Guid id, CancellationToken ct) => service.CancelRequestAsync(id, CustomerId, Admin, ct);
    [HttpGet]
    public Task<IReadOnlyList<CustomRequestDto>> Mine(CancellationToken ct) => Staff ? service.StaffRequestsAsync(ct) : service.MineAsync(CustomerId, ct);
    [HttpGet("{id:guid}")]
    public Task<CustomQuoteDto> Get(Guid id, CancellationToken ct) => service.QuoteAsync(id, Staff ? null : CustomerId, Staff, Admin, ct);
    [HttpPost("{id:guid}/quote"), Authorize(Roles = "Admin")]
    public Task<CustomQuoteDto> Quote(Guid id, CustomQuoteInput input, CancellationToken ct) => service.ReviseAsync(id, AdminId, input, ct);
    [HttpPost("{id:guid}/approve")]
    public Task<CustomQuoteDto> Approve(Guid id, CustomApproveInput input, CancellationToken ct) => service.ApproveAsync(id, CustomerId, input, ct);
    [HttpPost("{id:guid}/checkout")]
    public Task<CustomCheckoutDto> Checkout(Guid id, CustomCheckoutInput input, CancellationToken ct) => service.CheckoutAsync(id, CustomerId, input, ct);
    [HttpPost("{id:guid}/production/start"), Authorize(Roles = "Admin,Staff")]
    public async Task<CustomLifecycleDto> StartProduction(Guid id, CancellationToken ct) => LimitLifecycle(await service.StartProductionAsync(id, ct));
    [HttpPost("{id:guid}/complete"), Authorize(Roles = "Admin,Staff")]
    public async Task<CustomLifecycleDto> Complete(Guid id, CancellationToken ct) => LimitLifecycle(await service.CompleteAsync(id, ct));
    private CustomLifecycleDto LimitLifecycle(CustomLifecycleDto value) => Admin ? value : value with { RequiredDeposit = 0, PaidAmount = 0, BalanceAmount = 0 };
}
