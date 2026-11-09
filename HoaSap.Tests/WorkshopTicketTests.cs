using System.Net;
using System.Text.Json;
using HoaSap.BLL.Workshops;
using HoaSap.DAL;
using HoaSap.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HoaSap.Tests;

[Collection("api")]
public sealed class WorkshopTicketTests : ApiTestBase
{
    private static readonly TimeZoneInfo VietnamTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
    private readonly ApiFixture fixture;

    public WorkshopTicketTests(ApiFixture fixture) : base(fixture) => this.fixture = fixture;

    [Fact]
    public void CodecRoundTripsAndRejectsTamperedTokens()
    {
        var codec = new WorkshopTicketCodec(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = ApiFixture.TestJwtKey,
            ["Site:PublicUrl"] = "https://shop.example.test/"
        }).Build());
        var id = Guid.NewGuid();
        var token = codec.Create(id);

        Assert.Equal(43, token.Length);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", token);
        Assert.True(codec.TryRead(token, out var read));
        Assert.Equal(id, read);
        Assert.Equal($"https://shop.example.test/nhan-vien?ve={token}", codec.CheckInUrl(token));
        Assert.NotEqual(codec.Create(Guid.NewGuid()), token);

        var tampered = (token[0] == 'A' ? 'B' : 'A') + token[1..];
        Assert.False(codec.TryRead(tampered, out _));
        Assert.False(codec.TryRead(token[..42], out _));
        Assert.False(codec.TryRead("not-a-ticket", out _));
        Assert.False(codec.TryRead(null, out _));
    }

    [Fact]
    public async Task CustomerReportStaysPendingUntilStaffConfirmsAndStagesOneTicketEmail()
    {
        using var admin = await AdminClientAsync();
        var (_, sessionId, _) = await WorkshopAsync(admin);
        using var customer = await CustomerClientAsync();
        var booking = await BookAsync(customer, sessionId);
        var reportCode = "CK-" + booking.Code;

        var report = await customer.PostAsync($"/api/payments/booking/{booking.Id}", Json(new { transactionCode = reportCode, paymentMethod = "BankTransfer", amount = booking.Total }));
        report.EnsureSuccessStatusCode();
        (await customer.PostAsync($"/api/payments/booking/{booking.Id}", Json(new { transactionCode = reportCode, paymentMethod = "BankTransfer", amount = booking.Total }))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await customer.PostAsync($"/api/payments/booking/{booking.Id}", Json(new { transactionCode = "CK-OTHER", paymentMethod = "BankTransfer", amount = booking.Total }))).StatusCode);

        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HoaSapDbContext>();
            var saved = await db.WorkshopBookings.AsNoTracking().SingleAsync(x => x.Id == booking.Id);
            Assert.Equal("pending", saved.BookingStatus);
            Assert.Equal(booking.Total, saved.RemainingAmount);
            Assert.True(saved.HoldExpiresAt > DateTime.UtcNow.AddHours(23));
            var payment = await db.Payments.AsNoTracking().SingleAsync(x => x.BookingId == booking.Id);
            Assert.Equal("PendingVerification", payment.PaymentStatus);
            Assert.Null(payment.PaidAt);
            Assert.Equal("Customer reported transfer", payment.PaymentGatewayResponse);
        }

        var pendingDto = await BookingJsonAsync(customer, booking.Id);
        Assert.Equal("pending", pendingDto.GetProperty("bookingStatus").GetString());
        Assert.Equal(booking.Total, pendingDto.GetProperty("pendingVerificationAmount").GetDecimal());
        Assert.Equal(reportCode, pendingDto.GetProperty("pendingTransactionCode").GetString());

        var mine = await customer.GetAsync("/api/bookings");
        mine.EnsureSuccessStatusCode();
        using (var mineJson = JsonDocument.Parse(await mine.Content.ReadAsStringAsync()))
        {
            var listed = mineJson.RootElement.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == booking.Id);
            Assert.Equal(booking.Total, listed.GetProperty("pendingVerificationAmount").GetDecimal());
            Assert.Equal(reportCode, listed.GetProperty("pendingTransactionCode").GetString());
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PostAsync($"/api/payments/booking/{booking.Id}/confirm", Json(new { transactionCode = reportCode }))).StatusCode);

        var confirm = await admin.PostAsync($"/api/payments/booking/{booking.Id}/confirm", Json(new { transactionCode = reportCode }));
        confirm.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/payments/booking/{booking.Id}/confirm", Json(new { transactionCode = reportCode }))).StatusCode);

        var confirmedDto = await BookingJsonAsync(customer, booking.Id);
        Assert.Equal("confirmed", confirmedDto.GetProperty("bookingStatus").GetString());
        Assert.Equal(0m, confirmedDto.GetProperty("remainingAmount").GetDecimal());
        Assert.Equal(0m, confirmedDto.GetProperty("pendingVerificationAmount").GetDecimal());
        Assert.Equal(JsonValueKind.Null, confirmedDto.GetProperty("pendingTransactionCode").ValueKind);

        using var verify = fixture.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<HoaSapDbContext>();
        var paid = await verifyDb.Payments.AsNoTracking().SingleAsync(x => x.BookingId == booking.Id);
        Assert.Equal("Paid", paid.PaymentStatus);
        Assert.NotNull(paid.PaidAt);
        Assert.NotNull(paid.RecordedBy);
        var emails = await verifyDb.Set<NotificationOutbox>().AsNoTracking().Where(x => x.EventType == "WorkshopTicketIssued" && x.PayloadJson.Contains(booking.Id.ToString())).ToListAsync();
        var email = Assert.Single(emails);
        Assert.Equal(NotificationChannel.Email, email.Channel);
        Assert.Equal("Vé của bạn: " + booking.WorkshopName, email.Subject);
        Assert.NotNull(email.Destination);
        Assert.DoesNotContain("token", email.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ve=", email.PayloadJson, StringComparison.OrdinalIgnoreCase);
        using var payload = JsonDocument.Parse(email.PayloadJson);
        Assert.Equal(booking.Id, payload.RootElement.GetProperty("bookingId").GetGuid());
        Assert.Equal(booking.Code, payload.RootElement.GetProperty("code").GetString());
        Assert.Equal(2, payload.RootElement.GetProperty("ticketCount").GetInt32());
    }

    [Fact]
    public async Task TicketEndpointGivesOwnerQrOnlyWhenIssued()
    {
        using var admin = await AdminClientAsync();
        var (_, sessionId, _) = await WorkshopAsync(admin);
        using var customer = await CustomerClientAsync();
        var booking = await BookAsync(customer, sessionId);

        var pending = await TicketJsonAsync(customer, booking.Id);
        Assert.False(pending.GetProperty("issued").GetBoolean());
        Assert.Equal(JsonValueKind.Null, pending.GetProperty("qrPngBase64").ValueKind);

        await ReportAndConfirmAsync(admin, customer, booking);

        var issued = await TicketJsonAsync(customer, booking.Id);
        Assert.True(issued.GetProperty("issued").GetBoolean());
        Assert.Equal(booking.Code, issued.GetProperty("bookingCode").GetString());
        var checkInUrl = issued.GetProperty("checkInUrl").GetString()!;
        Assert.StartsWith("http://localhost:3000/nhan-vien?ve=", checkInUrl);
        var png = Convert.FromBase64String(issued.GetProperty("qrPngBase64").GetString()!);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png.Take(4).ToArray());

        using var other = await CustomerClientAsync();
        var denied = await other.GetAsync($"/api/bookings/{booking.Id}/ticket");
        Assert.Contains(denied.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.Forbidden });
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync($"/api/bookings/{booking.Id}/ticket")).StatusCode);
    }

    [Fact]
    public async Task StaffScansAndChecksInByTokenThenDoubleCheckInConflicts()
    {
        using var admin = await AdminClientAsync();
        var (_, sessionId, _) = await WorkshopAsync(admin);
        using var customer = await CustomerClientAsync();
        var booking = await BookAsync(customer, sessionId);
        await ReportAndConfirmAsync(admin, customer, booking);
        var token = TokenFrom(await TicketJsonAsync(customer, booking.Id));
        await SetSessionAsync(sessionId, TimeSpan.FromMinutes(-30));

        var scan = await admin.GetAsync($"/api/bookings/tickets/{token}");
        scan.EnsureSuccessStatusCode();
        using (var scanJson = JsonDocument.Parse(await scan.Content.ReadAsStringAsync()))
        {
            Assert.True(scanJson.RootElement.GetProperty("fullyPaid").GetBoolean());
            Assert.True(scanJson.RootElement.GetProperty("canCheckIn").GetBoolean());
            Assert.Equal(JsonValueKind.Null, scanJson.RootElement.GetProperty("reason").ValueKind);
            Assert.Equal("confirmed", scanJson.RootElement.GetProperty("bookingStatus").GetString());
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync($"/api/bookings/tickets/{token}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PostAsync($"/api/bookings/tickets/{token}/check-in", null)).StatusCode);

        var checkIn = await admin.PostAsync($"/api/bookings/tickets/{token}/check-in", null);
        checkIn.EnsureSuccessStatusCode();
        using (var checkInJson = JsonDocument.Parse(await checkIn.Content.ReadAsStringAsync()))
        {
            Assert.Equal("attended", checkInJson.RootElement.GetProperty("bookingStatus").GetString());
            Assert.NotEqual(JsonValueKind.Null, checkInJson.RootElement.GetProperty("checkedInAt").ValueKind);
        }

        var again = await admin.PostAsync($"/api/bookings/tickets/{token}/check-in", null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("check-in", await again.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/bookings/tickets/not-a-valid-token")).StatusCode);
    }

    [Fact]
    public async Task OwnerTicketStaysIssuedAfterCheckInWithoutStagingSecondEmail()
    {
        using var admin = await AdminClientAsync();
        var (_, sessionId, _) = await WorkshopAsync(admin);
        using var customer = await CustomerClientAsync();
        var booking = await BookAsync(customer, sessionId);
        await ReportAndConfirmAsync(admin, customer, booking);
        var token = TokenFrom(await TicketJsonAsync(customer, booking.Id));
        await SetSessionAsync(sessionId, TimeSpan.FromMinutes(-30));

        var checkIn = await admin.PostAsync($"/api/bookings/tickets/{token}/check-in", null);
        checkIn.EnsureSuccessStatusCode();

        var ticket = await TicketJsonAsync(customer, booking.Id);
        Assert.Equal("attended", ticket.GetProperty("bookingStatus").GetString());
        Assert.True(ticket.GetProperty("issued").GetBoolean());
        var png = Convert.FromBase64String(ticket.GetProperty("qrPngBase64").GetString()!);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png.Take(4).ToArray());
        Assert.NotEqual(JsonValueKind.Null, ticket.GetProperty("checkedInAt").ValueKind);

        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HoaSapDbContext>();
        var emails = await db.Set<NotificationOutbox>().AsNoTracking().Where(x => x.EventType == "WorkshopTicketIssued" && x.PayloadJson.Contains(booking.Id.ToString())).ToListAsync();
        Assert.Single(emails);
    }

    [Fact]
    public async Task CheckInWindowOpensSixtyMinutesBeforeStart()
    {
        using var admin = await AdminClientAsync();
        var (_, sessionId, _) = await WorkshopAsync(admin);
        using var customer = await CustomerClientAsync();
        var booking = await BookAsync(customer, sessionId);
        await ReportAndConfirmAsync(admin, customer, booking);
        var token = TokenFrom(await TicketJsonAsync(customer, booking.Id));

        await SetSessionAsync(sessionId, TimeSpan.FromMinutes(61));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/bookings/tickets/{token}/check-in", null)).StatusCode);

        await SetSessionAsync(sessionId, TimeSpan.FromMinutes(59));
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/bookings/tickets/{token}/check-in", null)).StatusCode);
    }

    [Fact]
    public async Task BookingAndTicketTimestampsSerializeAsUtc()
    {
        using var admin = await AdminClientAsync();
        var (_, sessionId, _) = await WorkshopAsync(admin);
        using var customer = await CustomerClientAsync();
        var booking = await BookAsync(customer, sessionId);

        var bookingResponse = await customer.GetAsync($"/api/bookings/{booking.Id}");
        bookingResponse.EnsureSuccessStatusCode();
        var bookingRaw = await bookingResponse.Content.ReadAsStringAsync();
        using (var bookingJson = JsonDocument.Parse(bookingRaw))
        {
            Assert.EndsWith("Z", bookingJson.RootElement.GetProperty("holdExpiresAt").GetString()!);
            Assert.EndsWith("Z", bookingJson.RootElement.GetProperty("createdAt").GetString()!);
        }

        await ReportAndConfirmAsync(admin, customer, booking);
        var token = TokenFrom(await TicketJsonAsync(customer, booking.Id));
        await SetSessionAsync(sessionId, TimeSpan.FromMinutes(-30));

        var checkIn = await admin.PostAsync($"/api/bookings/tickets/{token}/check-in", null);
        checkIn.EnsureSuccessStatusCode();
        var checkInRaw = await checkIn.Content.ReadAsStringAsync();
        using (var checkInJson = JsonDocument.Parse(checkInRaw))
            Assert.EndsWith("Z", checkInJson.RootElement.GetProperty("checkedInAt").GetString()!);

        var ticketResponse = await customer.GetAsync($"/api/bookings/{booking.Id}/ticket");
        ticketResponse.EnsureSuccessStatusCode();
        var ticketRaw = await ticketResponse.Content.ReadAsStringAsync();
        using (var ticketJson = JsonDocument.Parse(ticketRaw))
            Assert.EndsWith("Z", ticketJson.RootElement.GetProperty("checkedInAt").GetString()!);

        var bookingAfter = await customer.GetAsync($"/api/bookings/{booking.Id}");
        bookingAfter.EnsureSuccessStatusCode();
        var bookingAfterRaw = await bookingAfter.Content.ReadAsStringAsync();
        using (var bookingAfterJson = JsonDocument.Parse(bookingAfterRaw))
            Assert.EndsWith("Z", bookingAfterJson.RootElement.GetProperty("checkedInAt").GetString()!);
    }

    private async Task<HttpClient> AdminClientAsync() => await AuthenticatedAsync(await AdminTokenAsync());

    private async Task<HttpClient> CustomerClientAsync() => await AuthenticatedAsync(await RegisterAsync($"ticket-{Guid.NewGuid():N}@example.test"));

    private static async Task<(Guid WorkshopId, Guid SessionId, decimal Price)> WorkshopAsync(HttpClient admin, decimal price = 100000m)
    {
        var workshop = await admin.PostAsync("/api/workshops", Json(new { code = "W-" + Guid.NewGuid().ToString("N")[..10], name = "Ticket Workshop", basePrice = price, duration = 60, maxCapacity = 5, isActive = true }));
        workshop.EnsureSuccessStatusCode();
        using var workshopJson = JsonDocument.Parse(await workshop.Content.ReadAsStringAsync());
        var workshopId = workshopJson.RootElement.GetProperty("id").GetGuid();
        var session = await admin.PostAsync($"/api/workshops/{workshopId}/sessions", Json(new { sessionDate = DateTime.UtcNow.AddDays(3).Date, startTime = "10:00:00", endTime = "11:00:00", location = "Studio Ticket", capacity = 5 }));
        session.EnsureSuccessStatusCode();
        using var sessionJson = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
        return (workshopId, sessionJson.RootElement.GetProperty("id").GetGuid(), price);
    }

    private sealed record Booking(Guid Id, string Code, string WorkshopName, decimal Total);

    private static async Task<Booking> BookAsync(HttpClient customer, Guid sessionId, int tickets = 2)
    {
        var response = await customer.PostAsync($"/api/bookings/sessions/{sessionId}", Json(new { ticketCount = tickets, customerName = "Ticket Guest", customerPhone = "0901234567", idempotencyKey = "ticket-" + Guid.NewGuid().ToString("N") }));
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return new Booking(json.RootElement.GetProperty("id").GetGuid(), json.RootElement.GetProperty("bookingCode").GetString()!, "Ticket Workshop", json.RootElement.GetProperty("totalAmount").GetDecimal());
    }

    private static async Task ReportAndConfirmAsync(HttpClient admin, HttpClient customer, Booking booking)
    {
        var code = "CK-" + booking.Code;
        (await customer.PostAsync($"/api/payments/booking/{booking.Id}", Json(new { transactionCode = code, paymentMethod = "BankTransfer", amount = booking.Total }))).EnsureSuccessStatusCode();
        (await admin.PostAsync($"/api/payments/booking/{booking.Id}/confirm", Json(new { transactionCode = code }))).EnsureSuccessStatusCode();
    }

    private static async Task<JsonElement> BookingJsonAsync(HttpClient client, Guid bookingId)
    {
        var response = await client.GetAsync($"/api/bookings/{bookingId}");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    private static async Task<JsonElement> TicketJsonAsync(HttpClient client, Guid bookingId)
    {
        var response = await client.GetAsync($"/api/bookings/{bookingId}/ticket");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    private static string TokenFrom(JsonElement ticket)
    {
        var url = ticket.GetProperty("checkInUrl").GetString()!;
        return url[(url.IndexOf("?ve=", StringComparison.Ordinal) + 4)..];
    }

    private async Task SetSessionAsync(Guid sessionId, TimeSpan startOffset)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HoaSapDbContext>();
        var session = await db.WorkshopSessions.SingleAsync(x => x.Id == sessionId);
        var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, VietnamTimeZone);
        var start = now.Add(startOffset);
        if (start.Date != now.Date) start = startOffset < TimeSpan.Zero ? now.Date : start;
        var end = start.TimeOfDay.Add(TimeSpan.FromHours(1));
        session.SessionDate = start.Date;
        session.StartTime = start.TimeOfDay;
        session.EndTime = end >= TimeSpan.FromDays(1) ? new TimeSpan(23, 59, 59) : end;
        await db.SaveChangesAsync();
    }
}
