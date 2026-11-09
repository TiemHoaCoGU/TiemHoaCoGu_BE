using System.Net;
using System.Text.Json;
using HoaSap.API.Services;
using HoaSap.BLL.Workshops;
using HoaSap.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace HoaSap.Tests;

public sealed class ResendEmailTransportTests
{
    private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Jwt:Key"] = ApiFixture.TestJwtKey,
        ["Site:PublicUrl"] = "https://shop.example.test/"
    }).Build();

    private static ResendEmailTransport Transport(CapturingHandler handler, string apiKey = "re_test_key") => new(
        new Factory(handler),
        Options.Create(new NotificationOptions { EmailEnabled = true, Resend = new ResendOptions { ApiKey = apiKey } }),
        new WorkshopTicketCodec(Config()),
        Config());

    private static NotificationOutbox TicketMessage(string customerName = "Ticket Guest", string location = "Studio A") => new()
    {
        EventType = "WorkshopTicketIssued",
        Destination = "guest@example.test",
        Subject = "Vé của bạn: Workshop Hoa",
        PayloadJson = JsonSerializer.Serialize(new
        {
            bookingId = Guid.NewGuid(),
            code = "WS-ABC123",
            customerName,
            ticketCount = 2,
            workshopName = "Workshop Hoa",
            sessionStartUtc = new DateTime(2026, 11, 10, 3, 0, 0, DateTimeKind.Utc),
            sessionEndUtc = new DateTime(2026, 11, 10, 5, 0, 0, DateTimeKind.Utc),
            location
        })
    };

    [Fact]
    public async Task TicketEmailUsesResendContractIdempotencyQrAttachmentAndEscapedHtml()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, "{\"id\":\"email-1\"}");
        var transport = Transport(handler);
        var message = TicketMessage("<script>alert(1)</script>", "Studio <A> & B");

        Assert.True(transport.Enabled);
        await transport.SendAsync(message, CancellationToken.None);

        Assert.Equal("https://api.resend.com/emails", handler.Url);
        Assert.Equal("Bearer", handler.Scheme);
        Assert.Equal("re_test_key", handler.Parameter);
        Assert.Equal(message.Id.ToString(), handler.IdempotencyKey);

        using var body = JsonDocument.Parse(handler.Body!);
        var root = body.RootElement;
        Assert.Equal("guest@example.test", root.GetProperty("to")[0].GetString());
        Assert.Equal("Vé của bạn: Workshop Hoa", root.GetProperty("subject").GetString());
        var html = root.GetProperty("html").GetString()!;
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("Studio &lt;A&gt; &amp; B", html);
        Assert.Contains("cid:ticket-qr", html);
        Assert.Contains("Đưa mã này cho tiệm khi check-in", html);
        Assert.Contains("https://calendar.google.com/calendar/render?action=TEMPLATE", html);
        Assert.Contains("https://shop.example.test/tra-cuu?ve=", html);
        Assert.Contains("20261110T030000Z/20261110T050000Z", html);
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("text").GetString()));

        var attachment = root.GetProperty("attachments")[0];
        Assert.Equal("ticket-qr", attachment.GetProperty("content_id").GetString());
        Assert.Equal("image/png", attachment.GetProperty("content_type").GetString());
        Assert.Equal("ve-WS-ABC123.png", attachment.GetProperty("filename").GetString());
        var png = Convert.FromBase64String(attachment.GetProperty("content").GetString()!);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png.Take(4).ToArray());
    }

    [Fact]
    public async Task NonTicketEmailsRenderSimpleHtmlWithoutAttachments()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, "{}");
        var transport = Transport(handler);
        var message = new NotificationOutbox
        {
            EventType = "PasswordReset",
            Destination = "guest@example.test",
            Subject = "Đặt lại mật khẩu",
            PayloadJson = JsonSerializer.Serialize(new { token = "protected", recoveryUrl = "https://shop.example.test/reset?token=protected" })
        };

        await transport.SendAsync(message, CancellationToken.None);

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.False(body.RootElement.TryGetProperty("attachments", out _));
        Assert.Contains("https://shop.example.test/reset?token=protected", body.RootElement.GetProperty("html").GetString());
        Assert.Contains("Đặt lại mật khẩu", body.RootElement.GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(408)]
    [InlineData(422)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task EveryFailureStatusIsDefiniteBecauseRetryIsIdempotent(int status)
    {
        var transport = Transport(new CapturingHandler((HttpStatusCode)status, "{}"));
        var error = await Assert.ThrowsAsync<NotificationDeliveryException>(() => transport.SendAsync(TicketMessage(), CancellationToken.None));
        Assert.False(error.IsAmbiguous);
    }

    [Fact]
    public async Task TimeoutIsDefiniteAndRetryable()
    {
        var transport = Transport(new CapturingHandler(HttpStatusCode.OK, "{}", _ => throw new TaskCanceledException("timeout")));
        var error = await Assert.ThrowsAsync<NotificationDeliveryException>(() => transport.SendAsync(TicketMessage(), CancellationToken.None));
        Assert.False(error.IsAmbiguous);
    }

    [Fact]
    public void TransportNeedsEnabledEmailApiKeyAndValidSender()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, "{}");
        var codec = new WorkshopTicketCodec(Config());
        Assert.False(new ResendEmailTransport(new Factory(handler), Options.Create(new NotificationOptions { EmailEnabled = false, Resend = new ResendOptions { ApiKey = "re_x" } }), codec, Config()).Enabled);
        Assert.False(new ResendEmailTransport(new Factory(handler), Options.Create(new NotificationOptions { EmailEnabled = true, Resend = new ResendOptions { ApiKey = "" } }), codec, Config()).Enabled);
        Assert.False(new ResendEmailTransport(new Factory(handler), Options.Create(new NotificationOptions { EmailEnabled = true, Resend = new ResendOptions { ApiKey = "re_x", From = "not-an-address" } }), codec, Config()).Enabled);
        Assert.True(new ResendEmailTransport(new Factory(handler), Options.Create(new NotificationOptions { EmailEnabled = true, Resend = new ResendOptions { ApiKey = "re_x" } }), codec, Config()).Enabled);
    }

    private sealed class CapturingHandler(HttpStatusCode status, string response, Func<HttpRequestMessage, Task>? beforeRespond = null) : HttpMessageHandler
    {
        public string? Url { get; private set; }
        public string? Scheme { get; private set; }
        public string? Parameter { get; private set; }
        public string? IdempotencyKey { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Url = request.RequestUri?.ToString();
            Scheme = request.Headers.Authorization?.Scheme;
            Parameter = request.Headers.Authorization?.Parameter;
            IdempotencyKey = request.Headers.TryGetValues("Idempotency-Key", out var values) ? values.Single() : null;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            if (beforeRespond is not null) await beforeRespond(request);
            return new HttpResponseMessage(status) { Content = new StringContent(response) };
        }
    }

    private sealed class Factory(CapturingHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
