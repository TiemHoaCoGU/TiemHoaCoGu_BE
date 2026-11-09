using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HoaSap.BLL.Workshops;

public sealed class VnPayOptions
{
    public string TmnCode { get; set; } = "";
    public string HashSecret { get; set; } = "";
    public string PayUrl { get; set; } = "https://sandbox.vnpayment.vn/paymentv2/vpcpay.html";
    public string ApiUrl { get; set; } = "https://sandbox.vnpayment.vn/merchant_webapi/api/transaction";
    public bool Enabled => !string.IsNullOrWhiteSpace(TmnCode) && !string.IsNullOrWhiteSpace(HashSecret);
}

public sealed class VnPayException(string message) : Exception(message);

public sealed record VnPayResult(string TmnCode, string TxnRef, long Amount, string ResponseCode, string TransactionStatus, string TransactionNo, string BankCode, bool SignatureValid, string RawQuery);
public sealed record VnPayQueryResult(bool Ok, bool Found, bool Paid, long Amount, string ResponseCode, string TransactionStatus, string TransactionNo, string BankCode, bool SignatureValid, bool HasSecureHash, string RawJson);

public sealed class VnPayClient
{
    private readonly HttpClient http;
    private readonly VnPayOptions options;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeZoneInfo VietnamTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");

    public VnPayClient(HttpClient http, VnPayOptions options)
    {
        this.http = http;
        this.options = options;
        http.Timeout = TimeSpan.FromSeconds(15);
    }

    public bool Enabled => options.Enabled;
    public string TmnCode => options.TmnCode;

    public string BuildPaymentUrl(string txnRef, int amountVnd, string orderInfo, string returnUrl, string ipAddr, DateTime createUtc, DateTime expireUtc)
    {
        var p = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["vnp_Version"] = "2.1.0",
            ["vnp_Command"] = "pay",
            ["vnp_TmnCode"] = options.TmnCode,
            ["vnp_Amount"] = ((long)amountVnd * 100).ToString(CultureInfo.InvariantCulture),
            ["vnp_CreateDate"] = Gmt7(createUtc),
            ["vnp_CurrCode"] = "VND",
            ["vnp_ExpireDate"] = Gmt7(expireUtc),
            ["vnp_IpAddr"] = string.IsNullOrWhiteSpace(ipAddr) ? "127.0.0.1" : ipAddr,
            ["vnp_Locale"] = "vn",
            ["vnp_OrderInfo"] = orderInfo,
            ["vnp_OrderType"] = "other",
            ["vnp_ReturnUrl"] = returnUrl,
            ["vnp_TxnRef"] = txnRef
        };
        var query = BuildQuery(p);
        return $"{options.PayUrl}?{query}&vnp_SecureHash={Sign(query)}";
    }

    public bool VerifyReturn(IEnumerable<KeyValuePair<string, string>> query, out VnPayResult result)
    {
        var all = query.Where(x => !string.IsNullOrEmpty(x.Key)).ToDictionary(x => x.Key, x => x.Value ?? "", StringComparer.Ordinal);
        var received = all.TryGetValue("vnp_SecureHash", out var h) ? h : "";
        var signed = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in all.Where(x => x.Key.StartsWith("vnp_", StringComparison.Ordinal) && x.Key != "vnp_SecureHash" && x.Key != "vnp_SecureHashType")) signed[kv.Key] = kv.Value;
        var data = BuildQuery(signed);
        var valid = !string.IsNullOrWhiteSpace(received) && FixedEquals(Sign(data), received);
        all.TryGetValue("vnp_Amount", out var a);
        long.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var amount);
        result = new VnPayResult(Get(all, "vnp_TmnCode"), Get(all, "vnp_TxnRef"), amount, Get(all, "vnp_ResponseCode"), Get(all, "vnp_TransactionStatus"), Get(all, "vnp_TransactionNo"), Get(all, "vnp_BankCode"), valid, data);
        return valid;
    }

    public async Task<VnPayQueryResult> QueryAsync(string txnRef, string transactionDate, string orderInfo, string ipAddr, CancellationToken ct = default)
    {
        var requestId = Guid.NewGuid().ToString("N")[..32];
        var now = Gmt7(DateTime.UtcNow);
        var hashData = string.Join("|", requestId, "2.1.0", "querydr", options.TmnCode, txnRef, transactionDate, now, string.IsNullOrWhiteSpace(ipAddr) ? "127.0.0.1" : ipAddr, orderInfo);
        var body = new Dictionary<string, string> { ["vnp_RequestId"] = requestId, ["vnp_Version"] = "2.1.0", ["vnp_Command"] = "querydr", ["vnp_TmnCode"] = options.TmnCode, ["vnp_TxnRef"] = txnRef, ["vnp_OrderInfo"] = orderInfo, ["vnp_TransactionDate"] = transactionDate, ["vnp_CreateDate"] = now, ["vnp_IpAddr"] = string.IsNullOrWhiteSpace(ipAddr) ? "127.0.0.1" : ipAddr, ["vnp_SecureHash"] = Sign(hashData) };
        using var req = new HttpRequestMessage(HttpMethod.Post, options.ApiUrl) { Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json") };
        using var res = await http.SendAsync(req, ct);
        var raw = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            var snippet = raw.Length > 200 ? raw[..200] : raw;
            throw new VnPayException($"VNPAY query error {(int)res.StatusCode}: {snippet}");
        }
        using var doc = JsonDocument.Parse(raw);
        var r = doc.RootElement;
        var respCode = Str(r, "vnp_ResponseCode");
        var txnStatus = Str(r, "vnp_TransactionStatus");
        var txnNo = Str(r, "vnp_TransactionNo");
        var bank = Str(r, "vnp_BankCode");
        var message = Str(r, "vnp_Message");
        long.TryParse(Str(r, "vnp_Amount"), NumberStyles.None, CultureInfo.InvariantCulture, out var amount);
        var responseHashData = string.Join("|", Str(r, "vnp_ResponseId"), Str(r, "vnp_Command"), respCode, message, Str(r, "vnp_TmnCode"), Str(r, "vnp_TxnRef"), Str(r, "vnp_Amount"), bank, Str(r, "vnp_PayDate"), txnNo, Str(r, "vnp_TransactionType"), txnStatus, Str(r, "vnp_OrderInfo"));
        var receivedHash = Str(r, "vnp_SecureHash");
        var hasHash = !string.IsNullOrWhiteSpace(receivedHash);
        var valid = hasHash && FixedEquals(Sign(responseHashData), receivedHash);
        return new VnPayQueryResult(respCode == "00", respCode != "91", txnStatus == "00", amount, respCode, txnStatus, txnNo, bank, valid, hasHash, raw);
    }

    public static SortedDictionary<string, string> ParseQuery(string? rawQuery)
    {
        var dict = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(rawQuery)) return dict;
        var q = rawQuery.TrimStart('?');
        foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = part.IndexOf('=');
            var key = WebUtility.UrlDecode(i < 0 ? part : part[..i]) ?? (i < 0 ? part : part[..i]);
            var val = WebUtility.UrlDecode(i < 0 ? "" : part[(i + 1)..]) ?? (i < 0 ? "" : part[(i + 1)..]);
            if (key.Length > 0) dict[key] = val;
        }
        return dict;
    }

    public string Sign(string value)
    {
        using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(options.HashSecret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static string BuildQuery(SortedDictionary<string, string> p) => string.Join("&", p.Select(x => $"{WebUtility.UrlEncode(x.Key)}={WebUtility.UrlEncode(x.Value)}"));
    private static bool FixedEquals(string a, string b)
    {
        var ab = Encoding.ASCII.GetBytes(a.ToLowerInvariant());
        var bb = Encoding.ASCII.GetBytes(b.ToLowerInvariant());
        return ab.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ab, bb);
    }
    private static string Gmt7(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), VietnamTimeZone).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
    private static string Get(Dictionary<string, string> d, string key) => d.TryGetValue(key, out var v) ? v : "";
    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : "";
}
