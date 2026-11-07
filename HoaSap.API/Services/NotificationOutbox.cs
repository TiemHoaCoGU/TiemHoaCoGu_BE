using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using HoaSap.DAL;
using HoaSap.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HoaSap.API.Services;

public sealed class NotificationOptions
{
    public bool EmailEnabled { get; set; }
    public bool SmsEnabled { get; set; }
    public int MaxAttempts { get; set; } = 8;
    public int LeaseSeconds { get; set; } = 120;
    public SmtpOptions Smtp { get; set; } = new();
    public SmsOptions Sms { get; set; } = new();
    public ResendOptions Resend { get; set; } = new();
}
public sealed class ResendOptions
{
    public string ApiKey { get; set; } = "";
    public string From { get; set; } = "Tiệm Hoa Có Gu <onboarding@resend.dev>";
    public string BaseUrl { get; set; } = "https://api.resend.com";
}
public sealed class SmtpOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string From { get; set; } = "";
}
public sealed class SmsOptions
{
    public string Mode { get; set; } = "Disabled";
    public string TestAccountSid { get; set; } = "";
    public string TestAuthToken { get; set; } = "";
    public const string TestFrom = "+15005550006";
    public const string TestTo = "+12025550123";
}

public interface INotificationTransport
{
    NotificationChannel Channel { get; }
    bool Enabled { get; }
    Task SendAsync(NotificationOutbox message, CancellationToken ct);
}

/// <summary>Provider may have accepted a message although its response was lost.</summary>
public sealed class NotificationDeliveryException(string message, bool isAmbiguous, Exception? inner = null) : Exception(message, inner)
{
    public bool IsAmbiguous { get; } = isAmbiguous;
}

public static class NotificationDeliveryClassifier
{
    public static bool IsAmbiguous(Exception exception) => exception switch
    {
        NotificationDeliveryException delivery => delivery.IsAmbiguous,
        OperationCanceledException => true, // delivery timeout: provider may have accepted it
        HttpRequestException => true, // response/connection can be lost after provider acceptance
        SmtpFailedRecipientException => false, // SMTP explicitly rejected the recipient
        SmtpException => true, // SMTP connection/response may have been lost after DATA
        IOException => true,
        _ => false
    };
}
public sealed class SmtpNotificationTransport(IOptions<NotificationOptions> settings) : INotificationTransport
{
    private readonly NotificationOptions options = settings.Value;
    public NotificationChannel Channel => NotificationChannel.Email;
    public bool Enabled => options.EmailEnabled && string.Equals(options.Smtp.Host, "sandbox.smtp.mailtrap.io", StringComparison.OrdinalIgnoreCase)
        && options.Smtp.Port is > 0 and <= 65535 && options.Smtp.EnableSsl
        && !string.IsNullOrWhiteSpace(options.Smtp.Username) && !string.IsNullOrWhiteSpace(options.Smtp.Password)
        && MailAddress.TryCreate(options.Smtp.From, out var from) && from.Host.EndsWith(".test", StringComparison.OrdinalIgnoreCase);
    public async Task SendAsync(NotificationOutbox message, CancellationToken ct)
    {
        if (!Enabled) throw new InvalidOperationException("Email transport is disabled");
        using var client = new SmtpClient(options.Smtp.Host, options.Smtp.Port) { EnableSsl = options.Smtp.EnableSsl, DeliveryMethod = SmtpDeliveryMethod.Network };
        if (!string.IsNullOrWhiteSpace(options.Smtp.Username)) client.Credentials = new NetworkCredential(options.Smtp.Username, options.Smtp.Password);
        using var mail = new MailMessage(options.Smtp.From, message.Destination!, message.Subject, RenderBody(message.PayloadJson));
        mail.Headers.Add("Message-Id", $"<{message.Id:N}@hoasap.local>");
        try { await client.SendMailAsync(mail, ct); }
        catch (SmtpFailedRecipientException ex) { throw new NotificationDeliveryException("SMTP rejected recipient", false, ex); }
        catch (SmtpException ex) { throw new NotificationDeliveryException("SMTP delivery outcome unknown", true, ex); }
    }
    internal static string RenderBody(string json)
    {
        try { using var doc = JsonDocument.Parse(json); return doc.RootElement.TryGetProperty("recoveryUrl", out var url) ? $"Vui lòng mở liên kết khôi phục: {url.GetString()}" : $"Tiệm Hoa Có Gu: {doc.RootElement.GetProperty("code").GetString()} — trạng thái: {doc.RootElement.GetProperty("status").GetString()}."; }
        catch (JsonException) { return "Có cập nhật mới từ Tiệm Hoa Có Gu."; }
    }

    internal static string RenderSmsBody(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("recoveryUrl", out _)) return "Tiệm Hoa Có Gu: bản thử nghiệm yêu cầu khôi phục tài khoản; liên kết được lược bỏ.";
            return $"Tiệm Hoa Có Gu: {doc.RootElement.GetProperty("code").GetString()} — trạng thái: {doc.RootElement.GetProperty("status").GetString()}.";
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return "Tiệm Hoa Có Gu: có cập nhật thử nghiệm; không kèm thông tin khôi phục.";
        }
    }
}
/// <summary>Twilio's Test Credentials transport. It can simulate API acceptance only; no real SMS is sent.</summary>
public sealed class TwilioSmsNotificationTransport(IHttpClientFactory clients, IOptions<NotificationOptions> settings) : INotificationTransport
{
    private readonly NotificationOptions options = settings.Value;
    public NotificationChannel Channel => NotificationChannel.Sms;
    public bool Enabled => options.SmsEnabled && string.Equals(options.Sms.Mode, "Test", StringComparison.Ordinal)
        && options.Sms.TestAccountSid.Length == 34 && options.Sms.TestAccountSid.StartsWith("AC", StringComparison.Ordinal)
        && options.Sms.TestAccountSid.AsSpan(2).ToString().All(Uri.IsHexDigit)
        && !string.IsNullOrWhiteSpace(options.Sms.TestAuthToken);
    public async Task SendAsync(NotificationOutbox message, CancellationToken ct)
    {
        if (!Enabled) throw new InvalidOperationException("Twilio Test Credentials transport is disabled or incompletely configured");
        var path = $"https://api.twilio.com/2010-04-01/Accounts/{options.Sms.TestAccountSid}/Messages.json";
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{options.Sms.TestAccountSid}:{options.Sms.TestAuthToken}"));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["To"] = SmsOptions.TestTo,
            ["From"] = SmsOptions.TestFrom,
            ["Body"] = SmtpNotificationTransport.RenderSmsBody(message.PayloadJson)
        });
        HttpResponseMessage response;
        try { response = await clients.CreateClient("notifications").SendAsync(request, ct); }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        { throw new NotificationDeliveryException("Twilio test request outcome unknown", true, ex); }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.RequestTimeout || (int)response.StatusCode == 429 || (int)response.StatusCode >= 500)
                throw new NotificationDeliveryException($"Twilio test request outcome unknown ({(int)response.StatusCode})", true);
            if (!response.IsSuccessStatusCode)
                throw new NotificationDeliveryException($"Twilio test request rejected ({(int)response.StatusCode})", false);
            try
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (!document.RootElement.TryGetProperty("sid", out var sid) || string.IsNullOrWhiteSpace(sid.GetString()))
                    throw new NotificationDeliveryException("Twilio test response did not confirm API acceptance", true);
            }
            catch (JsonException ex) { throw new NotificationDeliveryException("Twilio test response was invalid; outcome unknown", true, ex); }
        }
    }
}

public sealed class NotificationWorker(IServiceScopeFactory scopes, IEnumerable<INotificationTransport> transports, IOptions<NotificationOptions> options, ILogger<NotificationWorker> log) : BackgroundService
{
    private readonly string owner = $"{Environment.MachineName}:{Guid.NewGuid():N}";
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProcessBatch(stoppingToken); } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; } catch (Exception ex) { log.LogError(ex, "Notification worker cycle failed"); }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
    private async Task ProcessBatch(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HoaSapDbContext>();
        var now = DateTime.UtcNow; var lease = now.AddSeconds(Math.Clamp(options.Value.LeaseSeconds, 15, 3600));
        var enabledChannels = transports.Where(x => x.Enabled).Select(x => x.Channel).ToArray();
        if (enabledChannels.Length == 0) return;
        // An expired processing lease may mean the process crashed after the provider accepted
        // the request but before Sent was persisted. Never reclaim it for another send.
        await db.Set<NotificationOutbox>()
            .Where(x => enabledChannels.Contains(x.Channel) && x.Status == NotificationStatus.Processing && x.LeasedUntilUtc < now)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, NotificationStatus.Unknown)
                .SetProperty(x => x.LastError, "processing lease expired; delivery outcome unknown; automatic retry suppressed")
                .SetProperty(x => x.LeasedUntilUtc, (DateTime?)null)
                .SetProperty(x => x.LeaseOwner, (string?)null), ct);
        var candidates = await db.Set<NotificationOutbox>().AsNoTracking().Where(x => enabledChannels.Contains(x.Channel) && x.Status == NotificationStatus.Pending && x.NextAttemptAtUtc <= now).OrderBy(x => x.CreatedAtUtc).Take(50).Select(x => x.Id).ToListAsync(ct);
        foreach (var id in candidates)
        {
            var peek = await db.Set<NotificationOutbox>().AsNoTracking().SingleAsync(x => x.Id == id, ct);
            var transport = transports.FirstOrDefault(x => x.Channel == peek.Channel && x.Enabled);
            if (transport is null) continue; // disabled remains Pending and does not consume attempts
            now = DateTime.UtcNow; lease = now.AddSeconds(Math.Clamp(options.Value.LeaseSeconds, 60, 3600));
            var claimed = await db.Set<NotificationOutbox>().Where(x => x.Id == id && x.Status == NotificationStatus.Pending && x.NextAttemptAtUtc <= now).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, NotificationStatus.Processing).SetProperty(x => x.LeaseOwner, owner).SetProperty(x => x.LeasedUntilUtc, lease), ct);
            if (claimed != 1) continue;
            var row = await db.Set<NotificationOutbox>().AsNoTracking().SingleAsync(x => x.Id == id, ct);
            using var deliveryTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deliveryTimeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(45, Math.Clamp(options.Value.LeaseSeconds, 60, 3600) - 10)));
            try { await transport.SendAsync(row, deliveryTimeout.Token); row.Status = NotificationStatus.Sent; row.SentAtUtc = DateTime.UtcNow; row.LeasedUntilUtc = null; row.LeaseOwner = null; }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                row.Attempts++;
                var ambiguous = NotificationDeliveryClassifier.IsAmbiguous(ex);
                row.LastError = ambiguous ? "delivery outcome unknown; automatic retry suppressed" : "delivery failed";
                row.Status = ambiguous ? NotificationStatus.Unknown : row.Attempts >= Math.Max(1, options.Value.MaxAttempts) ? NotificationStatus.Failed : NotificationStatus.Pending;
                row.NextAttemptAtUtc = DateTime.UtcNow.AddMinutes(Math.Min(60, Math.Pow(2, Math.Min(6, row.Attempts))));
                row.LeasedUntilUtc = null; row.LeaseOwner = null;
            }
            await db.Set<NotificationOutbox>().Where(x => x.Id == id && x.LeaseOwner == owner && x.Status == NotificationStatus.Processing).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, row.Status).SetProperty(x => x.SentAtUtc, row.SentAtUtc).SetProperty(x => x.Attempts, row.Attempts).SetProperty(x => x.LastError, row.LastError).SetProperty(x => x.NextAttemptAtUtc, row.NextAttemptAtUtc).SetProperty(x => x.LeasedUntilUtc, (DateTime?)null).SetProperty(x => x.LeaseOwner, (string?)null), ct);
        }
    }
}
