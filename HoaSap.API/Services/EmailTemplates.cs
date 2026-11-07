using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace HoaSap.API.Services;

public static class EmailTemplates
{
    private const string Cobalt = "#243db5";
    private const string Citrus = "#ebf28b";
    private const string WarmWhite = "#fffdf8";
    private const string NearBlack = "#1c2326";
    private static readonly TimeZoneInfo VietnamTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");

    public sealed record Rendered(string Html, string Text);

    public static Rendered Render(string eventType, string payloadJson, string publicUrl, bool hasQr)
    {
        using var document = Parse(payloadJson);
        var payload = document.RootElement;
        return eventType switch
        {
            "WorkshopTicketIssued" => Ticket(payload, publicUrl, hasQr),
            "WorkshopBookingCancelled" => Cancelled(payload, publicUrl),
            "PasswordReset" => PasswordReset(payload),
            _ => Generic(payload)
        };
    }

    private static Rendered Ticket(JsonElement payload, string publicUrl, bool hasQr)
    {
        var name = Text(payload, "customerName");
        var workshop = Text(payload, "workshopName");
        var location = Text(payload, "location");
        var code = Text(payload, "code");
        var bookingId = Text(payload, "bookingId");
        var tickets = Number(payload, "ticketCount");
        var start = Date(payload, "sessionStartUtc");
        var end = Date(payload, "sessionEndUtc");
        var time = SessionTime(start, end);
        var ticketUrl = $"{Site(publicUrl)}/tra-cuu?ve={Uri.EscapeDataString(bookingId)}";
        var calendarUrl = CalendarUrl(workshop, location, code, ticketUrl, start, end);

        var content = new StringBuilder();
        content.Append($"<p style=\"margin:0 0 18px;font-size:15px;line-height:1.6;\">Chào {Encode(name)}, bạn đã có vé cho workshop <strong>{Encode(workshop)}</strong>.</p>");
        content.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"width:100%;border-collapse:collapse;margin:0 0 20px;\">");
        content.Append(Row("Thời gian", time));
        content.Append(Row("Địa điểm", location));
        content.Append(Row("Số vé", tickets.ToString(CultureInfo.InvariantCulture)));
        content.Append(Row("Mã đặt chỗ", code));
        content.Append("</table>");
        if (hasQr)
        {
            content.Append($"<div style=\"text-align:center;margin:0 0 8px;\"><img src=\"cid:ticket-qr\" alt=\"Mã QR vé {Encode(code)}\" width=\"220\" height=\"220\" style=\"display:block;margin:0 auto;border:8px solid {Citrus};border-radius:12px;background:#ffffff;\"></div>");
            content.Append("<p style=\"text-align:center;margin:0 0 22px;font-size:14px;color:" + NearBlack + ";\">Đưa mã này cho tiệm khi check-in</p>");
        }
        content.Append("<p style=\"text-align:center;margin:0;\">");
        content.Append($"<a href=\"{Encode(ticketUrl)}\" style=\"display:inline-block;padding:12px 22px;margin:4px;background:{Cobalt};color:{WarmWhite};text-decoration:none;border-radius:999px;font-size:14px;font-weight:bold;\">Xem vé</a>");
        if (calendarUrl.Length > 0)
            content.Append($"<a href=\"{Encode(calendarUrl)}\" style=\"display:inline-block;padding:12px 22px;margin:4px;background:{Citrus};color:{NearBlack};text-decoration:none;border-radius:999px;font-size:14px;font-weight:bold;\">Thêm vào Google Calendar</a>");
        content.Append("</p>");

        var text = new StringBuilder()
            .AppendLine("Tiệm Hoa Có Gu — Bạn đã có vé")
            .AppendLine($"Chào {name}, bạn đã có vé cho workshop {workshop}.")
            .AppendLine($"Thời gian: {time}")
            .AppendLine($"Địa điểm: {location}")
            .AppendLine($"Số vé: {tickets}")
            .AppendLine($"Mã đặt chỗ: {code}")
            .AppendLine("Đưa mã QR trong email này cho tiệm khi check-in.")
            .AppendLine($"Xem vé: {ticketUrl}")
            .Append(calendarUrl.Length > 0 ? $"Thêm vào Google Calendar: {calendarUrl}" : "")
            .ToString();
        return new Rendered(Shell("Vé workshop", "Bạn đã có vé", content.ToString()), text);
    }

    private static Rendered Cancelled(JsonElement payload, string publicUrl)
    {
        var name = Text(payload, "customerName");
        var workshop = Text(payload, "workshopName");
        var location = Text(payload, "location");
        var code = Text(payload, "code");
        var bookingId = Text(payload, "bookingId");
        var start = Date(payload, "sessionStartUtc");
        var end = Date(payload, "sessionEndUtc");
        var time = SessionTime(start, end);
        var lookupUrl = $"{Site(publicUrl)}/tra-cuu?ve={Uri.EscapeDataString(bookingId)}";

        var content = new StringBuilder();
        content.Append($"<p style=\"margin:0 0 18px;font-size:15px;line-height:1.6;\">Chào {Encode(name)}, đặt chỗ <strong>{Encode(code)}</strong> cho workshop <strong>{Encode(workshop)}</strong> đã được hủy.</p>");
        content.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"width:100%;border-collapse:collapse;margin:0 0 20px;\">");
        content.Append(Row("Thời gian", time));
        content.Append(Row("Địa điểm", location));
        content.Append(Row("Mã đặt chỗ", code));
        content.Append("</table>");
        content.Append("<p style=\"margin:0 0 20px;font-size:14px;line-height:1.6;\">Nếu bạn đã thanh toán, tiệm sẽ liên hệ để hoàn tiền theo quy định.</p>");
        content.Append($"<p style=\"margin:0;\"><a href=\"{Encode(lookupUrl)}\" style=\"display:inline-block;padding:12px 22px;background:{Cobalt};color:{WarmWhite};text-decoration:none;border-radius:999px;font-size:14px;font-weight:bold;\">Xem đặt chỗ</a></p>");

        var text = new StringBuilder()
            .AppendLine("Tiệm Hoa Có Gu — Đã hủy đặt chỗ")
            .AppendLine($"Chào {name}, đặt chỗ {code} cho workshop {workshop} đã được hủy.")
            .AppendLine($"Thời gian: {time}")
            .AppendLine($"Địa điểm: {location}")
            .AppendLine("Nếu bạn đã thanh toán, tiệm sẽ liên hệ để hoàn tiền theo quy định.")
            .Append($"Xem đặt chỗ: {lookupUrl}")
            .ToString();
        return new Rendered(Shell("Đặt chỗ workshop", "Đã hủy đặt chỗ", content.ToString()), text);
    }

    private static Rendered PasswordReset(JsonElement payload)
    {
        var url = Text(payload, "recoveryUrl");
        var content = $"""
            <p style="margin:0 0 18px;font-size:15px;line-height:1.6;">Nhấn nút bên dưới để đặt lại mật khẩu. Liên kết chỉ dùng được một lần và sẽ hết hạn sau ít phút.</p>
            <p style="margin:0 0 20px;"><a href="{Encode(url)}" style="display:inline-block;padding:12px 22px;background:{Cobalt};color:{WarmWhite};text-decoration:none;border-radius:999px;font-size:14px;font-weight:bold;">Đặt lại mật khẩu</a></p>
            <p style="margin:0;font-size:14px;line-height:1.6;">Nếu bạn không yêu cầu đặt lại mật khẩu, hãy bỏ qua email này.</p>
            """;
        var text = new StringBuilder()
            .AppendLine("Tiệm Hoa Có Gu — Đặt lại mật khẩu")
            .AppendLine("Mở liên kết sau để đặt lại mật khẩu (chỉ dùng một lần):")
            .Append(url)
            .ToString();
        return new Rendered(Shell("Tài khoản", "Đặt lại mật khẩu", content), text);
    }

    private static Rendered Generic(JsonElement payload)
    {
        var code = Text(payload, "code");
        var status = Text(payload, "status");
        var content = $"""
            <p style="margin:0 0 18px;font-size:15px;line-height:1.6;">Tiệm Hoa Có Gu vừa cập nhật đơn hàng <strong>{Encode(code)}</strong>.</p>
            <p style="margin:0;font-size:14px;line-height:1.6;">Trạng thái mới: <strong>{Encode(status)}</strong>.</p>
            """;
        var text = $"Tiệm Hoa Có Gu: {code} — trạng thái: {status}.";
        return new Rendered(Shell("Cập nhật", "Đơn hàng của bạn", content), text);
    }

    private static string Shell(string kicker, string heading, string content) => $"""
        <!doctype html>
        <html lang="vi"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>{heading}</title></head>
        <body style="margin:0;padding:0;background:{WarmWhite};color:{NearBlack};font-family:Arial,Helvetica,sans-serif;">
        <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="background:{WarmWhite};padding:24px 12px;"><tr><td align="center">
        <table role="presentation" width="560" cellpadding="0" cellspacing="0" border="0" style="width:560px;max-width:100%;background:#ffffff;border:1px solid #e8e3d5;border-radius:16px;overflow:hidden;">
        <tr><td style="background:{Cobalt};padding:20px 28px;color:{WarmWhite};font-size:20px;font-weight:bold;">Tiệm Hoa Có Gu</td></tr>
        <tr><td style="padding:28px;">
        <p style="margin:0 0 8px;font-size:12px;letter-spacing:.18em;text-transform:uppercase;color:{Cobalt};font-weight:bold;">{kicker}</p>
        <h1 style="margin:0 0 18px;font-size:26px;line-height:1.25;color:{NearBlack};">{heading}</h1>
        {content}
        </td></tr>
        <tr><td style="padding:18px 28px;background:{WarmWhite};border-top:1px solid #e8e3d5;font-size:12px;color:#5c6668;line-height:1.6;">Tiệm Hoa Có Gu · Workshop hoa và trải nghiệm sáng tạo<br>Email tự động, vui lòng không trả lời.</td></tr>
        </table></td></tr></table></body></html>
        """;

    private static string Row(string label, string value) =>
        $"<tr><td style=\"padding:10px 0;border-bottom:1px solid #efe9db;font-size:13px;color:#5c6668;width:38%;\">{Encode(label)}</td><td style=\"padding:10px 0;border-bottom:1px solid #efe9db;font-size:14px;color:{NearBlack};font-weight:bold;\">{Encode(value)}</td></tr>";

    private static string CalendarUrl(string workshop, string location, string code, string ticketUrl, DateTime? startUtc, DateTime? endUtc)
    {
        if (startUtc is null || endUtc is null) return "";
        var dates = $"{startUtc.Value:yyyyMMddTHHmmssZ}/{endUtc.Value:yyyyMMddTHHmmssZ}";
        var details = $"Mã đặt chỗ {code}. Xem vé: {ticketUrl}";
        return "https://calendar.google.com/calendar/render?action=TEMPLATE"
            + $"&text={Uri.EscapeDataString(workshop)}"
            + $"&dates={dates}"
            + $"&location={Uri.EscapeDataString(location)}"
            + $"&details={Uri.EscapeDataString(details)}";
    }

    private static string SessionTime(DateTime? startUtc, DateTime? endUtc)
    {
        if (startUtc is null) return "";
        var start = TimeZoneInfo.ConvertTimeFromUtc(startUtc.Value, VietnamTimeZone);
        var end = endUtc is null ? (DateTime?)null : TimeZoneInfo.ConvertTimeFromUtc(endUtc.Value, VietnamTimeZone);
        return end is null ? $"{start:HH:mm dd/MM/yyyy} (giờ VN)" : $"{start:HH:mm}–{end:HH:mm} {start:dd/MM/yyyy} (giờ VN)";
    }

    private static string Site(string publicUrl) => string.IsNullOrWhiteSpace(publicUrl) ? "http://localhost:3000" : publicUrl.Trim().TrimEnd('/');
    private static string Encode(string value) => WebUtility.HtmlEncode(value ?? "");

    private static string Text(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var value) ? value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
            _ => ""
        } : "";

    private static int Number(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static DateTime? Date(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.TryGetDateTime(out var date)
            ? DateTime.SpecifyKind(date, DateTimeKind.Utc) : null;

    private static JsonDocument Parse(string json)
    {
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json); }
        catch (JsonException) { return JsonDocument.Parse("{}"); }
    }
}
