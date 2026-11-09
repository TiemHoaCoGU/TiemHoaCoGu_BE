using Microsoft.AspNetCore.DataProtection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using HoaSap.API.Middleware;
using HoaSap.API.Security;
using HoaSap.API.Services;
using HoaSap.BLL.Commerce;
using HoaSap.BLL.CustomOrders;
using HoaSap.BLL.Workshops;
using HoaSap.DAL;
using HoaSap.DAL.Auditing;
using HoaSap.Domain.Entities;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);
var isDev = builder.Environment.IsDevelopment();
var paymentProvider = builder.Configuration["Payments:Gateway:Provider"] ?? "";
if (!builder.Environment.IsEnvironment("Testing") && paymentProvider != "MomoSandbox" && paymentProvider != "VnPaySandbox")
    throw new InvalidOperationException("Only verified sandbox payment adapters (MomoSandbox / VnPaySandbox) may be used by a running app; generic/live payment providers are disabled.");
var carrierProvider = builder.Configuration["Shipping:Carrier:Provider"] ?? "Disabled";
if (!builder.Environment.IsEnvironment("Testing") && carrierProvider != "Disabled" && carrierProvider != "GhnSandbox" && carrierProvider != "GhnStaging")
    throw new InvalidOperationException("Carrier calls require a verified GHN staging adapter (GhnSandbox/GhnStaging); generic/live carrier endpoints are prohibited.");
if (!isDev && (builder.Configuration.GetValue<bool>("Database:Initialize") || builder.Configuration.GetValue<bool>("Database:Seed")))
    throw new InvalidOperationException("Production requires reviewed database migrations; development initialization and seed must be disabled.");
builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MaxRequestBodySize = 1024 * 1024;
});
var key = builder.Configuration["Jwt:Key"];
if (string.IsNullOrWhiteSpace(key))
{
    if (!isDev) throw new InvalidOperationException("Set Jwt__Key (at least 32 bytes) outside source control.");
    key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
    builder.Configuration["Jwt:Key"] = key;
}
if (Encoding.UTF8.GetByteCount(key) < 32) throw new InvalidOperationException("Jwt key must be at least 32 bytes.");
builder.Services.AddHttpContextAccessor();
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("HoaSap");
var protectionPath = builder.Configuration["DataProtection:KeyPath"];
if (!string.IsNullOrWhiteSpace(protectionPath)) dataProtection.PersistKeysToFileSystem(new DirectoryInfo(Path.GetFullPath(protectionPath)));
builder.Services.Configure<NotificationOptions>(builder.Configuration.GetSection("Notifications"));
builder.Services.AddHttpClient("notifications");
builder.Services.Configure<GhnStagingOptions>(builder.Configuration.GetSection("Shipping:GhnStaging"));
builder.Services.AddHttpClient<GhnStagingAddressCatalog>(client => client.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddScoped<IGhnStagingAddressCatalog>(sp => sp.GetRequiredService<GhnStagingAddressCatalog>());
builder.Services.AddSingleton<INotificationTransport, SmtpNotificationTransport>();
builder.Services.AddSingleton<INotificationTransport, ResendEmailTransport>();
builder.Services.AddSingleton<INotificationTransport, TwilioSmsNotificationTransport>();
builder.Services.AddSingleton<WorkshopTicketCodec>();
builder.Services.AddScoped<IAuditActor, HttpAuditActor>();
builder.Services.AddDbContext<HoaSapDbContext>(options =>
{
    var provider = builder.Configuration["Database:Provider"] ?? "Sqlite";
    var connection = builder.Configuration.GetConnectionString("DefaultConnection")!;
    if (provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase)) options.UseSqlServer(connection);
    else if (provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase)) options.UseSqlite(connection);
    else throw new InvalidOperationException("Unsupported Database:Provider");
});
builder.Services.AddScoped<CommerceService>();
builder.Services.AddScoped<WorkshopService>();
builder.Services.AddSingleton(sp => { var o = new VnPayOptions(); builder.Configuration.GetSection("Payments:VnPay").Bind(o); return o; });
builder.Services.AddHttpClient<VnPayClient>(client => client.DefaultRequestHeaders.UserAgent.ParseAdd("HoaSap/1.0")).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddScoped<VnPayBookingService>();
builder.Services.AddScoped<RefundService>();
builder.Services.AddScoped<CustomOrderService>();
builder.Services.AddScoped<ShippingQuoteService>();
builder.Services.AddScoped<GatewayReconciliationService>();
builder.Services.AddCommerceAdapters(
    o => builder.Configuration.GetSection("Payments:Gateway").Bind(o),
    o => builder.Configuration.GetSection("Shipping:Carrier").Bind(o));
builder.Services.AddHostedService<BookingExpiryWorker>();
builder.Services.AddHostedService<VnPayPollWorker>();
builder.Services.AddHostedService<NotificationWorker>();
builder.Services.AddHostedService<OrderExpiryWorker>();
builder.Services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo { Title = "Tiệm Hoa Có Gu API", Version = "v1", Description = "E-commerce, hoa đặt riêng và workshop. Adapter thanh toán/vận chuyển yêu cầu cấu hình nhà cung cấp; ghi nhận thủ công vẫn khả dụng." });
    o.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme { Name = "Authorization", Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT", In = ParameterLocation.Header });
    o.AddSecurityRequirement(new OpenApiSecurityRequirement { [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = Array.Empty<string>() });
});
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = builder.Configuration["Jwt:Issuer"],
        ValidAudience = builder.Configuration["Jwt:Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!)),
        ClockSkew = TimeSpan.FromSeconds(30)
    };
    o.Events = new JwtBearerEvents
    {
        OnTokenValidated = async context =>
        {
            if (!Guid.TryParse(context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id)) { context.Fail("Invalid subject"); return; }
            var db = context.HttpContext.RequestServices.GetRequiredService<HoaSapDbContext>();
            var user = await db.Customers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && !x.IsGuest && x.IsActive);
            var presentedStamp = context.Principal?.FindFirstValue(SecurityStampService.ClaimType);
            var key = builder.Configuration["Jwt:Key"]!;
            if (user is null || user.Role != context.Principal?.FindFirstValue(ClaimTypes.Role) ||
                !SecurityStampService.Matches(presentedStamp, user.PasswordHash, key))
                context.Fail("Account, role, or security stamp no longer valid");
        }
    };
});
builder.Services.AddAuthorization();
var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => { if (origins.Length > 0) p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().WithExposedHeaders("X-Request-ID", "Retry-After"); }));
builder.Services.AddRateLimiter(o =>
{
    var globalPermitLimit = builder.Configuration.GetValue("RateLimiting:PermitLimit", 300);
    if (globalPermitLimit is < 1 or > 1000000) throw new InvalidOperationException("RateLimiting:PermitLimit must be between 1 and 1000000.");
    var authPermitLimit = builder.Configuration.GetValue("RateLimiting:AuthPermitLimit", 15);
    if (authPermitLimit is < 1 or > 10000) throw new InvalidOperationException("RateLimiting:AuthPermitLimit must be between 1 and 10000.");
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (rejected, ct) =>
    {
        var response = rejected.HttpContext.Response;
        if (rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        await response.WriteAsJsonAsync(new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = "Quá nhiều yêu cầu. Vui lòng thử lại sau.",
            Instance = rejected.HttpContext.Request.Path,
            Extensions = { ["traceId"] = rejected.HttpContext.TraceIdentifier }
        }, options: null, contentType: "application/problem+json", cancellationToken: ct);
    };
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = globalPermitLimit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = authPermitLimit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
var forwardedHeaders = TrustedProxyConfiguration.CreateOptions(app.Configuration);
app.UseForwardedHeaders(forwardedHeaders);
app.UseMiddleware<RequestDiagnosticsMiddleware>();
app.UseMiddleware<ExceptionMiddleware>();
if (isDev) { app.UseSwagger(); app.UseSwaggerUI(); }
else { app.UseHsts(); app.UseHttpsRedirection(); }
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/admin") || context.Request.Path.StartsWithSegments("/shop"))
    {
        context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; img-src 'self' http: https: data:; style-src 'self' 'unsafe-inline'; script-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; object-src 'none'";
        context.Response.Headers.XContentTypeOptions = "nosniff";
    }
    await next();
});
app.UseStaticFiles();
app.UseCors();
app.UseRateLimiter();
if (builder.Configuration.GetValue("RateLimiting:Distributed", !isDev))
    app.UseMiddleware<DistributedRateLimitMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/", () => Results.Redirect("/shop/index.html"));
app.MapGet("/shop", () => Results.Redirect("/shop/index.html"));
app.MapGet("/admin", () => Results.Redirect("/admin/index.html"));
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapGet("/health/ready", async (HoaSapDbContext db, CancellationToken ct) =>
{
    try
    {
        // Query a mapped table as well as opening a connection: an empty database is not ready.
        await db.Orders.AsNoTracking().Select(x => x.Id).Take(1).ToListAsync(ct);
        await db.Refunds.AsNoTracking().Select(x => new { x.TransactionCode, x.RequestedAt }).Take(1).ToListAsync(ct);
        await db.AuditLogs.AsNoTracking().Select(x => new { x.EntityType, x.OccurredAtUtc }).Take(1).ToListAsync(ct);
        await db.Customers.AsNoTracking().Select(x => x.IsActive).Take(1).ToListAsync(ct);
        await db.Categories.AsNoTracking().Select(x => x.ImageUrl).Take(1).ToListAsync(ct);
        await db.Products.AsNoTracking().Select(x => x.ShippingWeightGrams).Take(1).ToListAsync(ct);
        await db.GatewayPaymentIntents.AsNoTracking().Select(x => x.ClientKey).Take(1).ToListAsync(ct);
        await db.Set<HoaSap.Domain.GatewayRefundAttempt>().AsNoTracking().Select(x => x.Id).Take(1).ToListAsync(ct);
        await db.Set<HoaSap.Domain.RefreshToken>().AsNoTracking().Select(x => x.TokenHash).Take(1).ToListAsync(ct);
        await db.Set<HoaSap.Domain.PasswordResetToken>().AsNoTracking().Select(x => x.Id).Take(1).ToListAsync(ct);
        await db.Set<HoaSap.Domain.NotificationOutbox>().AsNoTracking().Select(x => x.Id).Take(1).ToListAsync(ct);
        await db.Set<HoaSap.Domain.RateLimitCounter>().AsNoTracking().Select(x => x.Id).Take(1).ToListAsync(ct);
        await db.Set<CustomQuotationRevision>().AsNoTracking().Select(x => x.Id).Take(1).ToListAsync(ct);
        await db.ShippingQuotes.AsNoTracking().Select(x => x.CartFingerprint).Take(1).ToListAsync(ct);
        return Results.Ok(new { status = "ready" });
    }
    catch (Exception) when (!ct.IsCancellationRequested)
    {
        return Results.Json(new { status = "not_ready" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).AllowAnonymous();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<HoaSapDbContext>();
    if (builder.Configuration.GetValue<bool>("Database:Initialize")) await db.Database.EnsureCreatedAsync();
    if (builder.Configuration.GetValue<bool>("Database:Seed")) await DataSeeder.SeedAsync(db);
    var adminEmail = builder.Configuration["BootstrapAdmin:Email"]?.Trim().ToLowerInvariant();
    var adminPassword = builder.Configuration["BootstrapAdmin:Password"];
    if (!string.IsNullOrWhiteSpace(adminEmail) && !string.IsNullOrWhiteSpace(adminPassword))
    {
        if (adminPassword.Length < 12) throw new InvalidOperationException("Bootstrap admin password must have at least 12 characters.");
        if (!await db.Customers.AnyAsync(x => x.Email == adminEmail))
        {
            var phone = builder.Configuration["BootstrapAdmin:PhoneNumber"];
            if (string.IsNullOrWhiteSpace(phone)) throw new InvalidOperationException("Bootstrap admin phone is required.");
            db.Customers.Add(new Customer { Email = adminEmail, FullName = builder.Configuration["BootstrapAdmin:FullName"] ?? "Admin", PhoneNumber = phone, PasswordHash = PasswordService.Hash(adminPassword), Role = "Admin", IsGuest = false, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
    }
}
await app.RunAsync();
public partial class Program { }
