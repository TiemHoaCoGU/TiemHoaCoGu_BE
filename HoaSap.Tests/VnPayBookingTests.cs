using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using HoaSap.BLL.Workshops;
using HoaSap.DAL;
using HoaSap.Domain;
using HoaSap.Domain.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HoaSap.Tests;

public sealed class VnPayApiFixture : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"hoasap-vnpay-{Guid.NewGuid():N}.db");
    public FakeVnPayHandler Handler { get; } = new();
    public const string AdminEmail = "qa-admin@example.test";
    public const string AdminPassword = "A-very-long-test-password-123!";
    public const string TestJwtKey = "qa-only-key-with-at-least-32-bytes-123456";
    public const string TmnCode = "TESTTMN";
    public const string HashSecret = "test-hash-secret-key-1234567890";
    internal string? CachedAdminToken { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite",
            ["ConnectionStrings:DefaultConnection"] = $"Data Source={_dbPath}",
            ["Media:StoragePath"] = _dbPath + "-media",
            ["Database:Initialize"] = "true",
            ["Database:Seed"] = "false",
            ["RateLimiting:PermitLimit"] = "100000",
            ["RateLimiting:AuthPermitLimit"] = "1000",
            ["Jwt:Key"] = TestJwtKey,
            ["Jwt:Issuer"] = "qa-tests",
            ["Jwt:Audience"] = "qa-tests",
            ["BootstrapAdmin:Email"] = AdminEmail,
            ["BootstrapAdmin:Password"] = AdminPassword,
            ["BootstrapAdmin:PhoneNumber"] = "0900000001",
            ["BootstrapAdmin:FullName"] = "QA Admin",
            ["Shipping:Carrier:Origin"] = "Synthetic QA warehouse",
            ["Site:PublicUrl"] = "https://shop.example.test",
            ["Payments:VnPay:TmnCode"] = TmnCode,
            ["Payments:VnPay:HashSecret"] = HashSecret,
            ["Payments:VnPay:PayUrl"] = "https://sandbox.vnpayment.vn/paymentv2/vpcpay.html",
            ["Payments:VnPay:ApiUrl"] = "https://sandbox.vnpayment.vn/merchant_webapi/api/transaction"
        }));
        builder.ConfigureTestServices(services => services.AddHttpClient<VnPayClient>(client => client.DefaultRequestHeaders.UserAgent.ParseAdd("HoaSap/1.0")).ConfigurePrimaryHttpMessageHandler(() => Handler));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-shm"); File.Delete(_dbPath + "-wal"); } catch { }
        try { Directory.Delete(_dbPath + "-media", true); } catch { }
    }
}

public sealed class FakeVnPayHandler : HttpMessageHandler
{
    public readonly List<CapturedVnPayRequest> Requests = [];
    public Func<HttpRequestMessage, string, Task<HttpResponseMessage>>? Responder;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new CapturedVnPayRequest(request.Method, request.RequestUri?.ToString() ?? "", body));
        if (Responder is not null) return await Responder(request, body);
        return Json(new { vnp_ResponseCode = "99" }, HttpStatusCode.BadRequest);
    }

    public static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
}

public sealed record CapturedVnPayRequest(HttpMethod Method, string Url, string Body);

public sealed class DisabledVnPayFixture : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"hoasap-vnpay-off-{Guid.NewGuid():N}.db");
    public const string AdminEmail = "qa-admin@example.test";
    public const string AdminPassword = "A-very-long-test-password-123!";
    internal string? CachedAdminToken { get; set; }
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite", ["ConnectionStrings:DefaultConnection"] = $"Data Source={_dbPath}", ["Media:StoragePath"] = _dbPath + "-media",
            ["Database:Initialize"] = "true", ["Database:Seed"] = "false", ["RateLimiting:PermitLimit"] = "100000", ["RateLimiting:AuthPermitLimit"] = "1000",
            ["Jwt:Key"] = VnPayApiFixture.TestJwtKey, ["Jwt:Issuer"] = "qa-tests", ["Jwt:Audience"] = "qa-tests",
            ["BootstrapAdmin:Email"] = AdminEmail, ["BootstrapAdmin:Password"] = AdminPassword, ["BootstrapAdmin:PhoneNumber"] = "0900000001", ["BootstrapAdmin:FullName"] = "QA Admin",
            ["Shipping:Carrier:Origin"] = "Synthetic QA warehouse", ["Site:PublicUrl"] = "https://shop.example.test"
        }));
    }
    protected override void Dispose(bool disposing)
    { base.Dispose(disposing); try { File.Delete(_dbPath); } catch { } try { File.Delete(_dbPath + "-shm"); File.Delete(_dbPath + "-wal"); } catch { } try { Directory.Delete(_dbPath + "-media", true); } catch { } }
}

[CollectionDefinition("vnpay", DisableParallelization = true)]
public sealed class VnPayCollection : ICollectionFixture<VnPayApiFixture> { }

[Collection("vnpay")]
public sealed class VnPayBookingTests(VnPayApiFixture fixture)
{
    private readonly HttpClient Client = fixture.CreateClient();

    [Fact]
    public async Task CreateLink_ReturnsSignedUrl_SortedEncodedAmountTimes100_AndStoresAwaiting()
    {
        var (customer, _, booking) = await SetupAsync(price: 100000m);
        var res = await customer.PostAsync($"/api/bookings/{booking.Id}/vnpay", null);
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, body);
        using var json = JsonDocument.Parse(body);
        var url = json.RootElement.GetProperty("paymentUrl").GetString()!;
        var txnRef = json.RootElement.GetProperty("txnRef").GetString()!;
        Assert.StartsWith("https://sandbox.vnpayment.vn/paymentv2/vpcpay.html?", url);
        Assert.Equal(200000, json.RootElement.GetProperty("amount").GetInt32());
        Assert.Matches("^B[A-F0-9]{15}$", txnRef);

        var q = ParseUrlQuery(url);
        Assert.Equal("2.1.0", q["vnp_Version"]);
        Assert.Equal(VnPayApiFixture.TmnCode, q["vnp_TmnCode"]);
        Assert.Equal("20000000", q["vnp_Amount"]);
        Assert.Equal(txnRef, q["vnp_TxnRef"]);
        Assert.Equal("vn", q["vnp_Locale"]);
        Assert.Equal("VND", q["vnp_CurrCode"]);
        Assert.Contains("/thanh-toan?bookingId=" + booking.Id, q["vnp_ReturnUrl"]);
        Assert.Matches("^\\d{14}$", q["vnp_CreateDate"]);
        Assert.Matches("^\\d{14}$", q["vnp_ExpireDate"]);
        var signed = new SortedDictionary<string, string>(q.Where(x => x.Key.StartsWith("vnp_") && x.Key != "vnp_SecureHash" && x.Key != "vnp_SecureHashType").ToDictionary(x => x.Key, x => x.Value), StringComparer.Ordinal);
        var data = string.Join("&", signed.Select(x => $"{System.Net.WebUtility.UrlEncode(x.Key)}={System.Net.WebUtility.UrlEncode(x.Value)}"));
        Assert.Equal(VnSign(data), q["vnp_SecureHash"]);
        // ordinal sort
        var keys = signed.Keys.ToList();
        Assert.Equal(keys.OrderBy(x => x, StringComparer.Ordinal).ToList(), keys);

        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HoaSapDbContext>();
        var payment = await db.Payments.AsNoTracking().SingleAsync(x => x.BookingId == booking.Id && x.PaymentMethod == "VNPay");
        Assert.Equal("AwaitingPayment", payment.PaymentStatus);
        Assert.Equal($"VNP-{txnRef}", payment.TransactionCode);
        Assert.Equal(200000m, payment.Amount);
    }

    [Fact]
    public async Task SecondStart_ReusesSameUrl()
    {
        var (customer, _, booking) = await SetupAsync(price: 100000m);
        var a = await (await customer.PostAsync($"/api/bookings/{booking.Id}/vnpay", null)).Content.ReadAsStringAsync();
        var b = await (await customer.PostAsync($"/api/bookings/{booking.Id}/vnpay", null)).Content.ReadAsStringAsync();
        using var aj = JsonDocument.Parse(a); using var bj = JsonDocument.Parse(b);
        Assert.Equal(aj.RootElement.GetProperty("txnRef").GetString(), bj.RootElement.GetProperty("txnRef").GetString());
        Assert.Equal(aj.RootElement.GetProperty("paymentUrl").GetString(), bj.RootElement.GetProperty("paymentUrl").GetString());
    }

    [Fact]
    public async Task ReturnWithValidHash_ConfirmsBookingAndStagesOneTicketEmail()
    {
        var (customer, _, booking) = await SetupAsync(price: 100000m);
        var start = await StartAsync(customer, booking.Id);
        var query = SignedReturn(start.TxnRef, 200000, "00", "00");
        var ret = await customer.PostAsync($"/api/bookings/{booking.Id}/vnpay/return", Json(new { query }));
        Assert.True(ret.IsSuccessStatusCode, await ret.Content.ReadAsStringAsync());
        using var rj = JsonDocument.Parse(await ret.Content.ReadAsStringAsync());
        Assert.True(rj.RootElement.GetProperty("paid").GetBoolean());
        Assert.Equal("paid", rj.RootElement.GetProperty("lastResult").GetString());
        Assert.Equal(0m, rj.RootElement.GetProperty("remainingAmount").GetDecimal());

        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HoaSapDbContext>();
            var b = await db.WorkshopBookings.AsNoTracking().SingleAsync(x => x.Id == booking.Id);
            Assert.Equal("confirmed", b.BookingStatus);
            Assert.Equal(0m, b.RemainingAmount);
            var p = await db.Payments.AsNoTracking().SingleAsync(x => x.BookingId == booking.Id && x.PaymentMethod == "VNPay");
            Assert.Equal("Paid", p.PaymentStatus);
            var emails = await db.Set<NotificationOutbox>().AsNoTracking().Where(x => x.EventType == "WorkshopTicketIssued" && x.PayloadJson.Contains(booking.Id.ToString())).ToListAsync();
            Assert.Single(emails);
        }

        // idempotent
        (await customer.PostAsync($"/api/bookings/{booking.Id}/vnpay/return", Json(new { query }))).EnsureSuccessStatusCode();
        using var scope2 = fixture.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<HoaSapDbContext>();
        Assert.Single(await db2.Set<NotificationOutbox>().AsNoTracking().Where(x => x.EventType == "WorkshopTicketIssued" && x.PayloadJson.Contains(booking.Id.ToString())).ToListAsync());
    }

    [Fact]
    public async Task TamperedHash_Returns400_NoChange()
    {
        var (customer, _, booking) = await SetupAsync(price: 100000m);
        var start = await StartAsync(customer, booking.Id);
        var query = SignedReturn(start.TxnRef, 200000, "00", "00");
        query = query.Replace("vnp_SecureHash=", "vnp_SecureHash=00"); // tamper
        var ret = await customer.PostAsync($"/api/bookings/{booking.Id}/vnpay/return", Json(new { query }));
        Assert.Equal(HttpStatusCode.BadRequest, ret.StatusCode);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HoaSapDbContext>();
        Assert.Equal("pending", (await db.WorkshopBookings.AsNoTracking().SingleAsync(x => x.Id == booking.Id)).BookingStatus);
        Assert.Equal("AwaitingPayment", (await db.Payments.AsNoTracking().SingleAsync(x => x.BookingId == booking.Id && x.PaymentMethod == "VNPay")).PaymentStatus);
    }

    [Fact]
    public async Task Response24_CancelsPayment()
    {
        var (customer, _, booking) = await SetupAsync(price: 100000m);
        var start = await StartAsync(customer, booking.Id);
        var query = SignedReturn(start.TxnRef, 200000, "24", "02");
        var ret = await customer.PostAsync($"/api/bookings/{booking.Id}/vnpay/return", Json(new { query }));
        Assert.True(ret.IsSuccessStatusCode, await ret.Content.ReadAsStringAsync());
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HoaSapDbContext>();
        Assert.Equal("pending", (await db.WorkshopBookings.AsNoTracking().SingleAsync(x => x.Id == booking.Id)).BookingStatus);
        Assert.Equal("Canceled", (await db.Payments.AsNoTracking().SingleAsync(x => x.BookingId == booking.Id && x.PaymentMethod == "VNPay")).PaymentStatus);
    }

    [Fact]
    public async Task AmountMismatch_NotPaid()
    {
        var (customer, _, booking) = await SetupAsync(price: 100000m);
        var start = await StartAsync(customer, booking.Id);
        var query = SignedReturn(start.TxnRef, 1000, "00", "00"); // wrong amount
        var ret = await customer.PostAsync($"/api/bookings/{booking.Id}/vnpay/return", Json(new { query }));
        Assert.True(ret.IsSuccessStatusCode, await ret.Content.ReadAsStringAsync());
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HoaSapDbContext>();
        Assert.Equal("pending", (await db.WorkshopBookings.AsNoTracking().SingleAsync(x => x.Id == booking.Id)).BookingStatus);
        Assert.Equal("AwaitingPayment", (await db.Payments.AsNoTracking().SingleAsync(x => x.BookingId == booking.Id && x.PaymentMethod == "VNPay")).PaymentStatus);
    }

    [Fact]
    public async Task Ipn_Codes_00_02_97_01_04()
    {
        var (customer, _, booking) = await SetupAsync(price: 100000m);
        var start = await StartAsync(customer, booking.Id);

        // 97 invalid signature
        var bad = SignedReturn(start.TxnRef, 200000, "00", "00").Replace("vnp_SecureHash=", "vnp_SecureHash=00");
        var raw97 = await (await Client.GetAsync($"/api/vnpay/ipn?{bad}")).Content.ReadAsStringAsync();
        Assert.Contains("\"RspCode\"", raw97);
        Assert.Contains("\"Message\"", raw97);
        using (var d97 = JsonDocument.Parse(raw97)) Assert.Equal("97", d97.RootElement.GetProperty("RspCode").GetString());

        // 01 not found
        var nf = SignedReturn("B" + new string('A', 15), 200000, "00", "00");
        var r01 = await JsonAsync(await Client.GetAsync($"/api/vnpay/ipn?{nf}"));
        Assert.Equal("01", r01.GetProperty("RspCode").GetString());
        Assert.NotEmpty(r01.GetProperty("Message").GetString()!);

        // 04 invalid amount
        var ia = SignedReturn(start.TxnRef, 1, "00", "00");
        var r04 = await JsonAsync(await Client.GetAsync($"/api/vnpay/ipn?{ia}"));
        Assert.Equal("04", r04.GetProperty("RspCode").GetString());

        // 00 ok (confirms)
        var ok = SignedReturn(start.TxnRef, 200000, "00", "00");
        var r00 = await JsonAsync(await Client.GetAsync($"/api/vnpay/ipn?{ok}"));
        Assert.Equal("00", r00.GetProperty("RspCode").GetString());
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HoaSapDbContext>();
            Assert.Equal("confirmed", (await db.WorkshopBookings.AsNoTracking().SingleAsync(x => x.Id == booking.Id)).BookingStatus);
        }

        // 02 already confirmed
        var r02 = await JsonAsync(await Client.GetAsync($"/api/vnpay/ipn?{ok}"));
        Assert.Equal("02", r02.GetProperty("RspCode").GetString());
    }

    [Fact]
    public async Task QueryDr_PollPath_ConfirmsWithSignedResponse()
    {
        fixture.Handler.Requests.Clear();
        var (customer, _, booking) = await SetupAsync(price: 100000m);
        var start = await StartAsync(customer, booking.Id);
        fixture.Handler.Responder = (req, body) =>
        {
            using var doc = JsonDocument.Parse(body);
            var txnRef = doc.RootElement.GetProperty("vnp_TxnRef").GetString()!;
            var resp = SignedQueryDrResponse(txnRef, 200000, "00", "00", "00");
            return Task.FromResult(FakeVnPayHandler.Json(resp));
        };

        var sync = await customer.PostAsync($"/api/bookings/{booking.Id}/vnpay/sync", null);
        Assert.True(sync.IsSuccessStatusCode, await sync.Content.ReadAsStringAsync());
        using var sj = JsonDocument.Parse(await sync.Content.ReadAsStringAsync());
        Assert.True(sj.RootElement.GetProperty("paid").GetBoolean());
        Assert.NotEmpty(fixture.Handler.Requests);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HoaSapDbContext>();
        Assert.Equal("confirmed", (await db.WorkshopBookings.AsNoTracking().SingleAsync(x => x.Id == booking.Id)).BookingStatus);
        Assert.Equal("Paid", (await db.Payments.AsNoTracking().SingleAsync(x => x.BookingId == booking.Id && x.PaymentMethod == "VNPay")).PaymentStatus);
    }

    [Fact]
    public async Task QueryDr_SendsBufferedJsonWithContentLengthAndContentType()
    {
        fixture.Handler.Requests.Clear();
        long? contentLength = null;
        string? mediaType = null;
        string? userAgent = null;
        var (customer, _, booking) = await SetupAsync(price: 100000m);
        var start = await StartAsync(customer, booking.Id);
        fixture.Handler.Responder = (req, body) =>
        {
            contentLength = req.Content?.Headers.ContentLength;
            mediaType = req.Content?.Headers.ContentType?.MediaType;
            userAgent = req.Headers.UserAgent.ToString();
            using var doc = JsonDocument.Parse(body);
            var txnRef = doc.RootElement.GetProperty("vnp_TxnRef").GetString()!;
            var resp = SignedQueryDrResponse(txnRef, 200000, "00", "00", "00");
            return Task.FromResult(FakeVnPayHandler.Json(resp));
        };

        var sync = await customer.PostAsync($"/api/bookings/{booking.Id}/vnpay/sync", null);
        Assert.True(sync.IsSuccessStatusCode, await sync.Content.ReadAsStringAsync());
        Assert.NotNull(contentLength);
        Assert.True(contentLength > 0);
        Assert.Equal("application/json", mediaType);
        Assert.False(string.IsNullOrWhiteSpace(userAgent));
    }

    [Fact]
    public async Task QueryDr_Response99WithoutHash_IsUnknown_NoStateChange()
    {
        fixture.Handler.Requests.Clear();
        var (customer, _, booking) = await SetupAsync(price: 100000m);
        var start = await StartAsync(customer, booking.Id);
        fixture.Handler.Responder = (_, _) => Task.FromResult(FakeVnPayHandler.Json(new { vnp_ResponseCode = "99", vnp_Message = "Unexpected error" }));

        var sync = await customer.PostAsync($"/api/bookings/{booking.Id}/vnpay/sync", null);
        Assert.True(sync.IsSuccessStatusCode, await sync.Content.ReadAsStringAsync());
        using var sj = JsonDocument.Parse(await sync.Content.ReadAsStringAsync());
        Assert.False(sj.RootElement.GetProperty("paid").GetBoolean());
        Assert.Equal("pending", sj.RootElement.GetProperty("lastResult").GetString());
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HoaSapDbContext>();
        Assert.Equal("pending", (await db.WorkshopBookings.AsNoTracking().SingleAsync(x => x.Id == booking.Id)).BookingStatus);
        Assert.Equal("AwaitingPayment", (await db.Payments.AsNoTracking().SingleAsync(x => x.BookingId == booking.Id && x.PaymentMethod == "VNPay")).PaymentStatus);
    }

    [Fact]
    public async Task PaidAfterBookingCancelled_BecomesReconciliationRequired()
    {
        var (customer, _, booking) = await SetupAsync(price: 100000m);
        var start = await StartAsync(customer, booking.Id);
        (await customer.PostAsync($"/api/bookings/{booking.Id}/cancel", Json((string?)null))).EnsureSuccessStatusCode();
        var query = SignedReturn(start.TxnRef, 200000, "00", "00");
        (await customer.PostAsync($"/api/bookings/{booking.Id}/vnpay/return", Json(new { query }))).EnsureSuccessStatusCode();
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HoaSapDbContext>();
        Assert.Equal("cancelled", (await db.WorkshopBookings.AsNoTracking().SingleAsync(x => x.Id == booking.Id)).BookingStatus);
        Assert.Equal("ReconciliationRequired", (await db.Payments.AsNoTracking().SingleAsync(x => x.BookingId == booking.Id && x.PaymentMethod == "VNPay")).PaymentStatus);
    }

    [Fact]
    public async Task ExpiryWorker_SkipsWithinGraceAndExpiresAfter()
    {
        var (customer, _, booking) = await SetupAsync(price: 100000m);
        (await customer.PostAsync($"/api/bookings/{booking.Id}/vnpay", null)).EnsureSuccessStatusCode();
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HoaSapDbContext>();
            var b = await db.WorkshopBookings.SingleAsync(x => x.Id == booking.Id);
            b.HoldExpiresAt = DateTime.UtcNow.AddMinutes(-2);
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<WorkshopService>().ExpirePendingBookingsAsync();
        }
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HoaSapDbContext>();
            Assert.Equal("pending", (await db.WorkshopBookings.AsNoTracking().SingleAsync(x => x.Id == booking.Id)).BookingStatus);
            var b = await db.WorkshopBookings.SingleAsync(x => x.Id == booking.Id);
            b.HoldExpiresAt = DateTime.UtcNow.AddMinutes(-11);
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<WorkshopService>().ExpirePendingBookingsAsync();
        }
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HoaSapDbContext>();
            Assert.Equal("expired", (await db.WorkshopBookings.AsNoTracking().SingleAsync(x => x.Id == booking.Id)).BookingStatus);
            Assert.Equal("Expired", (await db.Payments.AsNoTracking().SingleAsync(x => x.BookingId == booking.Id && x.PaymentMethod == "VNPay")).PaymentStatus);
        }
    }

    [Fact]
    public async Task AnotherCustomer_CannotStartOrSync()
    {
        var (customer, _, booking) = await SetupAsync(price: 100000m);
        (await customer.PostAsync($"/api/bookings/{booking.Id}/vnpay", null)).EnsureSuccessStatusCode();
        using var other = await CustomerClientAsync();
        var start = await other.PostAsync($"/api/bookings/{booking.Id}/vnpay", null);
        Assert.True(start.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden, start.StatusCode.ToString());
        var sync = await other.PostAsync($"/api/bookings/{booking.Id}/vnpay/sync", null);
        Assert.True(sync.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden, sync.StatusCode.ToString());
    }

    private sealed record BookingInfo(Guid Id, string Code, decimal Total);
    private sealed record StartInfo(string TxnRef, int Amount);

    private async Task<(HttpClient customer, Guid sessionId, BookingInfo booking)> SetupAsync(decimal price)
    {
        using var admin = await AdminClientAsync();
        var sessionId = await CreateSessionAsync(admin, price);
        var customer = await CustomerClientAsync();
        var booking = await BookAsync(customer, sessionId, 2);
        return (customer, sessionId, booking);
    }

    private async Task<HttpClient> AdminClientAsync()
    {
        if (fixture.CachedAdminToken is null)
        {
            var res = await Client.PostAsync("/api/auth/login", Json(new { email = VnPayApiFixture.AdminEmail, password = VnPayApiFixture.AdminPassword }));
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            fixture.CachedAdminToken = doc.RootElement.GetProperty("accessToken").GetString();
        }
        var c = fixture.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.CachedAdminToken);
        return c;
    }

    private async Task<HttpClient> CustomerClientAsync()
    {
        var email = $"vnpay-{Guid.NewGuid():N}@example.test";
        var res = await Client.PostAsync("/api/auth/register", Json(new { fullName = "VNPAY Guest", phoneNumber = "09" + Random.Shared.Next(100000000, 999999999), email, password = "Password-123!", role = "Admin" }));
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var token = doc.RootElement.GetProperty("accessToken").GetString();
        var c = fixture.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private static async Task<Guid> CreateSessionAsync(HttpClient admin, decimal price)
    {
        var w = await admin.PostAsync("/api/workshops", Json(new { code = "V-" + Guid.NewGuid().ToString("N")[..10], name = "VNPAY Workshop", basePrice = price, duration = 60, maxCapacity = 10, isActive = true }));
        w.EnsureSuccessStatusCode();
        using var wj = JsonDocument.Parse(await w.Content.ReadAsStringAsync());
        var wid = wj.RootElement.GetProperty("id").GetGuid();
        var s = await admin.PostAsync($"/api/workshops/{wid}/sessions", Json(new { sessionDate = DateTime.UtcNow.AddDays(3).Date, startTime = "10:00:00", endTime = "11:00:00", location = "Studio", capacity = 10 }));
        s.EnsureSuccessStatusCode();
        using var sj = JsonDocument.Parse(await s.Content.ReadAsStringAsync());
        return sj.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<BookingInfo> BookAsync(HttpClient customer, Guid sessionId, int tickets)
    {
        var res = await customer.PostAsync($"/api/bookings/sessions/{sessionId}", Json(new { ticketCount = tickets, customerName = "Guest", customerPhone = "0901234567", idempotencyKey = "v-" + Guid.NewGuid().ToString("N") }));
        res.EnsureSuccessStatusCode();
        using var j = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return new BookingInfo(j.RootElement.GetProperty("id").GetGuid(), j.RootElement.GetProperty("bookingCode").GetString()!, j.RootElement.GetProperty("totalAmount").GetDecimal());
    }

    private static async Task<StartInfo> StartAsync(HttpClient customer, Guid bookingId)
    {
        var res = await customer.PostAsync($"/api/bookings/{bookingId}/vnpay", null);
        res.EnsureSuccessStatusCode();
        using var j = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return new StartInfo(j.RootElement.GetProperty("txnRef").GetString()!, j.RootElement.GetProperty("amount").GetInt32());
    }

    private static string SignedReturn(string txnRef, int amountVnd, string responseCode, string transactionStatus)
    {
        var p = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["vnp_Amount"] = ((long)amountVnd * 100).ToString(CultureInfo.InvariantCulture),
            ["vnp_BankCode"] = "NCB",
            ["vnp_OrderInfo"] = "Thanh toan ve",
            ["vnp_ResponseCode"] = responseCode,
            ["vnp_TmnCode"] = VnPayApiFixture.TmnCode,
            ["vnp_TransactionNo"] = "14000001",
            ["vnp_TransactionStatus"] = transactionStatus,
            ["vnp_TxnRef"] = txnRef
        };
        var data = string.Join("&", p.Select(x => $"{System.Net.WebUtility.UrlEncode(x.Key)}={System.Net.WebUtility.UrlEncode(x.Value)}"));
        p["vnp_SecureHash"] = VnSign(data);
        return string.Join("&", p.Select(x => $"{System.Net.WebUtility.UrlEncode(x.Key)}={System.Net.WebUtility.UrlEncode(x.Value)}"));
    }

    private static Dictionary<string, object> SignedQueryDrResponse(string txnRef, int amountVnd, string queryResponseCode, string responseCode, string transactionStatus)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["vnp_ResponseId"] = "RID1",
            ["vnp_Command"] = "querydr",
            ["vnp_ResponseCode"] = responseCode,
            ["vnp_Message"] = "ok",
            ["vnp_TmnCode"] = VnPayApiFixture.TmnCode,
            ["vnp_TxnRef"] = txnRef,
            ["vnp_Amount"] = ((long)amountVnd * 100).ToString(CultureInfo.InvariantCulture),
            ["vnp_BankCode"] = "NCB",
            ["vnp_PayDate"] = "20261107120000",
            ["vnp_TransactionNo"] = "14000001",
            ["vnp_TransactionType"] = "01",
            ["vnp_TransactionStatus"] = transactionStatus,
            ["vnp_OrderInfo"] = "Thanh toan ve"
        };
        var hashData = string.Join("|", fields["vnp_ResponseId"], fields["vnp_Command"], responseCode, fields["vnp_Message"], fields["vnp_TmnCode"], fields["vnp_TxnRef"], fields["vnp_Amount"], fields["vnp_BankCode"], fields["vnp_PayDate"], fields["vnp_TransactionNo"], fields["vnp_TransactionType"], fields["vnp_TransactionStatus"], fields["vnp_OrderInfo"]);
        var obj = fields.ToDictionary(x => x.Key, x => (object)x.Value);
        obj["vnp_ResponseCode"] = queryResponseCode; // outer query response
        // recompute hash with actual response code field value used in hash? spec says ResponseCode in hash is vnp_ResponseCode
        var hashData2 = string.Join("|", fields["vnp_ResponseId"], fields["vnp_Command"], queryResponseCode, fields["vnp_Message"], fields["vnp_TmnCode"], fields["vnp_TxnRef"], fields["vnp_Amount"], fields["vnp_BankCode"], fields["vnp_PayDate"], fields["vnp_TransactionNo"], fields["vnp_TransactionType"], fields["vnp_TransactionStatus"], fields["vnp_OrderInfo"]);
        obj["vnp_SecureHash"] = VnSign(hashData2);
        return obj;
    }

    private static string VnSign(string value)
    {
        using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(VnPayApiFixture.HashSecret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static SortedDictionary<string, string> ParseUrlQuery(string url)
    {
        var dict = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var q = url.Contains('?') ? url[(url.IndexOf('?') + 1)..] : url;
        foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = part.IndexOf('=');
            var key = System.Net.WebUtility.UrlDecode(i < 0 ? part : part[..i]) ?? "";
            var val = System.Net.WebUtility.UrlDecode(i < 0 ? "" : part[(i + 1)..]) ?? "";
            if (key.Length > 0) dict[key] = val;
        }
        return dict;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage res) { var t = await res.Content.ReadAsStringAsync(); using var d = JsonDocument.Parse(t); return d.RootElement.Clone(); }
    private static StringContent Json(object? value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
}

public sealed class VnPayDisabledTests(DisabledVnPayFixture fixture) : IClassFixture<DisabledVnPayFixture>
{
    [Fact]
    public async Task DisabledConfig_Returns503()
    {
        var client = fixture.CreateClient();
        var login = await client.PostAsync("/api/auth/login", new StringContent(JsonSerializer.Serialize(new { email = DisabledVnPayFixture.AdminEmail, password = DisabledVnPayFixture.AdminPassword }), Encoding.UTF8, "application/json"));
        login.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var token = doc.RootElement.GetProperty("accessToken").GetString();
        var authed = fixture.CreateClient();
        authed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await authed.PostAsync($"/api/bookings/{Guid.NewGuid()}/vnpay", null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
    }
}
