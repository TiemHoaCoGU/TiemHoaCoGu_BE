using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace HoaSap.BLL.Commerce;

public class CommerceGatewayOptions
{
    public string Provider { get; set; } = "GenericSignedGateway";
    public string? Endpoint { get; set; }
    public string? Secret { get; set; }
    public string? MerchantId { get; set; }
    public string MomoPartnerCode { get; set; } = "";
    public string MomoAccessKey { get; set; } = "";
    public string MomoSecretKey { get; set; } = "";
    public string MomoIpnUrl { get; set; } = "";
    public string VnPayTmnCode { get; set; } = "";
    public string VnPayHashSecret { get; set; } = "";
    public string VnPayBaseUrl { get; set; } = "https://sandbox.vnpayment.vn/paymentv2/vpcpay.html";
    public string VnPayApiUrl { get; set; } = "https://sandbox.vnpayment.vn/merchant_webapi/api/transaction";
    public int TimeoutSeconds { get; set; } = 15;
    public string[] AllowedReturnOrigins { get; set; } = [];
    public string[] ReturnUrlAllowlist { get => AllowedReturnOrigins; set => AllowedReturnOrigins = value ?? []; }
    public virtual void Validate()
    {
        if (Provider == "MomoSandbox") ValidateMomoSandbox();
        else if (Provider == "VnPaySandbox") ValidateVnPaySandbox();
        else if (Provider == "GenericSignedGateway") ValidateTransport();
        else throw new InvalidOperationException("Unsupported payment provider; only MoMo sandbox, VNPay sandbox or the explicitly configured generic test adapter is allowed.");
        if (AllowedReturnOrigins.Length == 0 || AllowedReturnOrigins.Any(x => !Uri.TryCreate(x, UriKind.Absolute, out var u) || (!u.IsLoopback && u.Host != "localhost" && u.Scheme != Uri.UriSchemeHttps) || !string.IsNullOrEmpty(u.PathAndQuery.Trim('/'))))
            throw new InvalidOperationException("Commerce gateway return URL allowlist is required and must contain HTTPS origins only.");
    }
    private void ValidateVnPaySandbox()
    {
        if (string.IsNullOrWhiteSpace(VnPayTmnCode) || string.IsNullOrWhiteSpace(VnPayHashSecret))
            throw new InvalidOperationException("VNPay sandbox is not configured; no payment request was sent.");
        if (TimeoutSeconds is < 10 or > 120) throw new InvalidOperationException("VNPay API timeout must be between 10 and 120 seconds.");
        if (!Uri.TryCreate(VnPayBaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.Host.EndsWith("vnpayment.vn", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("VNPay sandbox base URL must be an absolute HTTPS URL on vnpayment.vn.");
    }
    private void ValidateMomoSandbox()
    {
        if (string.IsNullOrWhiteSpace(MomoPartnerCode) || string.IsNullOrWhiteSpace(MomoAccessKey) || string.IsNullOrWhiteSpace(MomoSecretKey) || string.IsNullOrWhiteSpace(MomoIpnUrl))
            throw new InvalidOperationException("MoMo sandbox is not configured; no payment request was sent.");
        if (TimeoutSeconds is < 30 or > 120) throw new InvalidOperationException("MoMo API timeout must be between 30 and 120 seconds.");
        if (!Uri.TryCreate(MomoIpnUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || IsReservedTestHost(uri.Host) || IPAddress.TryParse(uri.Host, out var ip) && IsUnsafeIp(ip))
            throw new InvalidOperationException("MoMo sandbox IPN URL must be a publicly reachable HTTPS URL.");
    }
    static bool IsReservedTestHost(string host) => new[] { ".test", ".example", ".invalid", ".localhost", ".local", ".internal", ".lan" }.Any(s => host.Equals(s[1..], StringComparison.OrdinalIgnoreCase) || host.EndsWith(s, StringComparison.OrdinalIgnoreCase));
    static bool IsUnsafeIp(IPAddress ip) => IsPrivate(ip) || ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (ip.GetAddressBytes()[0] & 0xfe) == 0xfc || ip.GetAddressBytes()[0] == 0xff);
    protected void ValidateTransport()
    {
        if (string.IsNullOrWhiteSpace(Endpoint) || string.IsNullOrWhiteSpace(Secret) || string.IsNullOrWhiteSpace(MerchantId))
            throw new InvalidOperationException("Commerce gateway is not configured; no payment/refund request was sent.");
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Commerce gateway endpoint must be an absolute HTTPS URL.");
        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(uri.Host, out var ip) && (IPAddress.IsLoopback(ip) || IsPrivate(ip)))
            throw new InvalidOperationException("Commerce gateway endpoint cannot target a local or private address.");
        if (TimeoutSeconds is < 1 or > 120) throw new InvalidOperationException("Commerce gateway timeout is invalid.");

    }
    public bool IsAllowedReturnOrigin(Uri uri) => AllowedReturnOrigins.Any(x => Uri.TryCreate(x.TrimEnd('/'), UriKind.Absolute, out var allowed) && string.Equals(allowed.Scheme, uri.Scheme, StringComparison.OrdinalIgnoreCase) && string.Equals(allowed.Host, uri.Host, StringComparison.OrdinalIgnoreCase) && allowed.Port == uri.Port);
    static bool IsPrivate(IPAddress ip) { var b = ip.GetAddressBytes(); return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && (b[0] == 10 || b[0] == 127 || b[0] == 169 && b[1] == 254 || b[0] == 172 && b[1] >= 16 && b[1] <= 31 || b[0] == 192 && b[1] == 168); }
}

public sealed class CarrierOptions : CommerceGatewayOptions
{
    public CarrierOptions() => Provider = "Disabled";
    public override void Validate()
    {
        if (Provider is "GhnSandbox" or "GhnStaging")
        {
            if (string.IsNullOrWhiteSpace(Origin))
                throw new InvalidOperationException("Carrier origin address is required.");
            return;
        }
        ValidateTransport();
    }
    public string Origin { get; set; } = "";
    public int QuoteLifetimeMinutes { get; set; } = 15;
    public string Token { get; set; } = "";
    public int ShopId { get; set; }
    public int WarehouseDistrictId { get; set; } = 1442;
    public string WarehouseWardCode { get; set; } = "20101";
    public string SenderName { get; set; } = "Tiệm Hoa Cô Gu";
    public string SenderPhone { get; set; } = "0901234567";
    public string SenderAddress { get; set; } = "72 Lê Thánh Tôn, Bến Nghé, Quận 1, Hồ Chí Minh";
    public bool SandboxEmulation { get; set; } = true;
}
public sealed record GatewayChargeRequest(Guid OrderId, decimal Amount, string Currency, string ReturnUrl, string ProviderKey = "");
public sealed record GatewayChargeResult(bool Accepted, string TransactionCode, string? RedirectUrl, string? RawResponse);
public sealed record GatewayRefundRequest(Guid RefundId, decimal Amount, string Currency, string OriginalTransactionCode);
public sealed record GatewayRefundResult(bool Accepted, string TransactionCode, string? RawResponse);
public sealed record GatewayPaymentQueryResult(string Outcome, int ResultCode, string ProviderOrderId, string RequestId, decimal Amount, string TransactionCode, string Message, long ResponseTime);
public sealed record CarrierRateRequest(
    string Origin,
    string Destination,
    decimal Weight,
    int? LengthCm = null,
    int? WidthCm = null,
    int? HeightCm = null,
    int? ToProvinceId = null,
    int? ToWardId = null);
public sealed record CarrierRateResult(bool Accepted, decimal Amount, string? TrackingNumber, string? RawResponse);

public sealed record CarrierShipmentItem(
    string Name,
    string Code,
    int Quantity,
    decimal Price,
    int WeightGrams,
    int? LengthCm = null,
    int? WidthCm = null,
    int? HeightCm = null);

public sealed record CarrierShipmentRequest(
    string OrderCode,
    string DeliveryCode,
    string RecipientName,
    string RecipientPhone,
    string DestinationAddress,
    int? ToProvinceId,
    int? ToWardId,
    decimal WeightKg,
    int? LengthCm,
    int? WidthCm,
    int? HeightCm,
    decimal CodAmount,
    string Note,
    IReadOnlyList<CarrierShipmentItem> Items);

public sealed record CarrierShipmentResult(
    bool Accepted,
    string TrackingNumber,
    decimal ShippingFee,
    DateTime? ExpectedDeliveryDate,
    string? Status,
    string? RawResponse,
    string? ErrorMessage = null);

public sealed record CarrierCancelResult(
    bool Accepted,
    string? Message,
    string? RawResponse);

public sealed record CarrierTrackingResult(
    bool Accepted,
    string TrackingNumber,
    string Status,
    string? StatusDescription,
    DateTime? UpdatedAt,
    string? RawResponse);

public interface IPaymentGateway
{
    string PaymentMethod => "GenericSignedGateway";
    Task<GatewayChargeResult> ChargeAsync(GatewayChargeRequest request, CancellationToken ct = default);
    Task<GatewayRefundResult> RefundAsync(GatewayRefundRequest request, CancellationToken ct = default);
    Task<GatewayPaymentQueryResult> QueryAsync(string providerKey, decimal expectedAmount, CancellationToken ct = default) => Task.FromException<GatewayPaymentQueryResult>(new NotSupportedException("Provider transaction query is unavailable."));
    bool VerifyWebhook(ReadOnlySpan<byte> payload, string? signature, string? timestamp);
    GatewayWebhookRequest ParseWebhook(ReadOnlySpan<byte> payload) => JsonSerializer.Deserialize<GatewayWebhookRequest>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new JsonException("Gateway webhook payload is invalid.");
}
public interface ICarrierAdapter
{
    Task<CarrierRateResult> QuoteAsync(CarrierRateRequest request, CancellationToken ct = default);
    Task<CarrierShipmentResult> CreateShipmentAsync(CarrierShipmentRequest request, CancellationToken ct = default)
        => Task.FromResult(new CarrierShipmentResult(false, "", 0, null, "Disabled", null, "Carrier does not support shipment creation."));
    Task<CarrierCancelResult> CancelShipmentAsync(string trackingNumber, CancellationToken ct = default)
        => Task.FromResult(new CarrierCancelResult(false, "Carrier does not support shipment cancellation.", null));
    Task<CarrierTrackingResult> TrackShipmentAsync(string trackingNumber, CancellationToken ct = default)
        => Task.FromResult(new CarrierTrackingResult(false, trackingNumber, "Unknown", null, null, null));
}

public sealed class DisabledCarrier : ICarrierAdapter
{
    public Task<CarrierRateResult> QuoteAsync(CarrierRateRequest request, CancellationToken ct = default) => Task.FromException<CarrierRateResult>(new InvalidOperationException("Carrier integration is not configured."));
    public Task<CarrierShipmentResult> CreateShipmentAsync(CarrierShipmentRequest request, CancellationToken ct = default) => Task.FromException<CarrierShipmentResult>(new InvalidOperationException("Carrier integration is not configured."));
    public Task<CarrierCancelResult> CancelShipmentAsync(string trackingNumber, CancellationToken ct = default) => Task.FromException<CarrierCancelResult>(new InvalidOperationException("Carrier integration is not configured."));
    public Task<CarrierTrackingResult> TrackShipmentAsync(string trackingNumber, CancellationToken ct = default) => Task.FromException<CarrierTrackingResult>(new InvalidOperationException("Carrier integration is not configured."));
}

public static class CommerceServiceCollectionExtensions
{
    public static IServiceCollection AddCommerceAdapters(this IServiceCollection services, Action<CommerceGatewayOptions>? gateway = null, Action<CarrierOptions>? carrier = null)
    {
        services.AddOptions<CommerceGatewayOptions>().Configure(o => gateway?.Invoke(o));
        services.AddOptions<CarrierOptions>().Configure(o => carrier?.Invoke(o));
        services.AddSingleton<IPaymentGateway>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<CommerceGatewayOptions>>();
            var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            return options.Value.Provider switch
            {
                "MomoSandbox" => new MomoSandboxPaymentGateway(http, options),
                "VnPaySandbox" => new VnPaySandboxPaymentGateway(http, options),
                "GenericSignedGateway" => new SignedHttpPaymentGateway(http, options),
                _ => throw new InvalidOperationException("Unsupported payment provider; no request was sent.")
            };
        });
        services.AddSingleton<ICarrierAdapter>(sp => sp.GetRequiredService<IOptions<CarrierOptions>>().Value.Provider switch
        {
            "Disabled" => new DisabledCarrier(),
            "GhnSandbox" or "GhnStaging" => new GhnStagingCarrierAdapter(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }), sp.GetRequiredService<IOptions<CarrierOptions>>(), sp.GetService<IOptions<GhnStagingOptions>>()),
            "GenericSignedGateway" => new SignedHttpCarrier(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }), sp.GetRequiredService<IOptions<CarrierOptions>>()),
            _ => throw new InvalidOperationException("Unsupported carrier provider; no shipment request was sent.")
        });
        services.AddScoped<GatewayPaymentService>();
        services.AddScoped<GatewayRefundService>();
        return services;
    }
}

/// <summary>Generic signed adapter only. It deliberately does not identify or emulate a real provider.</summary>
public sealed class SignedHttpPaymentGateway(HttpClient http, IOptions<CommerceGatewayOptions> options) : IPaymentGateway, IDisposable
{
    public void Dispose() => http.Dispose();
    readonly HttpClient http = http;
    readonly CommerceGatewayOptions cfg = options.Value;
    public async Task<GatewayChargeResult> ChargeAsync(GatewayChargeRequest request, CancellationToken ct = default) => await SendAsync<GatewayChargeRequest, GatewayChargeResult>("charge", request, ct);
    public async Task<GatewayRefundResult> RefundAsync(GatewayRefundRequest request, CancellationToken ct = default) => await SendAsync<GatewayRefundRequest, GatewayRefundResult>("refund", request, ct);
    async Task<T> SendAsync<TIn, T>(string operation, TIn value, CancellationToken ct)
    {
        cfg.Validate();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromSeconds(cfg.TimeoutSeconds));
        var body = JsonSerializer.Serialize(value); var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(cfg.Endpoint!.TrimEnd('/') + "/"), operation)); req.Content = new StringContent(body, Encoding.UTF8, "application/json"); AddSignature(req, body, timestamp);
        using var response = await http.SendAsync(req, cts.Token); var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Gateway rejected {operation}: {(int)response.StatusCode}.");
        return JsonSerializer.Deserialize<T>(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidOperationException("Gateway returned an invalid response.");
    }
    void AddSignature(HttpRequestMessage req, string body, string timestamp) { req.Headers.Add("X-Merchant-Id", cfg.MerchantId); req.Headers.Add("X-Timestamp", timestamp); req.Headers.Add("X-Signature", Sign(body, timestamp)); }
    string Sign(string body, string timestamp) => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(cfg.Secret!), Encoding.UTF8.GetBytes(timestamp + "." + body))).ToLowerInvariant();
    public bool VerifyWebhook(ReadOnlySpan<byte> payload, string? signature, string? timestamp)
    {
        try { cfg.Validate(); if (!long.TryParse(timestamp, out var ts) || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts) > 300 || string.IsNullOrWhiteSpace(signature)) return false; var expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(cfg.Secret!), Encoding.UTF8.GetBytes(timestamp + "." + Encoding.UTF8.GetString(payload)))).ToLowerInvariant(); return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(signature.Trim().ToLowerInvariant())); } catch { return false; }
    }
}

public sealed class SignedHttpCarrier(HttpClient http, IOptions<CarrierOptions> options) : ICarrierAdapter, IDisposable
{
    public void Dispose() => http.Dispose();
    readonly HttpClient http = http; readonly CarrierOptions cfg = options.Value;
    public async Task<CarrierRateResult> QuoteAsync(CarrierRateRequest request, CancellationToken ct = default)
    {
        cfg.Validate(); using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(cfg.TimeoutSeconds)); ct = timeout.Token; using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(cfg.Endpoint!.TrimEnd('/') + "/"), "quote")); var body = JsonSerializer.Serialize(request); req.Content = new StringContent(body, Encoding.UTF8, "application/json"); var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(); req.Headers.Add("X-Merchant-Id", cfg.MerchantId); req.Headers.Add("X-Timestamp", stamp); req.Headers.Add("X-Signature", Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(cfg.Secret!), Encoding.UTF8.GetBytes(stamp + "." + body))).ToLowerInvariant()); using var response = await http.SendAsync(req, ct); if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Carrier rejected quote request."); return await response.Content.ReadFromJsonAsync<CarrierRateResult>(cancellationToken: ct) ?? throw new InvalidOperationException("Carrier returned an invalid response.");
    }
}
