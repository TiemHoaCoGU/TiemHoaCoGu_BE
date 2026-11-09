using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text.Json;
using HoaSap.BLL.Workshops;
using HoaSap.Domain;
using Microsoft.Extensions.Options;

namespace HoaSap.API.Services;

public sealed class ResendEmailTransport(IHttpClientFactory clients, IOptions<NotificationOptions> settings, WorkshopTicketCodec codec, IConfiguration configuration) : INotificationTransport
{
    private readonly NotificationOptions options = settings.Value;
    public NotificationChannel Channel => NotificationChannel.Email;
    public bool Enabled => options.EmailEnabled && !string.IsNullOrWhiteSpace(options.Resend.ApiKey) && MailAddress.TryCreate(options.Resend.From, out _);

    public async Task SendAsync(NotificationOutbox message, CancellationToken ct)
    {
        if (!Enabled) throw new InvalidOperationException("Resend email transport is disabled");
        if (string.IsNullOrWhiteSpace(message.Destination)) throw new NotificationDeliveryException("Missing email destination", false);
        var publicUrl = configuration["Site:PublicUrl"] ?? "";

        byte[]? qr = null;
        string? code = null;
        if (message.EventType == "WorkshopTicketIssued" && TryBookingId(message.PayloadJson, out var bookingId))
        {
            var token = codec.Create(bookingId);
            qr = codec.QrPng(codec.CheckInUrl(token));
            code = ReadCode(message.PayloadJson);
        }
        var rendered = EmailTemplates.Render(message.EventType, message.PayloadJson, publicUrl, qr is not null);

        var body = new Dictionary<string, object?>
        {
            ["from"] = options.Resend.From,
            ["to"] = new[] { message.Destination },
            ["subject"] = message.Subject,
            ["html"] = rendered.Html,
            ["text"] = rendered.Text
        };
        if (qr is not null)
        {
            body["attachments"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["filename"] = $"ve-{(string.IsNullOrWhiteSpace(code) ? "ticket" : code)}.png",
                    ["content"] = Convert.ToBase64String(qr),
                    ["content_type"] = "image/png",
                    ["content_id"] = "ticket-qr"
                }
            };
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.Resend.BaseUrl.TrimEnd('/')}/emails");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Resend.ApiKey);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", message.Id.ToString());
        request.Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try { response = await clients.CreateClient("notifications").SendAsync(request, ct); }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        { throw new NotificationDeliveryException("Resend request outcome unknown; retry is idempotent", false, ex); }
        using (response)
        {
            if (response.IsSuccessStatusCode) return;
            // Every failure is non-ambiguous: the Idempotency-Key makes a retry safe.
            throw new NotificationDeliveryException($"Resend rejected the message ({(int)response.StatusCode})", false);
        }
    }

    private static bool TryBookingId(string payloadJson, out Guid id)
    {
        id = Guid.Empty;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("bookingId", out var value)
                && value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out id);
        }
        catch (JsonException) { return false; }
    }

    private static string ReadCode(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        }
        catch (JsonException) { return ""; }
    }
}
