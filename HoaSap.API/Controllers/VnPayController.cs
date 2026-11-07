using HoaSap.BLL.Workshops;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace HoaSap.API.Controllers;

[ApiController, Route("api/vnpay")]
public sealed class VnPayController(VnPayBookingService vnPay) : ControllerBase
{
    [HttpGet("ipn")]
    [AllowAnonymous]
    public async Task<IActionResult> Ipn(CancellationToken ct)
    {
        var raw = Request.QueryString.HasValue ? Request.QueryString.Value![1..] : "";
        var (rsp, message) = await vnPay.IpnAsync(raw, ct);
        return Content(JsonSerializer.Serialize(new Dictionary<string, string> { ["RspCode"] = rsp, ["Message"] = message }), "application/json");
    }
}
