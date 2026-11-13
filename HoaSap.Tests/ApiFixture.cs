using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using HoaSap.BLL.Commerce;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HoaSap.Tests;

public sealed class ApiFixture : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"hoasap-tests-{Guid.NewGuid():N}.db");
    private readonly int _authPermitLimit;
    public ApiFixture() : this(1000) { }
    internal ApiFixture(int authPermitLimit) => _authPermitLimit = authPermitLimit;
    public const string AdminEmail = "qa-admin@example.test";
    public const string AdminPassword = "A-very-long-test-password-123!";
    public const string TestJwtKey = "qa-only-key-with-at-least-32-bytes-123456";
    internal string? CachedAdminToken { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite",
            ["ConnectionStrings:DefaultConnection"] = $"Data Source={_dbPath}",
            ["Media:StoragePath"] = _dbPath + "-media",
            ["Database:Initialize"] = "true",
            ["Database:Seed"] = "false",
            ["RateLimiting:PermitLimit"] = "100000",
            ["RateLimiting:AuthPermitLimit"] = _authPermitLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Jwt:Key"] = TestJwtKey,
            ["Jwt:Issuer"] = "qa-tests",
            ["Jwt:Audience"] = "qa-tests",
            ["BootstrapAdmin:Email"] = AdminEmail,
            ["BootstrapAdmin:Password"] = AdminPassword,
            ["BootstrapAdmin:PhoneNumber"] = "0900000001",
            ["BootstrapAdmin:FullName"] = "QA Admin",
            ["Shipping:Carrier:Origin"] = "Synthetic QA warehouse"
        }));
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ICarrierAdapter>(new SyntheticQaCarrier(this));
            services.AddSingleton<IGhnStagingAddressCatalog, SyntheticQaAddressCatalog>();
        });
    }

    /// <summary>Last shipment handed to the carrier, so tests can assert the outbound payload.</summary>
    public CarrierShipmentRequest? LastShipment { get; internal set; }

    private sealed class SyntheticQaCarrier(ApiFixture owner) : ICarrierAdapter
    {
        public Task<CarrierRateResult> QuoteAsync(CarrierRateRequest request, CancellationToken ct = default) =>
            Task.FromResult(new CarrierRateResult(true, 42000m, null, null));

        public Task<CarrierShipmentResult> CreateShipmentAsync(CarrierShipmentRequest request, CancellationToken ct = default)
        {
            owner.LastShipment = request;
            return Task.FromResult(new CarrierShipmentResult(true, $"QA-{request.DeliveryCode}", 42000m, DateTime.UtcNow.AddDays(2), "ready_to_pick", null));
        }

        public Task<CarrierCancelResult> CancelShipmentAsync(string trackingNumber, CancellationToken ct = default) =>
            Task.FromResult(new CarrierCancelResult(true, "QA carrier cancelled the shipment.", null));

        public Task<CarrierTrackingResult> TrackShipmentAsync(string trackingNumber, CancellationToken ct = default) =>
            Task.FromResult(new CarrierTrackingResult(true, trackingNumber, "delivering", "Đang giao hàng (QA)", DateTime.UtcNow, null));
    }

    private sealed class SyntheticQaAddressCatalog : IGhnStagingAddressCatalog
    {
        public Task<IReadOnlyList<ShippingAreaDto>> GetProvincesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ShippingAreaDto>>([new ShippingAreaDto(1000001, "Hồ Chí Minh", 1)]);

        public Task<IReadOnlyList<ShippingAreaDto>> GetWardsAsync(int provinceId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ShippingAreaDto>>(provinceId == 1000001
                ? [new ShippingAreaDto(1003580, "Phường Thủ Đức", 1000001)]
                : []);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-shm"); File.Delete(_dbPath + "-wal"); } catch { }
        try { Directory.Delete(_dbPath + "-media", true); } catch { }
    }
}

[CollectionDefinition("api", DisableParallelization = true)]
public sealed class ApiCollection : ICollectionFixture<ApiFixture> { }

public abstract class ApiTestBase
{
    protected readonly HttpClient Client;
    protected ApiFixture Fixture { get; }

    protected ApiTestBase(ApiFixture fixture)
    {
        Fixture = fixture;
        Client = fixture.CreateClient();
    }

    protected static StringContent Json(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    protected sealed record TestShippingQuote(Guid Id, string Destination, decimal Amount);

    protected async Task<TestShippingQuote> ShippingQuoteAsync(HttpClient customer, string streetAddress, params (Guid ProductId, int Quantity)[] items)
    {
        var response = await customer.PostAsync("/api/shipping/quote", Json(new
        {
            streetAddress,
            provinceId = 1000001,
            wardId = 1003580,
            items = items.Select(x => new { productId = x.ProductId, quantity = x.Quantity }).ToArray()
        }));
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return new TestShippingQuote(document.RootElement.GetProperty("id").GetGuid(), document.RootElement.GetProperty("destination").GetString()!, document.RootElement.GetProperty("amount").GetDecimal());
    }

    protected async Task<string> RegisterAsync(string email, string? phone = null, string password = "Password-123!")
    {
        phone ??= "09" + Random.Shared.Next(100000000, 999999999).ToString();
        var response = await Client.PostAsync("/api/auth/register", Json(new { fullName = "QA Customer", phoneNumber = phone, email, password, role = "Admin" }));
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("accessToken").GetString()!;
    }

    protected Task<HttpClient> AuthenticatedAsync(string token)
    {
        var client = Fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return Task.FromResult(client);
    }

    protected async Task<string> AdminTokenAsync()
    {
        if (Fixture.CachedAdminToken is { } cached) return cached;
        var response = await Client.PostAsync("/api/auth/login", Json(new { email = ApiFixture.AdminEmail, password = ApiFixture.AdminPassword }));
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Fixture.CachedAdminToken = document.RootElement.GetProperty("accessToken").GetString()!;
        return Fixture.CachedAdminToken;
    }
}
