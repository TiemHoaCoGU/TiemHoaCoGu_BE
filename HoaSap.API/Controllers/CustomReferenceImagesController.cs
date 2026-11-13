using System.Security.Claims;
using HoaSap.API.Storage;
using HoaSap.BLL;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HoaSap.API.Controllers;

[ApiController, Authorize, Route("api/custom-orders/reference-images")]
public sealed class CustomReferenceImagesController(IWebHostEnvironment environment, IConfiguration configuration) : ControllerBase
{
    private const int Limit = 900 * 1024;
    private Guid Actor => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
        ? id : throw new BusinessException(401, "Yêu cầu đăng nhập.");
    private string Root => Path.Combine((configuration.GetSection(MediaStorageOptions.SectionName).Get<MediaStorageOptions>() ?? new()).Resolve(environment.ContentRootPath), "custom-reference");

    [HttpPost]
    [RequestSizeLimit(1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 1024 * 1024)]
    public async Task<IActionResult> Upload(IFormFile file, CancellationToken ct)
    {
        var actor = Actor;
        if (file is null || file.Length <= 0 || file.Length > Limit)
            throw new BusinessException(400, "Chọn ảnh PNG, JPEG hoặc WebP không quá 900 KiB.");
        await using var input = file.OpenReadStream();
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + count > Limit) throw new BusinessException(400, "Ảnh vượt quá 900 KiB.");
            await buffer.WriteAsync(chunk.AsMemory(0, count), ct);
        }
        var data = buffer.ToArray();
        var format = MediaFileValidator.Identify(data);
        if (format is null) throw new BusinessException(400, "Chỉ chấp nhận ảnh PNG, JPEG hoặc WebP hợp lệ.");
        var name = Guid.NewGuid().ToString("N") + format.Value.Extension;
        var owner = actor.ToString("N");
        var folder = Path.Combine(Root, owner);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        try
        {
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, true);
            await output.WriteAsync(data, ct);
        }
        catch
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            throw;
        }
        return StatusCode(201, new { path = $"/api/reference-images/{owner}/{name}" });
    }

    [HttpGet("{owner}/{fileName}")]
    public IActionResult Read(string owner, string fileName)
    {
        if (!Guid.TryParseExact(owner, "N", out var ownerId) || !MediaFileValidator.IsSafeName(fileName)) return NotFound();
        if (Actor != ownerId && !User.IsInRole("Admin") && !User.IsInRole("Staff")) return NotFound();
        var path = Path.Combine(Root, ownerId.ToString("N"), fileName);
        if (!System.IO.File.Exists(path)) return NotFound();
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
        Response.Headers.CacheControl = "private,no-store";
        return PhysicalFile(path, MediaFileValidator.ContentType(fileName)!);
    }
}
