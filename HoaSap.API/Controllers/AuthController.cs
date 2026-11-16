using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using HoaSap.API.Security;
using HoaSap.API.Services;
using HoaSap.BLL;
using HoaSap.DAL;
using HoaSap.DAL.Notifications;
using HoaSap.Domain;
using HoaSap.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;

namespace HoaSap.API.Controllers;

[ApiController, Route("api/auth"), EnableRateLimiting("auth")]
public sealed class AuthController(HoaSapDbContext db, IConfiguration config, IDataProtectionProvider protectionProvider) : ControllerBase
{
    private IDataProtector ResetProtector => protectionProvider.CreateProtector("HoaSap.Auth.PasswordReset.v1");
    [HttpPost("register"), AllowAnonymous]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        if (await db.Customers.AnyAsync(x => x.Email == email || x.PhoneNumber == request.PhoneNumber.Trim(), ct)) throw new BusinessException(409, "Email hoặc số điện thoại đã được sử dụng.");
        if (!PasswordService.IsValid(request.Password)) throw new BusinessException(400, PasswordService.PolicyMessage);
        var customer = new Customer { FullName = request.FullName.Trim(), Email = email, PhoneNumber = request.PhoneNumber.Trim(), PasswordHash = PasswordService.Hash(request.Password), Role = "Customer", CreatedAt = DateTime.UtcNow };
        db.Customers.Add(customer); await db.SaveChangesAsync(ct); return StatusCode(201, await IssueAsync(customer, ct));
    }

    [HttpPost("login"), AllowAnonymous]
    // Đọc Customers theo email, kiểm mật khẩu với bản băm rồi mới cấp phiên.
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant(); var user = await db.Customers.SingleOrDefaultAsync(x => x.Email == email && !x.IsGuest && x.IsActive, ct);
        var valid = user is not null ? PasswordService.Verify(request.Password, user.PasswordHash) : PasswordService.Verify(request.Password, PasswordService.Hash("dummy-unusable-password"));
        if (!valid || user is null) throw new BusinessException(401, "Email hoặc mật khẩu không đúng.");
        return Ok(await IssueAsync(user, ct));
    }

    [HttpPost("refresh"), AllowAnonymous]
    public async Task<IActionResult> Refresh(RefreshRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken)) throw new BusinessException(401, "Refresh token không hợp lệ.");
        await using var tx = await db.Database.BeginTransactionAsync(ct); var hash = TokenService.Hash(request.RefreshToken); var now = DateTime.UtcNow;
        var stored = await db.Set<RefreshToken>().SingleOrDefaultAsync(x => x.TokenHash == hash, ct);
        if (stored is null) throw new BusinessException(401, "Refresh token không hợp lệ.");
        if (stored.RevokedAtUtc is not null) { await db.Set<RefreshToken>().Where(x => x.FamilyId == stored.FamilyId && x.RevokedAtUtc == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAtUtc, now).SetProperty(x => x.RevocationReason, "reuse-detected"), ct); await tx.CommitAsync(ct); throw new BusinessException(401, "Refresh token không hợp lệ."); }
        if (stored.ExpiresAtUtc <= now) throw new BusinessException(401, "Refresh token đã hết hạn.");
        var user = await db.Customers.SingleOrDefaultAsync(x => x.Id == stored.CustomerId && x.IsActive && !x.IsGuest, ct); if (user is null) throw new BusinessException(401, "Tài khoản không hợp lệ.");
        var plain = TokenService.NewOpaqueToken(); var replacement = new RefreshToken { CustomerId = user.Id, FamilyId = stored.FamilyId, TokenHash = TokenService.Hash(plain), ExpiresAtUtc = now.AddDays(config.GetValue("Jwt:RefreshExpirationDays", 30)) };
        var claimed = await db.Set<RefreshToken>().Where(x => x.Id == stored.Id && x.RevokedAtUtc == null && x.ExpiresAtUtc > now).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAtUtc, now).SetProperty(x => x.ReplacedByHash, replacement.TokenHash).SetProperty(x => x.RevocationReason, "rotated"), ct);
        if (claimed != 1) { await db.Set<RefreshToken>().Where(x => x.FamilyId == stored.FamilyId && x.RevokedAtUtc == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAtUtc, now).SetProperty(x => x.RevocationReason, "reuse-detected"), ct); await tx.CommitAsync(ct); throw new BusinessException(401, "Refresh token không hợp lệ."); }
        db.Set<RefreshToken>().Add(replacement); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        var access = TokenService.AccessToken(user, config, out var expires); return Ok(new { accessToken = access, refreshToken = plain, expiresAt = expires, user = UserDto(user) });
    }

    [HttpPost("logout"), AllowAnonymous]
    public async Task<IActionResult> Logout(RefreshRequest request, CancellationToken ct)
    { if (!string.IsNullOrWhiteSpace(request.RefreshToken)) await db.Set<RefreshToken>().Where(x => x.TokenHash == TokenService.Hash(request.RefreshToken) && x.RevokedAtUtc == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAtUtc, DateTime.UtcNow).SetProperty(x => x.RevocationReason, "logout"), ct); return NoContent(); }

    [HttpPost("forgot-password"), AllowAnonymous]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest request, CancellationToken ct)
    {
        var generic = new { message = "Nếu tài khoản tồn tại, hướng dẫn khôi phục đã được gửi." }; var email = request.Email.Trim().ToLowerInvariant();
        var user = await db.Customers.SingleOrDefaultAsync(x => x.Email == email && !x.IsGuest && x.IsActive, ct);
        if (user is not null) { var token = TokenService.NewOpaqueToken(); db.Set<PasswordResetToken>().Add(new PasswordResetToken { CustomerId = user.Id, TokenHash = TokenService.Hash(token), ExpiresAtUtc = DateTime.UtcNow.AddMinutes(config.GetValue("Auth:PasswordResetMinutes", 30)) }); var protectedToken = ResetProtector.Protect(token); var baseUrl = config["Auth:PasswordResetUrlBase"]; if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var recoveryBase) || recoveryBase.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("Auth:PasswordResetUrlBase must be an HTTPS URL."); var recoveryUrl = $"{baseUrl.TrimEnd('/') }?token={Uri.EscapeDataString(protectedToken)}"; NotificationOutboxStaging.StagePasswordReset(db, user, protectedToken, recoveryUrl); await db.SaveChangesAsync(ct); }
        return Accepted(generic);
    }

    [HttpPost("reset-password"), AllowAnonymous]
    public async Task<IActionResult> ResetPassword(ForgotResetPasswordRequest request, CancellationToken ct)
    {
        if (!PasswordService.IsValid(request.NewPassword)) throw new BusinessException(400, PasswordService.PolicyMessage);
        await using var tx = await db.Database.BeginTransactionAsync(ct); string tokenHash; try { tokenHash = TokenService.Hash(ResetProtector.Unprotect(request.Token)); } catch (Exception) when (request.Token.Length > 0) { throw new BusinessException(400, "Mã khôi phục không hợp lệ hoặc đã hết hạn."); } var row = await db.Set<PasswordResetToken>().SingleOrDefaultAsync(x => x.TokenHash == tokenHash, ct);
        if (row is null || row.UsedAtUtc is not null || row.ExpiresAtUtc <= DateTime.UtcNow) throw new BusinessException(400, "Mã khôi phục không hợp lệ hoặc đã hết hạn.");
        var claimed = await db.Set<PasswordResetToken>().Where(x => x.Id == row.Id && x.UsedAtUtc == null && x.ExpiresAtUtc > DateTime.UtcNow).ExecuteUpdateAsync(s => s.SetProperty(x => x.UsedAtUtc, DateTime.UtcNow), ct); if (claimed != 1) throw new BusinessException(400, "Mã khôi phục không hợp lệ hoặc đã hết hạn.");
        var user = await db.Customers.SingleOrDefaultAsync(x => x.Id == row.CustomerId && !x.IsGuest && x.IsActive, ct); if (user is null) throw new BusinessException(400, "Mã khôi phục không hợp lệ hoặc đã hết hạn.");
        user.PasswordHash = PasswordService.Hash(request.NewPassword);
        await db.Set<RefreshToken>().Where(x => x.CustomerId == user.Id && x.RevokedAtUtc == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAtUtc, DateTime.UtcNow).SetProperty(x => x.RevocationReason, "password-reset"), ct); await db.Set<PasswordResetToken>().Where(x => x.CustomerId == user.Id && x.UsedAtUtc == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.UsedAtUtc, DateTime.UtcNow), ct); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return NoContent();
    }

    [HttpPost("change-password"), Authorize]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken ct)
    {
        if (!PasswordService.IsValid(request.NewPassword)) throw new BusinessException(400, PasswordService.PolicyMessage); if (request.NewPassword != request.ConfirmNewPassword) throw new BusinessException(400, "Mật khẩu mới và xác nhận mật khẩu không khớp.");
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)) throw new BusinessException(401, "Phiên đăng nhập không hợp lệ."); var user = await db.Customers.SingleOrDefaultAsync(x => x.Id == id && !x.IsGuest && x.IsActive, ct);
        if (user is null || !PasswordService.Verify(request.CurrentPassword, user.PasswordHash)) throw new BusinessException(401, "Mật khẩu hiện tại không đúng."); if (PasswordService.Verify(request.NewPassword, user.PasswordHash)) throw new BusinessException(400, "Mật khẩu mới phải khác mật khẩu hiện tại.");
        user.PasswordHash = PasswordService.Hash(request.NewPassword); await db.Set<PasswordResetToken>().Where(x => x.CustomerId == id && x.UsedAtUtc == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.UsedAtUtc, DateTime.UtcNow), ct); await db.Set<RefreshToken>().Where(x => x.CustomerId == id && x.RevokedAtUtc == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAtUtc, DateTime.UtcNow).SetProperty(x => x.RevocationReason, "password-change"), ct); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return NoContent();
    }

    [HttpGet("me"), Authorize]
    public async Task<IActionResult> Me(CancellationToken ct) { if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)) return Unauthorized(); var u = await db.Customers.AsNoTracking().Where(x => x.Id == id && x.IsActive && !x.IsGuest).SingleOrDefaultAsync(ct); return u is null ? NotFound() : Ok(UserDto(u)); }

    // SQL lưu bản băm của refresh token. Access token JWT được ký và trả về cho BFF.
    private async Task<object> IssueAsync(Customer user, CancellationToken ct) { var plain = TokenService.NewOpaqueToken(); db.Set<RefreshToken>().Add(new RefreshToken { CustomerId = user.Id, FamilyId = Guid.NewGuid(), TokenHash = TokenService.Hash(plain), ExpiresAtUtc = DateTime.UtcNow.AddDays(config.GetValue("Jwt:RefreshExpirationDays", 30)) }); await db.SaveChangesAsync(ct); var access = TokenService.AccessToken(user, config, out var expires); return new { accessToken = access, refreshToken = plain, expiresAt = expires, user = UserDto(user) }; }
    private static object UserDto(Customer x) => new { x.Id, x.FullName, x.Email, x.PhoneNumber, x.Role };
}

public sealed record RegisterRequest([Required, StringLength(120, MinimumLength = 2)] string FullName, [Required, EmailAddress, StringLength(254)] string Email, [Required, RegularExpression(@"^\+?[0-9]{9,15}$")] string PhoneNumber, [Required, StringLength(128, MinimumLength = 10)] string Password);
public sealed record LoginRequest([Required, EmailAddress] string Email, [Required, StringLength(128)] string Password);
public sealed record RefreshRequest([Required] string RefreshToken);
public sealed record ForgotPasswordRequest([Required, EmailAddress] string Email);
public sealed record ForgotResetPasswordRequest([Required] string Token, [Required, StringLength(128, MinimumLength = 10)] string NewPassword);
public sealed record ChangePasswordRequest([Required, StringLength(128)] string CurrentPassword, [Required, StringLength(128, MinimumLength = 10)] string NewPassword, [Required, StringLength(128, MinimumLength = 10)] string ConfirmNewPassword);
