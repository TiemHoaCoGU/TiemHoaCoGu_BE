using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace HoaSap.BLL.Commerce;

/// <summary>
/// GHN staging carrier adapter for Vietnam's 2-level address model (province + ward, no district).
/// Locked to the GHN staging gateway: https://dev-online-gateway.ghn.vn/shiip/public-api/
/// Fee quotes use Preview Order because /v2/shipping-order/fee still requires the retired
/// district catalogue; Preview and Create Order both accept is_new_to_address + ward/province names.
/// When CarrierOptions.SandboxEmulation is true the adapter never calls GHN and returns deterministic data.
/// </summary>
public sealed class GhnStagingCarrierAdapter : ICarrierAdapter, IDisposable
{
    private const string StagingBase = "https://dev-online-gateway.ghn.vn/shiip/public-api/";
    private const int MaxDimensionCm = 200;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly CarrierOptions _options;
    private readonly GhnStagingOptions? _ghnOptions;

    public GhnStagingCarrierAdapter(HttpClient http, IOptions<CarrierOptions> options, IOptions<GhnStagingOptions>? ghnOptions = null)
    {
        _http = http;
        _options = options.Value;
        _ghnOptions = ghnOptions?.Value;
    }

    public void Dispose() => _http.Dispose();

    private string EffectiveToken =>
        !string.IsNullOrWhiteSpace(_options.Token) ? _options.Token : (_ghnOptions?.Token ?? "");

    private int EffectiveShopId =>
        _options.ShopId > 0 ? _options.ShopId : (_ghnOptions?.ShopId ?? 0);

    public async Task<CarrierRateResult> QuoteAsync(CarrierRateRequest request, CancellationToken ct = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (request.Weight <= 0 || request.Weight > 100_000m)
            throw new BusinessException(400, "Trọng lượng kiện hàng không hợp lệ.");

        // Volumetric weight: (length * width * height) / 5000, the convention used by Vietnamese carriers.
        decimal volumetricKg = 0;
        if (request.LengthCm is > 0 && request.WidthCm is > 0 && request.HeightCm is > 0)
            volumetricKg = (request.LengthCm.Value * request.WidthCm.Value * request.HeightCm.Value) / 5000m;

        var chargeableWeightKg = Math.Max(request.Weight, volumetricKg);

        if (_options.SandboxEmulation)
            return new CarrierRateResult(true, ComputeStandardTariff(request.Origin, request.Destination, chargeableWeightKg), null, "SandboxEmulation");

        var payload = BuildAddressPayload(request, chargeableWeightKg);
        var raw = await PostAsync("v2/shipping-order/preview", payload, ct);
        var envelope = Deserialize<GhnEnvelope<GhnPreviewData>>(raw);
        if (envelope.Code != 200 || envelope.Data is null)
            throw new InvalidOperationException($"GHN từ chối báo phí ({envelope.Code}): {CleanMessage(envelope.Message)}");
        if (envelope.Data.TotalFee <= 0)
            throw new InvalidOperationException("GHN trả về phí vận chuyển không hợp lệ.");
        return new CarrierRateResult(true, envelope.Data.TotalFee, null, raw);
    }

    public async Task<CarrierShipmentResult> CreateShipmentAsync(CarrierShipmentRequest request, CancellationToken ct = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        var weightGrams = (int)Math.Ceiling(request.WeightKg * 1000m);
        if (weightGrams <= 0) weightGrams = 500;

        if (_options.SandboxEmulation)
        {
            var emulatedFee = ComputeStandardTariff(_options.Origin, request.DestinationAddress, request.WeightKg);
            var hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{request.OrderCode}:{request.DeliveryCode}:{DateTime.UtcNow:yyyyMMdd}")))[..8];
            return new CarrierShipmentResult(true, $"GHN{DateTime.UtcNow:yyMMdd}{hex}", emulatedFee, DateTime.UtcNow.AddDays(2), "ready_to_pick", "SandboxEmulation");
        }

        if (request.IsNewAddress && (string.IsNullOrWhiteSpace(request.ToWardName) || string.IsNullOrWhiteSpace(request.ToProvinceName)))
            throw new BusinessException(409, "Đơn chưa có tên phường/tỉnh GHN 2 cấp của người nhận; bổ sung địa chỉ chuẩn trước khi tạo vận đơn.");

        var payload = BuildCreatePayload(request, weightGrams);
        var raw = await PostAsync("v2/shipping-order/create", payload, ct);
        var envelope = Deserialize<GhnEnvelope<GhnCreateOrderData>>(raw);
        if (envelope.Code != 200 || envelope.Data is null || string.IsNullOrWhiteSpace(envelope.Data.OrderCode))
            throw new InvalidOperationException($"GHN từ chối tạo vận đơn ({envelope.Code}): {CleanMessage(envelope.Message)}");

        var fee = envelope.Data.TotalFee > 0
            ? (decimal)envelope.Data.TotalFee
            : ComputeStandardTariff(_options.Origin, request.DestinationAddress, request.WeightKg);
        return new CarrierShipmentResult(true, envelope.Data.OrderCode, fee, envelope.Data.ExpectedDeliveryTime, "ready_to_pick", raw);
    }

    public async Task<CarrierCancelResult> CancelShipmentAsync(string trackingNumber, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(trackingNumber))
            return new CarrierCancelResult(false, "Mã vận đơn không hợp lệ.", null);

        if (_options.SandboxEmulation)
            return new CarrierCancelResult(true, "Đã hủy vận đơn mô phỏng (SandboxEmulation=true).", "SandboxEmulation");

        var raw = await PostAsync("v2/switch-status/cancel", new { order_codes = new[] { trackingNumber } }, ct);
        var envelope = Deserialize<GhnEnvelope<object>>(raw);
        return envelope.Code == 200
            ? new CarrierCancelResult(true, "Hủy vận đơn GHN thành công.", raw)
            : new CarrierCancelResult(false, $"GHN từ chối hủy vận đơn ({envelope.Code}): {CleanMessage(envelope.Message)}", raw);
    }

    public async Task<CarrierTrackingResult> TrackShipmentAsync(string trackingNumber, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(trackingNumber))
            return new CarrierTrackingResult(false, trackingNumber, "Unknown", "Mã vận đơn trống.", null, null);

        if (_options.SandboxEmulation)
            return new CarrierTrackingResult(true, trackingNumber, "delivering", "Đang vận chuyển (SandboxEmulation=true)", DateTime.UtcNow, "SandboxEmulation");

        var raw = await GetAsync($"v2/shipping-order/detail?order_code={Uri.EscapeDataString(trackingNumber)}", ct);
        var envelope = Deserialize<GhnEnvelope<GhnTrackingData>>(raw);
        if (envelope.Code != 200 || envelope.Data is null)
            return new CarrierTrackingResult(false, trackingNumber, "Unknown", $"GHN không trả về trạng thái ({envelope.Code}): {CleanMessage(envelope.Message)}", null, raw);

        var data = envelope.Data;
        var status = string.IsNullOrWhiteSpace(data.Status) ? "unknown" : data.Status;

        // GHN returns the full status history in `log[]`. When the array is missing we still show
        // the current status so the panel is never empty.
        var events = (data.Log ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x.Status))
            .OrderBy(x => x.UpdatedDate ?? DateTime.MinValue)
            .Select(x => new CarrierTrackingEvent(
                x.Status!,
                GetStatusDescription(x.Status!),
                x.UpdatedDate,
                string.IsNullOrWhiteSpace(x.Content) ? x.Note : x.Content))
            .ToList();
        if (events.Count == 0)
            events.Add(new CarrierTrackingEvent(status, GetStatusDescription(status), data.UpdatedDate, null));

        return new CarrierTrackingResult(
            true,
            trackingNumber,
            status,
            GetStatusDescription(status),
            data.UpdatedDate ?? DateTime.UtcNow,
            raw,
            events,
            data.CodAmount,
            data.IsCodCollected,
            data.CodCollectDate,
            data.LeadTime);
    }

    /// <summary>
    /// Local tariff used only when SandboxEmulation is enabled. Never used for a real GHN call.
    /// Intra-province: 22,000 VND up to 2 kg, +5,000 VND per extra 500 g.
    /// Inter-province: 35,000 VND up to 0.5 kg, +5,000 VND per extra 500 g.
    /// </summary>
    public static decimal ComputeStandardTariff(string origin, string destination, decimal weightKg)
    {
        if (weightKg <= 0) weightKg = 0.5m;

        if (IsSameProvince(origin, destination))
        {
            if (weightKg <= 2.0m) return 22_000m;
            return 22_000m + (int)Math.Ceiling((weightKg - 2.0m) / 0.5m) * 5_000m;
        }

        if (weightKg <= 0.5m) return 35_000m;
        return 35_000m + (int)Math.Ceiling((weightKg - 0.5m) / 0.5m) * 5_000m;
    }

    private Dictionary<string, object?> BuildAddressPayload(CarrierRateRequest request, decimal chargeableWeightKg)
    {
        var grams = (int)Math.Ceiling(chargeableWeightKg * 1000m);
        var payload = new Dictionary<string, object?>
        {
            ["payment_type_id"] = _options.PaymentTypeId,
            ["required_note"] = _options.RequiredNote,
            ["to_name"] = ReceiverName(request.ToName),
            ["to_phone"] = ReceiverPhone(request.ToPhone),
            ["to_address"] = request.Destination,
            ["to_ward_name"] = request.ToWardName ?? "",
            ["to_province_name"] = request.ToProvinceName ?? "",
            ["is_new_to_address"] = true,
            ["weight"] = grams,
            ["length"] = ClampDimension(request.LengthCm),
            ["width"] = ClampDimension(request.WidthCm),
            ["height"] = ClampDimension(request.HeightCm),
            ["service_type_id"] = grams > 20_000 ? 5 : 2,
            ["content"] = "Đơn hoa tươi"
        };
        if (request.Items is { Count: > 0 }) payload["items"] = MapItems(request.Items);
        return payload;
    }

    private static List<Dictionary<string, object?>> MapItems(IReadOnlyList<CarrierShipmentItem> items) =>
        items.Select(x => new Dictionary<string, object?>
        {
            ["name"] = Truncate(x.Name, 512),
            ["code"] = Truncate(x.Code, 50),
            ["quantity"] = x.Quantity,
            ["price"] = (int)Math.Clamp(x.Price, 0m, int.MaxValue),
            ["weight"] = x.WeightGrams,
            ["length"] = x.LengthCm,
            ["width"] = x.WidthCm,
            ["height"] = x.HeightCm
        }).ToList();

    private Dictionary<string, object?> BuildCreatePayload(CarrierShipmentRequest request, int weightGrams)
    {
        var payload = new Dictionary<string, object?>
        {
            ["client_order_code"] = Truncate(request.DeliveryCode, 50),
            ["payment_type_id"] = _options.PaymentTypeId,
            ["required_note"] = _options.RequiredNote,
            ["note"] = Truncate(request.Note, 5000),
            ["to_name"] = ReceiverName(request.RecipientName),
            ["to_phone"] = ReceiverPhone(request.RecipientPhone),
            ["to_address"] = request.DestinationAddress,
            ["to_ward_name"] = request.ToWardName ?? "",
            ["to_province_name"] = request.ToProvinceName ?? "",
            ["is_new_to_address"] = request.IsNewAddress,
            ["from_name"] = _options.SenderName,
            ["from_phone"] = _options.SenderPhone,
            ["from_address"] = _options.SenderAddress,
            ["weight"] = weightGrams,
            ["length"] = ClampDimension(request.LengthCm),
            ["width"] = ClampDimension(request.WidthCm),
            ["height"] = ClampDimension(request.HeightCm),
            ["service_type_id"] = weightGrams > 20_000 ? 5 : 2,
            ["cod_amount"] = (int)Math.Clamp(request.CodAmount, 0m, 50_000_000m),
            ["items"] = MapItems(request.Items)
        };

        if (!string.IsNullOrWhiteSpace(_options.SenderWardName) && !string.IsNullOrWhiteSpace(_options.SenderProvinceName))
        {
            payload["from_ward_name"] = _options.SenderWardName;
            payload["from_province_name"] = _options.SenderProvinceName;
            payload["is_new_from_address"] = _options.IsNewFromAddress;
        }

        return payload;
    }

    private async Task<string> PostAsync(string path, object payload, CancellationToken ct)
    {
        EnsureConfigured();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 5, 30)));

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(StagingBase), path));
        request.Headers.TryAddWithoutValidation("Token", EffectiveToken);
        request.Headers.TryAddWithoutValidation("ShopId", EffectiveShopId.ToString(CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(payload, options: JsonOptions);

        using var response = await _http.SendAsync(request, cts.Token);
        var raw = await response.Content.ReadAsStringAsync(cts.Token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"GHN trả về HTTP {(int)response.StatusCode}: {TrimRaw(raw)}");
        return raw;
    }

    private async Task<string> GetAsync(string path, CancellationToken ct)
    {
        EnsureConfigured();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 5, 30)));

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(StagingBase), path));
        request.Headers.TryAddWithoutValidation("Token", EffectiveToken);
        request.Headers.TryAddWithoutValidation("ShopId", EffectiveShopId.ToString(CultureInfo.InvariantCulture));

        using var response = await _http.SendAsync(request, cts.Token);
        var raw = await response.Content.ReadAsStringAsync(cts.Token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"GHN trả về HTTP {(int)response.StatusCode}: {TrimRaw(raw)}");
        return raw;
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(EffectiveToken))
            throw new InvalidOperationException("Chưa cấu hình Token GHN staging (Shipping:Carrier:Token hoặc Shipping:GhnStaging:Token).");
        if (EffectiveShopId <= 0)
            throw new InvalidOperationException("Chưa cấu hình ShopId GHN staging (Shipping:Carrier:ShopId hoặc Shipping:GhnStaging:ShopId).");
    }

    private static T Deserialize<T>(string raw) =>
        JsonSerializer.Deserialize<T>(raw, JsonOptions) ?? throw new InvalidOperationException("GHN trả về JSON không hợp lệ.");

    private static string ReceiverName(string? value) => string.IsNullOrWhiteSpace(value) ? "Khách hàng" : Truncate(value.Trim(), 1024);

    private static string ReceiverPhone(string? value) => string.IsNullOrWhiteSpace(value) ? "0900000000" : Truncate(value.Trim(), 30);

    private static int ClampDimension(int? value) => Math.Clamp(value ?? 10, 1, MaxDimensionCm);

    private static string Truncate(string? value, int max) => string.IsNullOrEmpty(value) || value.Length <= max ? value ?? "" : value[..max];

    private static string CleanMessage(string? message) => string.IsNullOrWhiteSpace(message) ? "không có thông điệp" : Truncate(message.Trim(), 300);

    private static string TrimRaw(string raw) => Truncate(raw.ReplaceLineEndings(" ").Trim(), 300);

    private static bool IsSameProvince(string origin, string destination)
    {
        if (string.IsNullOrWhiteSpace(origin) || string.IsNullOrWhiteSpace(destination)) return true;

        var o = origin.ToLowerInvariant();
        var d = destination.ToLowerInvariant();
        var hcmVariants = new[] { "hồ chí minh", "ho chi minh", "hcm", "tp.hcm", "tphcm" };
        var hnVariants = new[] { "hà nội", "ha noi", "hn" };

        if (hcmVariants.Any(o.Contains) && hcmVariants.Any(d.Contains)) return true;
        if (hnVariants.Any(o.Contains) && hnVariants.Any(d.Contains)) return true;
        return false;
    }

    private static string GetStatusDescription(string status) => status switch
    {
        "ready_to_pick" => "Mới tạo - Chờ lấy hàng",
        "picking" => "Đang lấy hàng",
        "money_collect_picking" => "Đang thu tiền khi lấy hàng",
        "picked" => "Đã lấy hàng",
        "storing" => "Đã nhập kho",
        "transporting" => "Đang luân chuyển",
        "sorting" => "Đang phân loại",
        "delivering" => "Đang giao hàng",
        "money_collect_delivering" => "Đang thu tiền khi giao hàng",
        "delivered" => "Giao hàng thành công",
        "delivery_fail" => "Giao hàng thất bại",
        "waiting_to_return" => "Chờ chuyển hoàn",
        "return" => "Đang chuyển hoàn",
        "return_transporting" => "Đang luân chuyển hoàn",
        "return_sorting" => "Đang phân loại hoàn",
        "returning" => "Đang trên đường hoàn",
        "return_fail" => "Chuyển hoàn thất bại",
        "returned" => "Đã chuyển hoàn",
        "cancel" => "Đã hủy đơn",
        "exception" => "Sự cố - cần xử lý tay",
        "lost" => "Thất lạc",
        "damage" => "Hư hỏng",
        "scrap" => "Đã tiêu hủy",
        _ => status
    };

    private sealed class GhnEnvelope<T>
    {
        [JsonPropertyName("code")] public int Code { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public T? Data { get; set; }
    }

    private sealed class GhnPreviewData
    {
        [JsonPropertyName("total_fee")] public int TotalFee { get; set; }
    }

    private sealed class GhnCreateOrderData
    {
        [JsonPropertyName("order_code")] public string OrderCode { get; set; } = "";
        [JsonPropertyName("total_fee")] public int TotalFee { get; set; }
        [JsonPropertyName("expected_delivery_time")] public DateTime? ExpectedDeliveryTime { get; set; }
    }

    private sealed class GhnTrackingData
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("updated_date")] public DateTime? UpdatedDate { get; set; }
        [JsonPropertyName("cod_amount")] public decimal? CodAmount { get; set; }
        [JsonPropertyName("is_cod_collected")] public bool? IsCodCollected { get; set; }
        [JsonPropertyName("cod_collect_date")] public DateTime? CodCollectDate { get; set; }
        [JsonPropertyName("leadtime")] public DateTime? LeadTime { get; set; }
        [JsonPropertyName("log")] public List<GhnTrackingLogEntry>? Log { get; set; }
    }

    /// <summary>GHN status-history entry; `content` is present on newer responses, `note` on older ones.</summary>
    private sealed class GhnTrackingLogEntry
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("updated_date")] public DateTime? UpdatedDate { get; set; }
        [JsonPropertyName("content")] public string? Content { get; set; }
        [JsonPropertyName("note")] public string? Note { get; set; }
    }
}
