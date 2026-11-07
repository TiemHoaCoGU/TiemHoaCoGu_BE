using System.Text.Json;
using HoaSap.Domain;
using HoaSap.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace HoaSap.DAL.Notifications;

public static class NotificationOutboxStaging
{
    private static readonly TimeZoneInfo VietnamTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");

    public static void Stage(ChangeTracker tracker)
    {
        var existing = tracker.Entries<NotificationOutbox>().Select(x => x.Entity)
            .Select(x => (x.EventType, x.CustomerId, x.Channel, x.Destination, x.PayloadJson)).ToHashSet();
        foreach (var entry in tracker.Entries().Where(x => x.Entity is Order or WorkshopBooking && IsStateChange(x)).ToList())
        {
            if (entry.Entity is Order order)
            {
                const string type = "OrderChanged";
                var email = FindCustomerEmail(tracker, order.CustomerId) ?? OrderContact(order.ShippingAddress, "ReceiverEmail");
                var phone = FindCustomerPhone(tracker, order.CustomerId) ?? OrderContact(order.ShippingAddress, "ReceiverPhone");
                var orderPayload = JsonSerializer.Serialize(new { entity = type, code = order.OrderCode, status = order.OrderStatus, paymentStatus = order.PaymentStatus });
                Add(tracker, type, order.CustomerId, NotificationChannel.Email, email, orderPayload, existing);
                Add(tracker, type, order.CustomerId, NotificationChannel.Sms, phone, orderPayload, existing);
                continue;
            }

            var booking = (WorkshopBooking)entry.Entity;
            var bookingEmail = string.IsNullOrWhiteSpace(booking.CustomerEmail) ? FindCustomerEmail(tracker, booking.CustomerId) : booking.CustomerEmail;
            var bookingPhone = string.IsNullOrWhiteSpace(booking.CustomerPhone) ? FindCustomerPhone(tracker, booking.CustomerId) : booking.CustomerPhone;
            var statusChanged = entry.State == EntityState.Added || Changed(entry, nameof(WorkshopBooking.BookingStatus));
            if (statusChanged)
            {
                var smsPayload = JsonSerializer.Serialize(new { entity = "WorkshopBookingChanged", code = booking.BookingCode, status = booking.BookingStatus, paymentStatus = (string?)null });
                Add(tracker, "WorkshopBookingChanged", booking.CustomerId, NotificationChannel.Sms, bookingPhone, smsPayload, existing);
            }

            var issued = IsIssued(booking);
            var wasIssued = entry.State == EntityState.Modified && WasIssued(entry);
            var cancelled = booking.BookingStatus == "cancelled" && statusChanged;
            if (!(issued && !wasIssued) && !cancelled) continue;

            var session = tracker.Context.Set<WorkshopSession>().AsNoTracking().FirstOrDefault(x => x.Id == booking.SessionId);
            var workshop = session is null ? null : tracker.Context.Set<Workshop>().AsNoTracking().FirstOrDefault(x => x.Id == session.WorkshopId);
            var payload = JsonSerializer.Serialize(new
            {
                bookingId = booking.Id,
                code = booking.BookingCode,
                customerName = booking.CustomerName,
                ticketCount = booking.TicketCount,
                workshopName = workshop?.Name ?? "",
                sessionStartUtc = session is null ? (DateTime?)null : SessionStartUtc(session),
                sessionEndUtc = session is null ? (DateTime?)null : SessionEndUtc(session),
                location = session?.Location ?? ""
            });
            if (issued && !wasIssued)
                Add(tracker, "WorkshopTicketIssued", booking.CustomerId, NotificationChannel.Email, bookingEmail, payload, existing, $"Vé của bạn: {workshop?.Name}");
            if (cancelled)
                Add(tracker, "WorkshopBookingCancelled", booking.CustomerId, NotificationChannel.Email, bookingEmail, payload, existing, $"Đã hủy đặt chỗ: {workshop?.Name}");
        }
    }

    public static void StagePasswordReset(HoaSapDbContext db, Customer customer, string protectedToken, string recoveryUrl)
    {
        var payload = JsonSerializer.Serialize(new { token = protectedToken, recoveryUrl });
        db.Set<NotificationOutbox>().Add(new NotificationOutbox { EventType = "PasswordReset", CustomerId = customer.Id, Channel = NotificationChannel.Email, Destination = customer.Email, Subject = "Đặt lại mật khẩu", PayloadJson = payload });
    }

    private static bool IsStateChange(EntityEntry entry)
    {
        if (entry.State == EntityState.Added) return true;
        if (entry.State != EntityState.Modified) return false;
        if (entry.Entity is Order) return Changed(entry, nameof(Order.PaymentStatus)) || Changed(entry, nameof(Order.OrderStatus));
        return Changed(entry, nameof(WorkshopBooking.BookingStatus)) || Changed(entry, nameof(WorkshopBooking.RemainingAmount));
    }

    private static bool Changed(EntityEntry entry, string property)
    {
        var value = entry.Property(property);
        return value.IsModified && !Equals(value.OriginalValue, value.CurrentValue);
    }

    private static bool IsIssued(WorkshopBooking booking) => booking.BookingStatus == "confirmed" && booking.RemainingAmount == 0;

    private static bool WasIssued(EntityEntry entry) =>
        entry.Property(nameof(WorkshopBooking.BookingStatus)).OriginalValue as string == "confirmed"
        && entry.Property(nameof(WorkshopBooking.RemainingAmount)).OriginalValue is decimal remaining && remaining == 0;

    private static DateTime SessionStartUtc(WorkshopSession s) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(s.SessionDate.Date.Add(s.StartTime), DateTimeKind.Unspecified), VietnamTimeZone);
    private static DateTime SessionEndUtc(WorkshopSession s) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(s.SessionDate.Date.Add(s.EndTime), DateTimeKind.Unspecified), VietnamTimeZone);

    private static void Add(ChangeTracker tracker, string type, Guid? customerId, NotificationChannel channel, string? destination, string payload, HashSet<(string, Guid?, NotificationChannel, string?, string)> existing, string? subject = null)
    {
        if (string.IsNullOrWhiteSpace(destination) || existing.Contains((type, customerId, channel, destination, payload))) return;
        tracker.Context.Set<NotificationOutbox>().Add(new NotificationOutbox { EventType = type, CustomerId = customerId, Channel = channel, Destination = destination.Trim(), Subject = subject ?? type, PayloadJson = payload });
    }

    private static string? FindCustomerEmail(ChangeTracker tracker, Guid? id) => id is null ? null : tracker.Context.Set<Customer>().Local.FirstOrDefault(x => x.Id == id)?.Email ?? (id is null ? null : tracker.Context.Set<Customer>().AsNoTracking().Where(x => x.Id == id).Select(x => x.Email).FirstOrDefault());
    private static string? FindCustomerPhone(ChangeTracker tracker, Guid? id) => id is null ? null : tracker.Context.Set<Customer>().Local.FirstOrDefault(x => x.Id == id)?.PhoneNumber ?? (id is null ? null : tracker.Context.Set<Customer>().AsNoTracking().Where(x => x.Id == id).Select(x => x.PhoneNumber).FirstOrDefault());
    private static string? OrderContact(string address, string field)
    {
        var prefix = field + ": ";
        var segment = address.Split(';').Select(x => x.Trim()).FirstOrDefault(x => x.StartsWith(prefix, StringComparison.Ordinal));
        var value = segment?[prefix.Length..].Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
