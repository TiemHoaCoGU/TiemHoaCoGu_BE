using System.Security.Claims;
using HoaSap.BLL;
using HoaSap.BLL.Commerce;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace HoaSap.API.Controllers;

[ApiController, Route("api/gateway")]
public sealed class GatewayController(GatewayPaymentService payments, GatewayReconciliationService reconciliation, IOptions<CommerceGatewayOptions> gatewayOptions) : ControllerBase
{
    [HttpGet("availability"), AllowAnonymous]
    public object Availability()
    {
        try { gatewayOptions.Value.Validate(); return new { provider = gatewayOptions.Value.Provider, ready = true, sandbox = gatewayOptions.Value.Provider is "MomoSandbox" or "VnPaySandbox" }; }
        catch (InvalidOperationException) { return new { provider = gatewayOptions.Value.Provider, ready = false, sandbox = true }; }
    }

    [HttpPost("orders/{orderId:guid}/charge"), Authorize]
    public Task<CheckoutPaymentResponse> Charge(Guid orderId, GatewayPaymentRequest request, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var owner)) throw new BusinessException(401, "Yêu cầu đăng nhập.");
        request.OrderId = orderId; return payments.StartAsync(owner, request, ct);
    }

    public sealed class SandboxSimulateRequest
    {
        public bool Success { get; set; } = true;
        public string? ResponseCode { get; set; } = "00";
    }

    [HttpPost("orders/{orderId:guid}/simulate-sandbox"), Authorize]
    public async Task<IActionResult> SimulateSandbox(Guid orderId, [FromBody] SandboxSimulateRequest? request, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var owner)) throw new BusinessException(401, "Yêu cầu đăng nhập.");
        var req = request ?? new SandboxSimulateRequest();
        var result = await payments.SimulateSandboxWebhookAsync(owner, orderId, req.Success, req.ResponseCode, ct);
        return Ok(new { success = req.Success, orderId, paymentStatus = req.Success ? "Paid" : "Unpaid" });
    }

    [HttpGet("orders/{orderId:guid}/balance"), Authorize]
    public Task<OrderPaymentSummary> Balance(Guid orderId, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var owner)) throw new BusinessException(401, "Yêu cầu đăng nhập.");
        return reconciliation.BalanceAsync(orderId, owner, ct);
    }

    [HttpGet("reconciliation"), Authorize(Roles = "Admin")]
    public Task<IReadOnlyList<PaymentReconciliationDto>> ReconciliationQueue(CancellationToken ct) => reconciliation.ListAsync(ct);

    [HttpPost("reconciliation/{id:guid}/query"), Authorize(Roles = "Admin")]
    public Task<GatewayPaymentQueryResult> Query(Guid id, CancellationToken ct) => payments.QueryAsync(id, ct);

    [HttpPost("reconciliation/{id:guid}"), Authorize(Roles = "Admin")]
    public Task<PaymentReconciliationDto> Reconcile(Guid id, PaymentReconciliationInput input, CancellationToken ct) => reconciliation.ReconcileAsync(id, input, ct);

    [HttpPost("webhook"), AllowAnonymous]
    public async Task<IActionResult> Webhook(CancellationToken ct)
    {
        using var stream = new MemoryStream(); await Request.Body.CopyToAsync(stream, ct);
        await payments.WebhookAsync(stream.ToArray(), Request.Headers["X-Signature"].FirstOrDefault(), Request.Headers["X-Timestamp"].FirstOrDefault(), ct);
        return NoContent();
    }

    [HttpGet("vnpay-ipn"), AllowAnonymous]
    public async Task<IActionResult> VnPayIpnGet(CancellationToken ct)
    {
        var query = Request.QueryString.Value?.TrimStart('?');
        if (string.IsNullOrEmpty(query))
            return Ok(new { RspCode = "99", Message = "Missing query string" });

        var raw = System.Text.Encoding.UTF8.GetBytes(query);
        try
        {
            var result = await payments.WebhookAsync(raw, Request.Query["vnp_SecureHash"].FirstOrDefault(), null, ct);
            if (result.Replay) return Ok(new { RspCode = "02", Message = "Order already confirmed" });
            return Ok(new { RspCode = "00", Message = "Confirm Success" });
        }
        catch (BusinessException ex) when (ex.StatusCode == 401)
        {
            return Ok(new { RspCode = "97", Message = "Invalid Checksum" });
        }
        catch (BusinessException ex) when (ex.StatusCode == 404)
        {
            return Ok(new { RspCode = "01", Message = "Order not found" });
        }
        catch (BusinessException ex) when (ex.StatusCode == 400 || ex.StatusCode == 409)
        {
            return Ok(new { RspCode = "04", Message = ex.Message });
        }
        catch
        {
            return Ok(new { RspCode = "99", Message = "Unknown error" });
        }
    }

    [HttpPost("vnpay-ipn"), AllowAnonymous]
    public async Task<IActionResult> VnPayIpnPost(CancellationToken ct)
    {
        using var stream = new MemoryStream();
        await Request.Body.CopyToAsync(stream, ct);
        var raw = stream.Length > 0 ? stream.ToArray() : System.Text.Encoding.UTF8.GetBytes(Request.QueryString.Value?.TrimStart('?') ?? "");
        try
        {
            var result = await payments.WebhookAsync(raw, Request.Query["vnp_SecureHash"].FirstOrDefault() ?? Request.Headers["X-Signature"].FirstOrDefault(), null, ct);
            if (result.Replay) return Ok(new { RspCode = "02", Message = "Order already confirmed" });
            return Ok(new { RspCode = "00", Message = "Confirm Success" });
        }
        catch (BusinessException ex) when (ex.StatusCode == 401)
        {
            return Ok(new { RspCode = "97", Message = "Invalid Checksum" });
        }
        catch (BusinessException ex) when (ex.StatusCode == 404)
        {
            return Ok(new { RspCode = "01", Message = "Order not found" });
        }
        catch (BusinessException ex) when (ex.StatusCode == 400 || ex.StatusCode == 409)
        {
            return Ok(new { RspCode = "04", Message = ex.Message });
        }
        catch
        {
            return Ok(new { RspCode = "99", Message = "Unknown error" });
        }
    }
}
