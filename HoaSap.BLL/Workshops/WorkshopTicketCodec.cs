using System.Security.Cryptography;
using System.Text;
using HoaSap.Domain.Entities;
using Microsoft.Extensions.Configuration;
using QRCoder;

namespace HoaSap.BLL.Workshops;

public sealed class WorkshopTicketCodec
{
    private const int IdBytes = 16;
    private const int MacBytes = 16;
    private const int TokenBytes = IdBytes + MacBytes;
    private const int TokenLength = 43;
    private static readonly byte[] HkdfInfo = Encoding.UTF8.GetBytes("hoasap-workshop-ticket-v1");
    private readonly byte[] key;
    private readonly string publicUrl;

    public WorkshopTicketCodec(IConfiguration configuration)
    {
        var configured = configuration["Tickets:SigningKey"];
        if (!string.IsNullOrWhiteSpace(configured) && Encoding.UTF8.GetByteCount(configured) >= 32) key = Encoding.UTF8.GetBytes(configured);
        else
        {
            var jwt = configuration["Jwt:Key"];
            if (string.IsNullOrWhiteSpace(jwt)) throw new InvalidOperationException("Set Tickets:SigningKey (at least 32 bytes) or Jwt:Key to sign workshop tickets.");
            key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(jwt), 32, info: HkdfInfo);
        }
        var site = configuration["Site:PublicUrl"];
        publicUrl = string.IsNullOrWhiteSpace(site) ? "http://localhost:3000" : site.Trim().TrimEnd('/');
    }

    public static bool Issued(WorkshopBooking booking) => (booking.BookingStatus == "confirmed" || booking.BookingStatus == "attended") && booking.RemainingAmount == 0;

    public string Create(Guid id)
    {
        Span<byte> bytes = stackalloc byte[TokenBytes];
        Span<byte> hash = stackalloc byte[32];
        id.TryWriteBytes(bytes[..IdBytes]);
        HMACSHA256.HashData(key, bytes[..IdBytes], hash);
        hash[..MacBytes].CopyTo(bytes[IdBytes..]);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public bool TryRead(string? token, out Guid id)
    {
        id = Guid.Empty;
        if (token is null || token.Length != TokenLength || !TryFromBase64Url(token, out var bytes) || bytes.Length != TokenBytes) return false;
        Span<byte> hash = stackalloc byte[32];
        HMACSHA256.HashData(key, bytes.AsSpan(0, IdBytes), hash);
        if (!CryptographicOperations.FixedTimeEquals(hash[..MacBytes], bytes.AsSpan(IdBytes, MacBytes))) return false;
        id = new Guid(bytes.AsSpan(0, IdBytes));
        return true;
    }

    public string CheckInUrl(string token) => $"{publicUrl}/nhan-vien?ve={Uri.EscapeDataString(token)}";

    public byte[] QrPng(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(8);
    }

    private static bool TryFromBase64Url(string value, out byte[] bytes)
    {
        bytes = [];
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        try { bytes = Convert.FromBase64String(padded); return true; }
        catch (FormatException) { return false; }
    }
}
