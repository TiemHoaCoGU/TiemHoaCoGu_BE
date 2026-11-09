using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HoaSap.BLL;
using HoaSap.BLL.Commerce;
using HoaSap.DAL;
using HoaSap.Domain;
using HoaSap.Domain.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace HoaSap.Tests;

public sealed class VnPaySandboxPaymentGatewayTests
{
    private const string TestTmnCode = "CGXZLS0Z";
    private const string TestHashSecret = "XNBCJFAKAZQSGTARRLRXNZNFZSBNACUA";
    private const string TestBaseUrl = "https://sandbox.vnpayment.vn/paymentv2/vpcpay.html";

    private static IOptions<CommerceGatewayOptions> CreateOptions(string? provider = "VnPaySandbox")
    {
        return Microsoft.Extensions.Options.Options.Create(new CommerceGatewayOptions
        {
            Provider = provider ?? "VnPaySandbox",
            VnPayTmnCode = TestTmnCode,
            VnPayHashSecret = TestHashSecret,
            VnPayBaseUrl = TestBaseUrl,
            TimeoutSeconds = 30,
            AllowedReturnOrigins = ["https://localhost:3000", "https://shop.example.test"]
        });
    }

    [Fact]
    public async Task ChargeAsync_GeneratesValidVnPayUrl_WithStrictHmacSha512Checksum()
    {
        using var httpClient = new HttpClient();
        using var gateway = new VnPaySandboxPaymentGateway(httpClient, CreateOptions());

        var intentKey = Guid.NewGuid().ToString("N");
        var orderId = Guid.NewGuid();
        var request = new GatewayChargeRequest(orderId, 150_000m, "VND", "https://shop.example.test/thanh-toan/ket-qua", "intent:" + intentKey);

        var result = await gateway.ChargeAsync(request);

        Assert.True(result.Accepted);
        Assert.Equal("I" + intentKey, result.TransactionCode);
        Assert.NotNull(result.RedirectUrl);

        var uri = new Uri(result.RedirectUrl);
        Assert.Equal("https", uri.Scheme);
        Assert.Equal("sandbox.vnpayment.vn", uri.Host);
        Assert.Equal("/paymentv2/vpcpay.html", uri.AbsolutePath);

        // Parse query params
        var queryParams = ParseQuery(uri.Query);
        Assert.Equal("2.1.0", queryParams["vnp_Version"]);
        Assert.Equal("pay", queryParams["vnp_Command"]);
        Assert.Equal(TestTmnCode, queryParams["vnp_TmnCode"]);
        Assert.Equal("15000000", queryParams["vnp_Amount"]); // 150.000 x 100
        Assert.Equal("VND", queryParams["vnp_CurrCode"]);
        Assert.Equal("I" + intentKey, queryParams["vnp_TxnRef"]);
        Assert.Equal("https://shop.example.test/thanh-toan/ket-qua", queryParams["vnp_ReturnUrl"]);
        Assert.True(queryParams.ContainsKey("vnp_SecureHash"));

        // Verify that the hash calculated by the gateway matches our independent HMAC-SHA512 calculation
        var receivedHash = queryParams["vnp_SecureHash"];
        var sortedParams = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in queryParams)
        {
            if (k != "vnp_SecureHash" && k != "vnp_SecureHashType" && !string.IsNullOrEmpty(v))
                sortedParams[k] = v;
        }

        var sb = new StringBuilder();
        foreach (var (k, v) in sortedParams)
        {
            if (sb.Length > 0) sb.Append('&');
            sb.Append(WebUtility.UrlEncode(k)).Append('=').Append(WebUtility.UrlEncode(v));
        }

        var expectedHash = Convert.ToHexString(HMACSHA512.HashData(
            Encoding.UTF8.GetBytes(TestHashSecret),
            Encoding.UTF8.GetBytes(sb.ToString())
        )).ToLowerInvariant();

        Assert.Equal(expectedHash, receivedHash.ToLowerInvariant());
    }

    [Theory]
    [InlineData(4999)]       // Dưới mức tối thiểu 5.000 VNĐ
    [InlineData(-1000)]      // Số âm
    [InlineData(100.5)]      // Lẻ xu (phải là nguyên VNĐ)
    public async Task ChargeAsync_RejectsInvalidAmounts(decimal invalidAmount)
    {
        using var httpClient = new HttpClient();
        using var gateway = new VnPaySandboxPaymentGateway(httpClient, CreateOptions());

        var request = new GatewayChargeRequest(Guid.NewGuid(), invalidAmount, "VND", "https://shop.example.test/return", "intent:" + Guid.NewGuid().ToString("N"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.ChargeAsync(request));
    }

    [Fact]
    public void VerifyWebhook_ValidSignature_ReturnsTrue()
    {
        using var httpClient = new HttpClient();
        using var gateway = new VnPaySandboxPaymentGateway(httpClient, CreateOptions());

        var intentKey = Guid.NewGuid().ToString("N");
        var rawParams = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["vnp_Amount"] = "20000000",
            ["vnp_BankCode"] = "NCB",
            ["vnp_BankTranNo"] = "VNP140001",
            ["vnp_CardType"] = "ATM",
            ["vnp_OrderInfo"] = "Thanh toan hoa sap",
            ["vnp_PayDate"] = "20261103190000",
            ["vnp_ResponseCode"] = "00",
            ["vnp_TmnCode"] = TestTmnCode,
            ["vnp_TransactionNo"] = "14123456",
            ["vnp_TransactionStatus"] = "00",
            ["vnp_TxnRef"] = "I" + intentKey
        };

        var sb = new StringBuilder();
        foreach (var (k, v) in rawParams)
        {
            if (sb.Length > 0) sb.Append('&');
            sb.Append(WebUtility.UrlEncode(k)).Append('=').Append(WebUtility.UrlEncode(v));
        }

        var hash = Convert.ToHexString(HMACSHA512.HashData(
            Encoding.UTF8.GetBytes(TestHashSecret),
            Encoding.UTF8.GetBytes(sb.ToString())
        )).ToLowerInvariant();

        var queryString = sb + "&vnp_SecureHash=" + hash;
        var payloadBytes = Encoding.UTF8.GetBytes(queryString);

        var isValid = gateway.VerifyWebhook(payloadBytes, null, null);
        Assert.True(isValid);

        var parsed = gateway.ParseWebhook(payloadBytes);
        Assert.Equal("intent:" + intentKey, parsed.ProviderKey);
        Assert.Equal("I" + intentKey, parsed.ProviderOrderId);
        Assert.Equal("14123456", parsed.ProviderTransactionId);
        Assert.Equal(200_000m, parsed.Amount);
        Assert.Equal("paid", parsed.Status);
    }

    [Fact]
    public void VerifyWebhook_TamperedData_ReturnsFalse()
    {
        using var httpClient = new HttpClient();
        using var gateway = new VnPaySandboxPaymentGateway(httpClient, CreateOptions());

        var intentKey = Guid.NewGuid().ToString("N");
        var rawParams = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["vnp_Amount"] = "20000000",
            ["vnp_ResponseCode"] = "00",
            ["vnp_TmnCode"] = TestTmnCode,
            ["vnp_TxnRef"] = "I" + intentKey
        };

        var sb = new StringBuilder();
        foreach (var (k, v) in rawParams)
        {
            if (sb.Length > 0) sb.Append('&');
            sb.Append(WebUtility.UrlEncode(k)).Append('=').Append(WebUtility.UrlEncode(v));
        }

        var hash = Convert.ToHexString(HMACSHA512.HashData(
            Encoding.UTF8.GetBytes(TestHashSecret),
            Encoding.UTF8.GetBytes(sb.ToString())
        )).ToLowerInvariant();

        // Kẻ tấn công sửa đổi số tiền nhưng giữ nguyên hash cũ
        var tamperedQuery = sb.ToString().Replace("20000000", "50000000") + "&vnp_SecureHash=" + hash;
        var payloadBytes = Encoding.UTF8.GetBytes(tamperedQuery);

        var isValid = gateway.VerifyWebhook(payloadBytes, null, null);
        Assert.False(isValid);
    }

    [Fact]
    public void ParseWebhook_CustomerCancellation_ReturnsCanceledStatus()
    {
        using var httpClient = new HttpClient();
        using var gateway = new VnPaySandboxPaymentGateway(httpClient, CreateOptions());

        var intentKey = Guid.NewGuid().ToString("N");
        var json = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["vnp_TxnRef"] = "I" + intentKey,
            ["vnp_Amount"] = "10000000",
            ["vnp_ResponseCode"] = "24", // Khách hủy giao dịch
            ["vnp_TransactionStatus"] = "02"
        });

        var parsed = gateway.ParseWebhook(Encoding.UTF8.GetBytes(json));
        Assert.Equal("canceled", parsed.Status);
        Assert.Equal(100_000m, parsed.Amount);
        Assert.Equal("intent:" + intentKey, parsed.ProviderKey);
    }

    [Fact]
    public async Task EndToEnd_GatewayPaymentService_WithVnPay_SucceedsAndSettlesOrder()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<HoaSapDbContext>().UseSqlite(connection).Options;
        await using var db = new HoaSapDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var ownerId = Guid.NewGuid();
        var order = new Order
        {
            Id = Guid.NewGuid(),
            CustomerId = ownerId,
            OrderCode = "ORD-TEST-VNPAY",
            SubTotal = 300_000m,
            TotalAmount = 300_000m,
            OrderStatus = "Confirmed",
            PaymentStatus = "Unpaid",
            ShippingAddress = "Test Address"
        };
        db.Customers.Add(new Customer { Id = ownerId, Email = "vnpay-test@example.com", FullName = "Tester" });
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        using var httpClient = new HttpClient();
        var gateway = new VnPaySandboxPaymentGateway(httpClient, CreateOptions());
        var service = new GatewayPaymentService(db, gateway, CreateOptions());

        // 1. Khách bắt đầu thanh toán VNPay
        var chargeResult = await service.StartAsync(ownerId, new GatewayPaymentRequest
        {
            OrderId = order.Id,
            Amount = 300_000m,
            ReturnUrl = "https://shop.example.test/thanh-toan/ket-qua",
            IdempotencyKey = "vnpay-key-1"
        });

        Assert.Equal(order.Id, chargeResult.OrderId);
        Assert.StartsWith("https://sandbox.vnpayment.vn/paymentv2/vpcpay.html?", chargeResult.RedirectUrl);

        var intent = await db.GatewayPaymentIntents.SingleAsync(x => x.OrderId == order.Id);
        Assert.Equal("Accepted", intent.Status);
        Assert.Equal(300_000m, intent.Amount);

        // 2. VNPay gọi IPN báo thanh toán thành công
        var ipnParams = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["vnp_Amount"] = "30000000",
            ["vnp_ResponseCode"] = "00",
            ["vnp_TransactionStatus"] = "00",
            ["vnp_TransactionNo"] = "99887766",
            ["vnp_TmnCode"] = TestTmnCode,
            ["vnp_TxnRef"] = intent.TransactionCode
        };

        var sb = new StringBuilder();
        foreach (var (k, v) in ipnParams)
        {
            if (sb.Length > 0) sb.Append('&');
            sb.Append(WebUtility.UrlEncode(k)).Append('=').Append(WebUtility.UrlEncode(v));
        }

        var ipnHash = Convert.ToHexString(HMACSHA512.HashData(
            Encoding.UTF8.GetBytes(TestHashSecret),
            Encoding.UTF8.GetBytes(sb.ToString())
        )).ToLowerInvariant();

        var ipnPayload = Encoding.UTF8.GetBytes(sb + "&vnp_SecureHash=" + ipnHash);

        var webhookResponse = await service.WebhookAsync(ipnPayload, null, null);
        Assert.True(webhookResponse.Accepted);
        Assert.False(webhookResponse.Replay);

        // 3. Kiểm tra Order và Intent đã chuyển sang Paid
        var updatedOrder = await db.Orders.SingleAsync(x => x.Id == order.Id);
        Assert.Equal("Paid", updatedOrder.PaymentStatus);

        var updatedIntent = await db.GatewayPaymentIntents.SingleAsync(x => x.Id == intent.Id);
        Assert.Equal("Paid", updatedIntent.Status);
        Assert.Equal("ResolvedPaid", updatedIntent.ReconciliationStatus);

        var payment = await db.Payments.SingleAsync(x => x.OrderId == order.Id);
        Assert.Equal("Paid", payment.PaymentStatus);
        Assert.Equal("VnPaySandbox", payment.PaymentMethod);

        // 4. Replay IPN trùng lặp phải là Idempotent (không bị lỗi, không cộng tiền 2 lần)
        var replayResponse = await service.WebhookAsync(ipnPayload, null, null);
        Assert.True(replayResponse.Accepted);
        Assert.True(replayResponse.Replay);
    }

    [Fact]
    public async Task SimulateSandboxWebhookAsync_Success_SettlesPaymentCorrectly()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var dbOptions = new DbContextOptionsBuilder<HoaSapDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new HoaSapDbContext(dbOptions);
        await db.Database.EnsureCreatedAsync();

        var customerId = Guid.NewGuid();
        var order = new Order
        {
            Id = Guid.NewGuid(),
            OrderCode = "ORD-SIM-001",
            CustomerId = customerId,
            OrderStatus = "Confirmed",
            PaymentStatus = "Unpaid",
            SubTotal = 300_000m,
            TotalAmount = 300_000m,
            ShippingAddress = "123 Test Street"
        };
        db.Customers.Add(new Customer { Id = customerId, Email = "sim1@example.com", FullName = "Tester 1" });
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        using var httpClient = new HttpClient();
        using var gateway = new VnPaySandboxPaymentGateway(httpClient, CreateOptions());
        var service = new GatewayPaymentService(db, gateway, CreateOptions());

        var simResponse = await service.SimulateSandboxWebhookAsync(customerId, order.Id, true, "00");
        Assert.True(simResponse.Accepted);

        var updatedOrder = await db.Orders.SingleAsync(x => x.Id == order.Id);
        Assert.Equal("Paid", updatedOrder.PaymentStatus);

        var payment = await db.Payments.SingleAsync(x => x.OrderId == order.Id);
        Assert.Equal("Paid", payment.PaymentStatus);
        Assert.Equal("VnPaySandbox", payment.PaymentMethod);
        Assert.Equal(300_000m, payment.Amount);
    }

    [Fact]
    public async Task SimulateSandboxWebhookAsync_Canceled_LeavesOrderUnpaid()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var dbOptions = new DbContextOptionsBuilder<HoaSapDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new HoaSapDbContext(dbOptions);
        await db.Database.EnsureCreatedAsync();

        var customerId = Guid.NewGuid();
        var order = new Order
        {
            Id = Guid.NewGuid(),
            OrderCode = "ORD-SIM-002",
            CustomerId = customerId,
            OrderStatus = "Confirmed",
            PaymentStatus = "Unpaid",
            SubTotal = 250_000m,
            TotalAmount = 250_000m,
            ShippingAddress = "123 Test Street"
        };
        db.Customers.Add(new Customer { Id = customerId, Email = "sim2@example.com", FullName = "Tester 2" });
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        using var httpClient = new HttpClient();
        using var gateway = new VnPaySandboxPaymentGateway(httpClient, CreateOptions());
        var service = new GatewayPaymentService(db, gateway, CreateOptions());

        var simResponse = await service.SimulateSandboxWebhookAsync(customerId, order.Id, false, "24");
        Assert.True(simResponse.Accepted);

        var updatedOrder = await db.Orders.SingleAsync(x => x.Id == order.Id);
        Assert.Equal("Unpaid", updatedOrder.PaymentStatus);

        var intent = await db.GatewayPaymentIntents.SingleAsync(x => x.OrderId == order.Id);
        Assert.Equal("Canceled", intent.Status);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var dict = new Dictionary<string, string>();
        var clean = query.StartsWith('?') ? query[1..] : query;
        foreach (var part in clean.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq < 0) continue;
            dict[WebUtility.UrlDecode(part[..eq])] = WebUtility.UrlDecode(part[(eq + 1)..]);
        }
        return dict;
    }
}
