using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HoaSap.DAL;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HoaSap.Tests;

[Collection("api")]
public sealed class WorkshopRefundAuditTests : ApiTestBase
{
    private readonly ApiFixture fixture;
    public WorkshopRefundAuditTests(ApiFixture fixture) : base(fixture) => this.fixture = fixture;

    [Fact]
    public async Task WorkshopRefundRequest_LeavesApprovedByEmptyUntilAdminApproval()
    {
        using var admin = await AuthenticatedAsync(await AdminTokenAsync());
        var workshopResponse = await admin.PostAsync("/api/workshops", Json(new
        {
            code = "REFUND-" + Guid.NewGuid().ToString("N")[..10],
            name = "Refund audit workshop",
            basePrice = 100000m,
            duration = 60,
            maxCapacity = 2,
            isActive = true
        }));
        workshopResponse.EnsureSuccessStatusCode();
        using var workshopJson = JsonDocument.Parse(await workshopResponse.Content.ReadAsStringAsync());
        var workshopId = workshopJson.RootElement.GetProperty("id").GetGuid();

        var sessionResponse = await admin.PostAsync($"/api/workshops/{workshopId}/sessions", Json(new
        {
            sessionDate = DateTime.UtcNow.AddDays(3).Date,
            startTime = "10:00:00",
            endTime = "11:00:00",
            location = "Studio A",
            capacity = 2
        }));
        sessionResponse.EnsureSuccessStatusCode();
        using var sessionJson = JsonDocument.Parse(await sessionResponse.Content.ReadAsStringAsync());
        var sessionId = sessionJson.RootElement.GetProperty("id").GetGuid();

        using var customer = fixture.CreateClient();
        var email = $"refund-{Guid.NewGuid():N}@example.test";
        var registration = await customer.PostAsync("/api/auth/register", Json(new
        {
            fullName = "Refund Customer",
            phoneNumber = "09" + Random.Shared.Next(100000000, 999999999),
            email,
            password = "Password-123!"
        }));
        registration.EnsureSuccessStatusCode();
        using (var registrationJson = JsonDocument.Parse(await registration.Content.ReadAsStringAsync()))
            customer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", registrationJson.RootElement.GetProperty("accessToken").GetString());

        var bookingResponse = await customer.PostAsync($"/api/bookings/sessions/{sessionId}", Json(new
        {
            ticketCount = 1,
            customerName = "Refund Customer",
            customerPhone = "0901234567",
            idempotencyKey = "refund-audit-" + Guid.NewGuid().ToString("N")
        }));
        bookingResponse.EnsureSuccessStatusCode();
        using var bookingJson = JsonDocument.Parse(await bookingResponse.Content.ReadAsStringAsync());
        var bookingId = bookingJson.RootElement.GetProperty("id").GetGuid();

        var payment = await admin.PostAsync($"/api/payments/booking/{bookingId}", Json(new
        {
            transactionCode = "PAY-" + Guid.NewGuid().ToString("N"),
            paymentMethod = "Cash",
            amount = 100000m
        }));
        payment.EnsureSuccessStatusCode();
        var cancel = await customer.PostAsync($"/api/bookings/{bookingId}/cancel", Json("customer request"));
        cancel.EnsureSuccessStatusCode();

        // Cancellation already stages a request. Reject it, then exercise the separate
        // Staff refund endpoint that used to incorrectly record Staff as the approver.
        Guid initialRefundId;
        using (var scope = fixture.Services.CreateScope())
            initialRefundId = await scope.ServiceProvider.GetRequiredService<HoaSapDbContext>()
                .Refunds.Where(x => x.BookingId == bookingId).Select(x => x.Id).SingleAsync();
        (await admin.PostAsync($"/api/refunds/{initialRefundId}/reject", Json(new { }))).EnsureSuccessStatusCode();
        var staffEmail = $"refund-staff-{Guid.NewGuid():N}@example.test";
        await RegisterAsync(staffEmail);
        Guid staffId;
        Guid adminId;
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HoaSapDbContext>();
            var account = await db.Customers.SingleAsync(x => x.Email == staffEmail);
            account.Role = "Staff";
            staffId = account.Id;
            adminId = await db.Customers.Where(x => x.Email == ApiFixture.AdminEmail).Select(x => x.Id).SingleAsync();
            await db.SaveChangesAsync();
        }
        using var staff = fixture.CreateClient();
        var login = await staff.PostAsync("/api/auth/login", Json(new { email = staffEmail, password = "Password-123!" }));
        login.EnsureSuccessStatusCode();
        using (var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync()))
            staff.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginJson.RootElement.GetProperty("accessToken").GetString());
        (await staff.PostAsync($"/api/bookings/{bookingId}/refund", Json(new { amount = 100000m, reason = "Staff refund request" }))).EnsureSuccessStatusCode();
        Guid refundId;
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HoaSapDbContext>();
            var refund = await db.Refunds.SingleAsync(x => x.BookingId == bookingId && x.RefundStatus == "Requested");
            refundId = refund.Id;
            Assert.Equal("Requested", refund.RefundStatus);
            Assert.True(string.IsNullOrEmpty(refund.ApprovedBy));
            var audit = await db.AuditLogs.SingleAsync(x => x.EntityType == "Refund" && x.EntityId == refundId.ToString() && x.Action == "Added");
            Assert.Equal(staffId, audit.ActorId);
            Assert.Equal("Staff", audit.ActorRole);
        }

        var list = await admin.GetAsync("/api/refunds?page=1&pageSize=100");
        list.EnsureSuccessStatusCode();
        using var listJson = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var dto = listJson.RootElement.GetProperty("items").EnumerateArray()
            .Single(x => x.GetProperty("id").GetGuid() == refundId);
        Assert.Equal("Requested", dto.GetProperty("refundStatus").GetString());
        Assert.Equal(JsonValueKind.Null, dto.GetProperty("approvedBy").ValueKind);
        foreach (var action in new[] { "approve", "process", "process-gateway", "reconcile-gateway" })
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await staff.PostAsync($"/api/refunds/{refundId}/{action}", Json(new { transactionCode = "not-allowed", originalTransactionCode = "not-allowed" }))).StatusCode);
        (await admin.PostAsync($"/api/refunds/{refundId}/approve", Json(new { }))).EnsureSuccessStatusCode();
        using (var scope = fixture.Services.CreateScope())
        {
            var refund = await scope.ServiceProvider.GetRequiredService<HoaSapDbContext>().Refunds.SingleAsync(x => x.Id == refundId);
            Assert.Equal("Approved", refund.RefundStatus);
            Assert.Equal(adminId.ToString(), refund.ApprovedBy);
        }
    }

    [Fact]
    public async Task WorkshopCancellation_Within48Hours_Issues70PercentRefund()
    {
        using var admin = await AuthenticatedAsync(await AdminTokenAsync());
        var workshopResponse = await admin.PostAsync("/api/workshops", Json(new
        {
            code = "WS48H-" + Guid.NewGuid().ToString("N")[..8],
            name = "48h Policy Workshop",
            basePrice = 200000m,
            duration = 90,
            maxCapacity = 5,
            isActive = true
        }));
        workshopResponse.EnsureSuccessStatusCode();
        using var workshopJson = JsonDocument.Parse(await workshopResponse.Content.ReadAsStringAsync());
        var workshopId = workshopJson.RootElement.GetProperty("id").GetGuid();

        var sessionTime = DateTime.UtcNow.AddHours(24);
        var sessionResponse = await admin.PostAsync($"/api/workshops/{workshopId}/sessions", Json(new
        {
            sessionDate = sessionTime.Date,
            startTime = sessionTime.ToString("HH:mm:ss"),
            endTime = sessionTime.AddHours(2).ToString("HH:mm:ss"),
            location = "Studio 48h",
            capacity = 5
        }));
        sessionResponse.EnsureSuccessStatusCode();
        using var sessionJson = JsonDocument.Parse(await sessionResponse.Content.ReadAsStringAsync());
        var sessionId = sessionJson.RootElement.GetProperty("id").GetGuid();

        using var customer = fixture.CreateClient();
        var email = $"ws48h-{Guid.NewGuid():N}@example.test";
        var registration = await customer.PostAsync("/api/auth/register", Json(new
        {
            fullName = "Customer 48h",
            phoneNumber = "09" + Random.Shared.Next(100000000, 999999999),
            email,
            password = "Password-123!"
        }));
        registration.EnsureSuccessStatusCode();
        using (var registrationJson = JsonDocument.Parse(await registration.Content.ReadAsStringAsync()))
            customer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", registrationJson.RootElement.GetProperty("accessToken").GetString());

        var bookingResponse = await customer.PostAsync($"/api/bookings/sessions/{sessionId}", Json(new
        {
            ticketCount = 1,
            customerName = "Customer 48h",
            customerPhone = "0901234567",
            idempotencyKey = "ws48h-" + Guid.NewGuid().ToString("N")
        }));
        bookingResponse.EnsureSuccessStatusCode();
        using var bookingJson = JsonDocument.Parse(await bookingResponse.Content.ReadAsStringAsync());
        var bookingId = bookingJson.RootElement.GetProperty("id").GetGuid();

        var payment = await admin.PostAsync($"/api/payments/booking/{bookingId}", Json(new
        {
            transactionCode = "PAY-" + Guid.NewGuid().ToString("N"),
            paymentMethod = "Cash",
            amount = 200000m
        }));
        payment.EnsureSuccessStatusCode();

        var cancel = await customer.PostAsync($"/api/bookings/{bookingId}/cancel", Json("Bận việc"));
        cancel.EnsureSuccessStatusCode();

        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HoaSapDbContext>();
            var booking = await db.WorkshopBookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal("cancelled", booking.BookingStatus);

            var refund = await db.Refunds.SingleAsync(r => r.BookingId == bookingId);
            Assert.Equal("Requested", refund.RefundStatus);
            Assert.Equal(140000m, refund.RefundAmount);
            Assert.Contains("70%", refund.RefundReason);
        }
    }

    [Fact]
    public async Task SessionCancellation_ByAdmin_AutoCancelsAndIssues100PercentRefund()
    {
        using var admin = await AuthenticatedAsync(await AdminTokenAsync());
        var workshopResponse = await admin.PostAsync("/api/workshops", Json(new
        {
            code = "WSCANCEL-" + Guid.NewGuid().ToString("N")[..8],
            name = "Session Cancel Workshop",
            basePrice = 150000m,
            duration = 90,
            maxCapacity = 5,
            isActive = true
        }));
        workshopResponse.EnsureSuccessStatusCode();
        using var workshopJson = JsonDocument.Parse(await workshopResponse.Content.ReadAsStringAsync());
        var workshopId = workshopJson.RootElement.GetProperty("id").GetGuid();

        var sessionTime = DateTime.UtcNow.AddDays(2);
        var sessionResponse = await admin.PostAsync($"/api/workshops/{workshopId}/sessions", Json(new
        {
            sessionDate = sessionTime.Date,
            startTime = sessionTime.ToString("HH:mm:ss"),
            endTime = sessionTime.AddHours(2).ToString("HH:mm:ss"),
            location = "Studio Cancel",
            capacity = 5
        }));
        sessionResponse.EnsureSuccessStatusCode();
        using var sessionJson = JsonDocument.Parse(await sessionResponse.Content.ReadAsStringAsync());
        var sessionId = sessionJson.RootElement.GetProperty("id").GetGuid();

        using var customer = fixture.CreateClient();
        var email = $"wscancel-{Guid.NewGuid():N}@example.test";
        var registration = await customer.PostAsync("/api/auth/register", Json(new
        {
            fullName = "Customer Session Cancel",
            phoneNumber = "09" + Random.Shared.Next(100000000, 999999999),
            email,
            password = "Password-123!"
        }));
        registration.EnsureSuccessStatusCode();
        using (var registrationJson = JsonDocument.Parse(await registration.Content.ReadAsStringAsync()))
            customer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", registrationJson.RootElement.GetProperty("accessToken").GetString());

        var bookingResponse = await customer.PostAsync($"/api/bookings/sessions/{sessionId}", Json(new
        {
            ticketCount = 1,
            customerName = "Customer Session Cancel",
            customerPhone = "0901234567",
            idempotencyKey = "wscancel-" + Guid.NewGuid().ToString("N")
        }));
        bookingResponse.EnsureSuccessStatusCode();
        using var bookingJson = JsonDocument.Parse(await bookingResponse.Content.ReadAsStringAsync());
        var bookingId = bookingJson.RootElement.GetProperty("id").GetGuid();

        var payment = await admin.PostAsync($"/api/payments/booking/{bookingId}", Json(new
        {
            transactionCode = "PAY-" + Guid.NewGuid().ToString("N"),
            paymentMethod = "Cash",
            amount = 150000m
        }));
        payment.EnsureSuccessStatusCode();

        var cancelSession = await admin.DeleteAsync($"/api/workshops/sessions/{sessionId}");
        cancelSession.EnsureSuccessStatusCode();

        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HoaSapDbContext>();
            var session = await db.WorkshopSessions.SingleAsync(s => s.Id == sessionId);
            Assert.Equal("Cancelled", session.SessionStatus);

            var booking = await db.WorkshopBookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal("cancelled", booking.BookingStatus);

            var refund = await db.Refunds.SingleAsync(r => r.BookingId == bookingId);
            Assert.Equal("Requested", refund.RefundStatus);
            Assert.Equal(150000m, refund.RefundAmount);
            Assert.Contains("100%", refund.RefundReason);
        }
    }
}
