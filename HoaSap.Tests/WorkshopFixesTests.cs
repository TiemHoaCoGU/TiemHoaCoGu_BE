using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HoaSap.BLL;
using HoaSap.BLL.Workshops;
using HoaSap.DAL;
using HoaSap.Domain.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HoaSap.Tests;

/// <summary>Regression tests for the workshop fixes on feat/workshop-fixes (C1/C2/M1/M2/M3/M4/M6).</summary>
public sealed class WorkshopFixesTests
{
    private static StringContent Json(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static async Task<(ApiFixture Fixture, HttpClient Admin)> StartAsync()
    {
        var fixture = new ApiFixture();
        var client = fixture.CreateClient();
        var response = await client.PostAsync("/api/auth/login", Json(new { email = ApiFixture.AdminEmail, password = ApiFixture.AdminPassword }));
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", document.RootElement.GetProperty("accessToken").GetString());
        return (fixture, client);
    }

    private static async Task<(Guid WorkshopId, Guid SessionId)> WorkshopAsync(HttpClient admin, int capacity = 2)
    {
        var workshop = await admin.PostAsync("/api/workshops", Json(new { code = "W-" + Guid.NewGuid().ToString("N")[..10], name = "Fixes Workshop", basePrice = 100000m, duration = 60, maxCapacity = capacity, isActive = true }));
        workshop.EnsureSuccessStatusCode();
        using var wd = JsonDocument.Parse(await workshop.Content.ReadAsStringAsync());
        var workshopId = wd.RootElement.GetProperty("id").GetGuid();
        var sessionDate = DateTime.UtcNow.AddDays(3).Date;
        var session = await admin.PostAsync($"/api/workshops/{workshopId}/sessions", Json(new { sessionDate, startTime = "10:00:00", endTime = "11:00:00", location = "Studio A", capacity }));
        session.EnsureSuccessStatusCode();
        using var sd = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
        return (workshopId, sd.RootElement.GetProperty("id").GetGuid());
    }

    [Fact]
    public void VnPayClient_IsEnabledUnderDevConfig()
    {
        // C1: booking VnPay binds the Gateway sandbox creds, so the client must be enabled in dev.
        using var fixture = new ApiFixture();
        using var scope = fixture.Services.CreateScope();
        Assert.True(scope.ServiceProvider.GetRequiredService<VnPayClient>().Enabled);
    }

    [Fact]
    public async Task AnonymousHold_IsRejectedWith401()
    {
        // M2: ticket booking requires login — no guest checkout, ever.
        var (fixture, admin) = await StartAsync();
        using (fixture) using (admin)
        {
            var (_, sessionId) = await WorkshopAsync(admin);
            using var anon = fixture.CreateClient();
            var response = await anon.PostAsync($"/api/bookings/sessions/{sessionId}", Json(new { ticketCount = 1, customerName = "Anon", customerPhone = "0901234567" }));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task ExpiredHoldReplay_CreatesFreshHoldInsteadOfReturningExpired()
    {
        // C2: replaying an expired hold must return a fresh live hold, not the dead one.
        await using var store = await TestStore.CreateAsync();
        var seed = await SeedAsync(store.Db, capacity: 2);
        var service = new WorkshopService(store.Db);
        var request = new BookingRequest { TicketCount = 1, CustomerName = "Customer", CustomerPhone = "0901234567", IdempotencyKey = "expired-replay-key" };
        var first = await service.HoldBookingAsync(seed.SessionId, request, seed.CustomerId);
        Assert.Equal("pending", first.BookingStatus);

        var held = await store.Db.WorkshopBookings.SingleAsync(x => x.Id == first.Id);
        held.HoldExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await store.Db.SaveChangesAsync();

        var replay = await service.HoldBookingAsync(seed.SessionId, request, seed.CustomerId);
        Assert.NotEqual(first.Id, replay.Id);
        Assert.Equal("pending", replay.BookingStatus);
        Assert.True(replay.HoldExpiresAt > DateTime.UtcNow);

        var old = await store.Db.WorkshopBookings.AsNoTracking().SingleAsync(x => x.Id == first.Id);
        Assert.Equal("expired", old.BookingStatus);
        Assert.Null(old.IdempotencyKey);
        Assert.Equal(1, await store.Db.WorkshopSessions.Where(x => x.Id == seed.SessionId).Select(x => x.BookedSlots).SingleAsync());
        Assert.Equal(2, await store.Db.WorkshopBookings.CountAsync(x => x.SessionId == seed.SessionId));
    }

    [Fact]
    public async Task ExpirePendingBookings_SkipsInconsistentRowAndExpiresTheRest()
    {
        // M4: one bad row (slots < tickets) must not abort the whole expiry sweep.
        await using var store = await TestStore.CreateAsync();
        var seed = await SeedAsync(store.Db, capacity: 10, bookedSlots: 1);
        var now = DateTime.UtcNow;
        var good = new WorkshopBooking { SessionId = seed.SessionId, CustomerId = seed.CustomerId, BookingCode = "WS-GOOD", CustomerName = "Good", CustomerPhone = "0901234567", TicketCount = 1, TotalAmount = 100000m, DepositAmount = 30000m, RemainingAmount = 100000m, BookingStatus = "pending", HoldExpiresAt = now.AddMinutes(-30), CreatedAt = now.AddMinutes(-40) };
        var bad = new WorkshopBooking { SessionId = seed.SessionId, CustomerId = seed.CustomerId, BookingCode = "WS-BAD", CustomerName = "Bad", CustomerPhone = "0901234567", TicketCount = 5, TotalAmount = 500000m, DepositAmount = 150000m, RemainingAmount = 500000m, BookingStatus = "pending", HoldExpiresAt = now.AddMinutes(-30), CreatedAt = now.AddMinutes(-40) };
        store.Db.WorkshopBookings.AddRange(good, bad);
        await store.Db.SaveChangesAsync();

        var service = new WorkshopService(store.Db, null, NullLogger<WorkshopService>.Instance);
        await service.ExpirePendingBookingsAsync();

        Assert.Equal("expired", await store.Db.WorkshopBookings.Where(x => x.Id == good.Id).Select(x => x.BookingStatus).SingleAsync());
        Assert.Equal("pending", await store.Db.WorkshopBookings.Where(x => x.Id == bad.Id).Select(x => x.BookingStatus).SingleAsync());
        Assert.Equal(0, await store.Db.WorkshopSessions.Where(x => x.Id == seed.SessionId).Select(x => x.BookedSlots).SingleAsync());
    }

    [Fact]
    public async Task VnPayStart_ConfirmedBookingWithRemaining_ChargesRemainder()
    {
        // M1: a confirmed booking that only paid the deposit can top up the rest by VNPay.
        await using var store = await TestStore.CreateAsync();
        var seed = await SeedAsync(store.Db, price: 100000m, capacity: 10, bookedSlots: 1);
        var now = DateTime.UtcNow;
        var booking = new WorkshopBooking { SessionId = seed.SessionId, CustomerId = seed.CustomerId, BookingCode = "WS-TOPUP", CustomerName = "Customer", CustomerPhone = "0901234567", TicketCount = 1, TotalAmount = 100000m, DepositAmount = 30000m, RemainingAmount = 70000m, BookingStatus = "confirmed", HoldExpiresAt = now.AddMinutes(10), CreatedAt = now };
        var deposit = new Payment { BookingId = booking.Id, TransactionCode = "CASH-DEPOSIT", PaymentType = "Deposit", PaymentMethod = "Cash", Amount = 30000m, PaymentStatus = "Paid", PaidAt = now, RecordedBy = seed.CustomerId };
        store.Db.AddRange(booking, deposit);
        await store.Db.SaveChangesAsync();

        var service = new VnPayBookingService(store.Db, TestVnPay(), TestConfig(), NullLogger<VnPayBookingService>.Instance);
        var checkout = await service.StartAsync(booking.Id, seed.CustomerId);
        Assert.Equal(70000, checkout.Amount);
        Assert.Contains("TESTTMN", checkout.PaymentUrl);
    }

    [Fact]
    public async Task Ipn_IgnoredOutcome_Answers99()
    {
        // M3: an IPN the service cannot act on must answer 99 (resend), not 00.
        await using var store = await TestStore.CreateAsync();
        var seed = await SeedAsync(store.Db);
        const string txnRef = "BTESTIGNORED001";
        var now = DateTime.UtcNow;
        var booking = new WorkshopBooking { SessionId = seed.SessionId, CustomerId = seed.CustomerId, BookingCode = "WS-IPN", CustomerName = "Customer", CustomerPhone = "0901234567", TicketCount = 1, TotalAmount = 100000m, DepositAmount = 30000m, RemainingAmount = 100000m, BookingStatus = "pending", HoldExpiresAt = now.AddMinutes(10), CreatedAt = now };
        var payment = new Payment { BookingId = booking.Id, TransactionCode = "VNP-" + txnRef, PaymentType = "Full", PaymentMethod = "VNPay", Amount = 100000m, PaymentStatus = "Canceled", PaymentGatewayResponse = JsonSerializer.Serialize(new { txnRef, createDate = "20261115120000", orderInfo = "Thanh toan ve", expiresAtUtc = now.AddMinutes(15), amount = 100000 }) };
        store.Db.AddRange(booking, payment);
        await store.Db.SaveChangesAsync();

        var vnPay = TestVnPay();
        var service = new VnPayBookingService(store.Db, vnPay, TestConfig(), NullLogger<VnPayBookingService>.Instance);
        var p = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["vnp_Amount"] = "10000000",
            ["vnp_BankCode"] = "NCB",
            ["vnp_OrderInfo"] = "Thanh toan ve",
            ["vnp_ResponseCode"] = "00",
            ["vnp_TmnCode"] = "TESTTMN",
            ["vnp_TransactionNo"] = "14000001",
            ["vnp_TransactionStatus"] = "00",
            ["vnp_TxnRef"] = txnRef
        };
        var data = string.Join("&", p.Select(x => $"{System.Net.WebUtility.UrlEncode(x.Key)}={System.Net.WebUtility.UrlEncode(x.Value)}"));
        var raw = data + "&vnp_SecureHash=" + vnPay.Sign(data);
        var (rsp, _) = await service.IpnAsync(raw);
        Assert.Equal("99", rsp);
    }

    [Fact]
    public async Task CheckIn_AfterSessionEnd_IsRejected_AndNoShowStillWorks()
    {
        // M6: check-in after the session end must 409; no-show remains the post-end path.
        await using var store = await TestStore.CreateAsync();
        var seed = await SeedAsync(store.Db, daysAhead: -1, bookedSlots: 1);
        var now = DateTime.UtcNow;
        var booking = new WorkshopBooking { SessionId = seed.SessionId, CustomerId = seed.CustomerId, BookingCode = "WS-END", CustomerName = "Customer", CustomerPhone = "0901234567", TicketCount = 1, TotalAmount = 100000m, DepositAmount = 30000m, RemainingAmount = 0m, BookingStatus = "confirmed", HoldExpiresAt = now, CreatedAt = now.AddDays(-1) };
        var payment = new Payment { BookingId = booking.Id, TransactionCode = "CASH-FULL", PaymentType = "Full", PaymentMethod = "Cash", Amount = 100000m, PaymentStatus = "Paid", PaidAt = now.AddDays(-1), RecordedBy = seed.CustomerId };
        store.Db.AddRange(booking, payment);
        await store.Db.SaveChangesAsync();

        var service = new WorkshopService(store.Db);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => service.CheckInAsync(booking.Id, false));
        Assert.Equal(409, ex.StatusCode);
        await service.CheckInAsync(booking.Id, true);
        Assert.Equal("no_show", await store.Db.WorkshopBookings.Where(x => x.Id == booking.Id).Select(x => x.BookingStatus).SingleAsync());
    }

    private static VnPayClient TestVnPay() => new(new HttpClient(), new VnPayOptions { TmnCode = "TESTTMN", HashSecret = "test-hash-secret-key-1234567890" });

    private static IConfiguration TestConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Site:PublicUrl"] = "https://shop.example.test" }).Build();

    private static async Task<(Guid WorkshopId, Guid SessionId, Guid CustomerId)> SeedAsync(HoaSapDbContext db, decimal price = 100000m, int capacity = 10, int daysAhead = 3, int bookedSlots = 0)
    {
        var customer = new Customer { Email = $"fix-{Guid.NewGuid():N}@example.test", FullName = "Fix Customer", PhoneNumber = "0901234567", PasswordHash = "test" };
        var workshop = new Workshop { Code = "FIX-" + Guid.NewGuid().ToString("N")[..8], Name = "Fixes Workshop", BasePrice = price, Duration = 60, MaxCapacity = capacity, IsActive = true };
        var session = new WorkshopSession { WorkshopId = workshop.Id, SessionDate = DateTime.UtcNow.AddDays(daysAhead).Date, StartTime = new TimeSpan(10, 0, 0), EndTime = new TimeSpan(11, 0, 0), Location = "Studio A", Capacity = capacity, BookedSlots = bookedSlots, SessionStatus = "Open" };
        db.AddRange(customer, workshop, session);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (workshop.Id, session.Id, customer.Id);
    }

    private sealed class TestStore : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        public HoaSapDbContext Db { get; }

        private TestStore(SqliteConnection connection, HoaSapDbContext db)
        {
            this.connection = connection;
            Db = db;
        }

        public static async Task<TestStore> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<HoaSapDbContext>().UseSqlite(connection).Options;
            var db = new HoaSapDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new TestStore(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
