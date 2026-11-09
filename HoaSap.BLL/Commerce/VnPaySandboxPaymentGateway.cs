using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace HoaSap.BLL.Commerce;

/// <summary>
/// Bộ chuyển đổi cổng thanh toán VNPay Sandbox (chuẩn VNPay 2.1.0).
/// Hỗ trợ tạo URL thanh toán có chữ ký HMAC-SHA512, xác thực IPN webhook, hoàn tiền và đối soát giao dịch.
/// </summary>
public sealed class VnPaySandboxPaymentGateway(HttpClient http, IOptions<CommerceGatewayOptions> options) : IPaymentGateway, IDisposable
{
    private const string PaymentMethodName = "VnPaySandbox";
    private readonly CommerceGatewayOptions cfg = options.Value;
    public string PaymentMethod => PaymentMethodName;
    public void Dispose() => http.Dispose();

    /// <summary>
    /// Tạo URL thanh toán VNPay Sandbox có kèm chữ ký HMAC-SHA512.
    /// </summary>
    public Task<GatewayChargeResult> ChargeAsync(GatewayChargeRequest request, CancellationToken ct = default)
    {
        cfg.Validate();
        if (cfg.Provider != "VnPaySandbox")
            throw new InvalidOperationException("VNPay adapter is not enabled for this environment.");

        if (request.Currency != "VND" || request.Amount < 5_000m || request.Amount > 1_000_000_000m || decimal.Truncate(request.Amount) != request.Amount)
            throw new InvalidOperationException("VNPay sandbox charge requires a whole-VND amount from 5,000 to 1,000,000,000.");

        var suffix = IntentSuffix(request.ProviderKey);
        var txnRef = "I" + suffix;
        var now = DateTime.UtcNow.AddHours(7); // Múi giờ Việt Nam (GMT+7)
        var createDate = now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var expireDate = now.AddMinutes(15).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var amountInVnPayUnits = (decimal.ToInt64(request.Amount) * 100).ToString(CultureInfo.InvariantCulture);

        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["vnp_Version"] = "2.1.0",
            ["vnp_Command"] = "pay",
            ["vnp_TmnCode"] = cfg.VnPayTmnCode,
            ["vnp_Amount"] = amountInVnPayUnits,
            ["vnp_CreateDate"] = createDate,
            ["vnp_CurrCode"] = "VND",
            ["vnp_IpAddr"] = "127.0.0.1",
            ["vnp_Locale"] = "vn",
            ["vnp_OrderInfo"] = "Thanh toan don hang " + request.OrderId.ToString("N"),
            ["vnp_OrderType"] = "other",
            ["vnp_ReturnUrl"] = request.ReturnUrl,
            ["vnp_TxnRef"] = txnRef,
            ["vnp_ExpireDate"] = expireDate
        };

        var queryString = BuildQueryString(parameters);
        var secureHash = Sign(queryString, cfg.VnPayHashSecret);
        var paymentUrl = $"{cfg.VnPayBaseUrl}?{queryString}&vnp_SecureHash={secureHash}";

        return Task.FromResult(new GatewayChargeResult(true, txnRef, paymentUrl, null));
    }

    /// <summary>
    /// Hoàn tiền giao dịch qua VNPay API (Command: refund).
    /// </summary>
    public async Task<GatewayRefundResult> RefundAsync(GatewayRefundRequest request, CancellationToken ct = default)
    {
        cfg.Validate();
        if (cfg.Provider != "VnPaySandbox")
            throw new InvalidOperationException("VNPay adapter is not enabled for this environment.");

        if (request.Currency != "VND" || request.Amount < 5_000m || request.Amount > 1_000_000_000m || decimal.Truncate(request.Amount) != request.Amount)
            throw new InvalidOperationException("VNPay sandbox refund requires a whole-VND amount from 5,000 to 1,000,000,000.");

        var requestId = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow.AddHours(7);
        var createDate = now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var transDate = createDate;
        var amountInVnPayUnits = (decimal.ToInt64(request.Amount) * 100).ToString(CultureInfo.InvariantCulture);

        const string version = "2.1.0";
        const string command = "refund";
        const string transType = "02"; // Hoàn toàn phần
        const string ipAddr = "127.0.0.1";
        const string orderInfo = "Hoan tien giao dich HoaSap";
        const string createdBy = "Admin";

        // Chuỗi ký định dạng pipe (|) theo tài liệu VNPay refund
        var rawData = $"{requestId}|{version}|{command}|{cfg.VnPayTmnCode}|{transType}|{request.OriginalTransactionCode}|{amountInVnPayUnits}||{transDate}|{createdBy}|{createDate}|{ipAddr}|{orderInfo}";
        var checksum = Sign(rawData, cfg.VnPayHashSecret);

        var payload = new
        {
            vnp_RequestId = requestId,
            vnp_Version = version,
            vnp_Command = command,
            vnp_TmnCode = cfg.VnPayTmnCode,
            vnp_TransactionType = transType,
            vnp_TxnRef = request.OriginalTransactionCode,
            vnp_Amount = amountInVnPayUnits,
            vnp_OrderInfo = orderInfo,
            vnp_TransactionNo = "",
            vnp_TransactionDate = transDate,
            vnp_CreateBy = createdBy,
            vnp_CreateDate = createDate,
            vnp_IpAddr = ipAddr,
            vnp_SecureHash = checksum
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, cfg.VnPayApiUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        using var response = await http.SendAsync(httpRequest, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("VNPay sandbox refund request failed.");

        var raw = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(raw);
        var responseCode = doc.RootElement.TryGetProperty("vnp_ResponseCode", out var rc) ? rc.GetString() : "";
        var transNo = doc.RootElement.TryGetProperty("vnp_TransactionNo", out var tn) ? tn.GetString() : "";

        if (responseCode != "00")
            return new GatewayRefundResult(false, request.OriginalTransactionCode, $"resultCode={responseCode}");

        return new GatewayRefundResult(true, transNo ?? request.OriginalTransactionCode, null);
    }

    /// <summary>
    /// Truy vấn trạng thái giao dịch từ VNPay (Command: querydr).
    /// </summary>
    public async Task<GatewayPaymentQueryResult> QueryAsync(string providerKey, decimal expectedAmount, CancellationToken ct = default)
    {
        cfg.Validate();
        if (cfg.Provider != "VnPaySandbox")
            throw new InvalidOperationException("VNPay adapter is not enabled for this environment.");

        var suffix = IntentSuffix(providerKey);
        var txnRef = "I" + suffix;
        var requestId = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow.AddHours(7);
        var createDate = now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var transDate = createDate;

        const string version = "2.1.0";
        const string command = "querydr";
        const string ipAddr = "127.0.0.1";
        const string orderInfo = "Truy van giao dich";

        // Chuỗi ký định dạng pipe (|) theo tài liệu VNPay querydr
        var rawData = $"{requestId}|{version}|{command}|{cfg.VnPayTmnCode}|{txnRef}|{transDate}|{createDate}|{ipAddr}|{orderInfo}";
        var checksum = Sign(rawData, cfg.VnPayHashSecret);

        var payload = new
        {
            vnp_RequestId = requestId,
            vnp_Version = version,
            vnp_Command = command,
            vnp_TmnCode = cfg.VnPayTmnCode,
            vnp_TxnRef = txnRef,
            vnp_OrderInfo = orderInfo,
            vnp_TransactionDate = transDate,
            vnp_CreateDate = createDate,
            vnp_IpAddr = ipAddr,
            vnp_SecureHash = checksum
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, cfg.VnPayApiUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("VNPay sandbox query was not confirmed.");

        var rawResponse = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(rawResponse);
        var root = doc.RootElement;
        var responseCode = root.TryGetProperty("vnp_ResponseCode", out var rc) ? rc.GetString() : "";
        var transactionStatus = root.TryGetProperty("vnp_TransactionStatus", out var ts) ? ts.GetString() : "";
        var transNo = root.TryGetProperty("vnp_TransactionNo", out var tn) ? tn.GetString() : "";
        var amount = root.TryGetProperty("vnp_Amount", out var am) && long.TryParse(am.GetString(), out var a) ? a / 100m : 0m;
        var message = root.TryGetProperty("vnp_Message", out var msg) ? msg.GetString() ?? "" : "";

        var outcome = responseCode == "00" && transactionStatus == "00" ? "paid"
            : responseCode == "24" ? "canceled"
            : responseCode == "00" && transactionStatus != "00" ? "pending"
            : "unknown";

        return new GatewayPaymentQueryResult(outcome, int.TryParse(responseCode, out var code) ? code : 99, txnRef, requestId, amount, transNo ?? "", message, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>
    /// Xác thực chữ ký IPN / Webhook gửi từ VNPay.
    /// </summary>
    public bool VerifyWebhook(ReadOnlySpan<byte> payload, string? signature, string? timestamp)
    {
        try
        {
            cfg.Validate();
            var dict = ParsePayloadToDictionary(payload);
            if (!dict.TryGetValue("vnp_SecureHash", out var receivedHash) || string.IsNullOrWhiteSpace(receivedHash))
            {
                if (!string.IsNullOrWhiteSpace(signature)) receivedHash = signature;
                else return false;
            }

            var cleanParams = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (k, v) in dict)
            {
                if (k.Equals("vnp_SecureHash", StringComparison.OrdinalIgnoreCase) ||
                    k.Equals("vnp_SecureHashType", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.IsNullOrWhiteSpace(v))
                    cleanParams[k] = v;
            }

            if (cleanParams.Count == 0) return false;
            var rawData = BuildQueryString(cleanParams);
            var expectedHash = Sign(rawData, cfg.VnPayHashSecret);

            return CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expectedHash.ToLowerInvariant()),
                Encoding.ASCII.GetBytes(receivedHash.Trim().ToLowerInvariant()));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Chuyển đổi dữ liệu IPN từ VNPay thành đối tượng GatewayWebhookRequest chuẩn hóa.
    /// </summary>
    public GatewayWebhookRequest ParseWebhook(ReadOnlySpan<byte> payload)
    {
        var dict = ParsePayloadToDictionary(payload);
        if (!dict.TryGetValue("vnp_TxnRef", out var txnRef) || string.IsNullOrWhiteSpace(txnRef))
            throw new JsonException("VNPay IPN payload missing vnp_TxnRef.");

        var suffix = txnRef.StartsWith('I') ? txnRef[1..] : txnRef;
        if (suffix.Length != 32 || !Guid.TryParseExact(suffix, "N", out _))
            throw new JsonException("VNPay TxnRef does not map to a local payment intent.");

        dict.TryGetValue("vnp_TransactionNo", out var transNo);
        dict.TryGetValue("vnp_Amount", out var amountStr);
        dict.TryGetValue("vnp_ResponseCode", out var responseCode);
        dict.TryGetValue("vnp_TransactionStatus", out var transStatus);

        if (!long.TryParse(amountStr, NumberStyles.None, CultureInfo.InvariantCulture, out var vnpAmount) || vnpAmount <= 0)
            throw new JsonException("VNPay IPN amount is invalid.");

        var decimalAmount = vnpAmount / 100m;
        var isSuccess = responseCode == "00" && (string.IsNullOrEmpty(transStatus) || transStatus == "00");
        var isCanceled = responseCode == "24";

        return new GatewayWebhookRequest
        {
            ProviderKey = "intent:" + suffix,
            ProviderOrderId = txnRef,
            ProviderTransactionId = transNo?.Trim() ?? "",
            Amount = decimalAmount,
            Currency = "VND",
            Status = isSuccess ? "paid" : isCanceled ? "canceled" : "canceled"
        };
    }

    private static Dictionary<string, string> ParsePayloadToDictionary(ReadOnlySpan<byte> payload)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (payload.IsEmpty) return dict;

        var text = Encoding.UTF8.GetString(payload).Trim();
        if (text.StartsWith('{') && text.EndsWith('}'))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    dict[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString() ?? "",
                        JsonValueKind.Number => prop.Value.GetRawText(),
                        _ => prop.Value.ToString()
                    };
                }
                return dict;
            }
            catch (JsonException) { /* fallback to query string */ }
        }

        var query = text.StartsWith('?') ? text[1..] : text;
        var parts = query.Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var eq = part.IndexOf('=');
            if (eq < 0) continue;
            var key = WebUtility.UrlDecode(part[..eq]);
            var val = WebUtility.UrlDecode(part[(eq + 1)..]);
            dict[key] = val;
        }
        return dict;
    }

    private static string BuildQueryString(SortedDictionary<string, string> parameters)
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in parameters)
        {
            if (string.IsNullOrEmpty(v)) continue;
            if (sb.Length > 0) sb.Append('&');
            sb.Append(WebUtility.UrlEncode(k));
            sb.Append('=');
            sb.Append(WebUtility.UrlEncode(v));
        }
        return sb.ToString();
    }

    private static string Sign(string data, string secret)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var dataBytes = Encoding.UTF8.GetBytes(data);
        var hashBytes = HMACSHA512.HashData(keyBytes, dataBytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static string IntentSuffix(string providerKey)
    {
        const string prefix = "intent:";
        var suffix = providerKey.StartsWith(prefix, StringComparison.Ordinal) ? providerKey[prefix.Length..] : "";
        if (suffix.Length != 32 || !Guid.TryParseExact(suffix, "N", out _))
            throw new InvalidOperationException("VNPay payment intent key is invalid.");
        return suffix;
    }
}
