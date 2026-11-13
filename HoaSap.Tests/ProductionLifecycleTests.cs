using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HoaSap.DAL;
using HoaSap.BLL.Workshops;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HoaSap.Tests;

public sealed class ProductionLifecycleTests
{
    static StringContent Json(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
    static async Task<(ApiFixture F, HttpClient Admin)> StartAsync()
    {
        var f = new ApiFixture(); var c = f.CreateClient();
        var r = await c.PostAsync("/api/auth/login", Json(new { email = ApiFixture.AdminEmail, password = ApiFixture.AdminPassword })); r.EnsureSuccessStatusCode();
        using var d = JsonDocument.Parse(await r.Content.ReadAsStringAsync()); c.DefaultRequestHeaders.Authorization = new("Bearer", d.RootElement.GetProperty("accessToken").GetString());
        return (f, c);
    }
    static async Task<HttpClient> UserAsync(ApiFixture f)
    {
        var c = f.CreateClient(); var email = $"qa-{Guid.NewGuid():N}@example.test";
        var r = await c.PostAsync("/api/auth/register", Json(new { fullName = "QA", phoneNumber = "09" + Random.Shared.Next(100000000, 999999999), email, password = "Password-123!" })); r.EnsureSuccessStatusCode();
        using var d = JsonDocument.Parse(await r.Content.ReadAsStringAsync()); c.DefaultRequestHeaders.Authorization = new("Bearer", d.RootElement.GetProperty("accessToken").GetString()); return c;
    }
    static async Task<Guid> ProductAsync(HttpClient admin, int stock = 2)
    {
        var cat = await admin.PostAsync("/api/management/categories", Json(new { code = "T-" + Guid.NewGuid().ToString("N")[..8], name = "QA" })); cat.EnsureSuccessStatusCode();
        using var cd = JsonDocument.Parse(await cat.Content.ReadAsStringAsync()); var categoryId = cd.RootElement.GetProperty("id").GetGuid();
        // Synthetic measurements are confined to this test helper; production seed SKUs remain unverified.
        var p = await admin.PostAsync("/api/management/products", Json(new { code = "P-" + Guid.NewGuid().ToString("N")[..8], name = "QA Flower", categoryId, price = 100000m, shippingWeightGrams = 500, packageLengthCm = 30m, packageWidthCm = 20m, packageHeightCm = 10m, packagingSpecsConfirmed = true, isActive = true })); p.EnsureSuccessStatusCode();
        using var pd = JsonDocument.Parse(await p.Content.ReadAsStringAsync()); var id = pd.RootElement.GetProperty("id").GetGuid();
        (await admin.PutAsync($"/api/management/inventory/{id}?quantity={stock}", Json(new { }))).EnsureSuccessStatusCode(); return id;
    }
    static async Task<Guid> OrderIdAsync(HttpResponseMessage response) { response.EnsureSuccessStatusCode(); using var d = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return d.RootElement.GetProperty("id").GetGuid(); }
    static async Task<(Guid Id, string Destination)> ShippingQuoteAsync(HttpClient c, string street, params (Guid ProductId, int Quantity)[] items)
    {
        var response = await c.PostAsync("/api/shipping/quote", Json(new { streetAddress = street, provinceId = 1000001, wardId = 1003580, items = items.Select(x => new { productId = x.ProductId, quantity = x.Quantity }).ToArray() }));
        response.EnsureSuccessStatusCode(); using var d = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (d.RootElement.GetProperty("id").GetGuid(), d.RootElement.GetProperty("destination").GetString()!);
    }
    static async Task<(Guid OrderId, Guid ItemId)> OrderAsync(HttpClient customer, Guid product, string? key = null, int quantity = 1)
    {
        var quote = await ShippingQuoteAsync(customer, "QA", (product, quantity));
        var id = await OrderIdAsync(await customer.PostAsync("/api/orders/checkout", Json(new { shippingAddress = quote.Destination, shippingQuoteId = quote.Id, idempotencyKey = key, items = new[] { new { productId = product, quantity } } })));
        using var d = JsonDocument.Parse(await (await customer.GetAsync($"/api/orders/{id}")).Content.ReadAsStringAsync()); return (id, d.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task CheckoutReplay_IsSameOrderAndReservationAndChangedPayloadConflicts()
    {
        var (f, admin) = await StartAsync(); using (f) using (admin) { var p = await ProductAsync(admin, 2); using var c = await UserAsync(f); const string key = "checkout-replay";
            var quote = await ShippingQuoteAsync(c, "A", (p, 1));
            var first = await c.PostAsync("/api/orders/checkout", Json(new { shippingAddress = quote.Destination, shippingQuoteId = quote.Id, idempotencyKey = key, items = new[] { new { productId = p, quantity = 1 } } })); var id = await OrderIdAsync(first);
            var replay = await c.PostAsync("/api/orders/checkout", Json(new { shippingAddress = quote.Destination, shippingQuoteId = quote.Id, idempotencyKey = key, items = new[] { new { productId = p, quantity = 1 } } })); Assert.Equal(HttpStatusCode.OK, replay.StatusCode); Assert.Equal(id, await OrderIdAsync(replay));
            Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync("/api/orders/checkout", Json(new { shippingAddress = quote.Destination, shippingQuoteId = quote.Id, idempotencyKey = key, items = new[] { new { productId = p, quantity = 2 } } }))).StatusCode);
            using var s = f.Services.CreateScope(); var db = s.ServiceProvider.GetRequiredService<HoaSapDbContext>(); var inv = await db.Inventories.SingleAsync(x => x.ProductId == p); Assert.Equal(1, inv.QuantityAvailable); Assert.Equal(1, inv.QuantityReserved);
        }
    }

    [Fact]
    public async Task AuthenticatedKeysAreIsolatedAndGuestCheckoutRequiresAnAuthenticatedShippingQuote()
    {
        var (f, admin) = await StartAsync(); using (f) using (admin) { var p = await ProductAsync(admin, 5); using var a = await UserAsync(f); using var b = await UserAsync(f); const string key = "shared-key";
            var quoteA = await ShippingQuoteAsync(a, "A", (p, 1)); var quoteB = await ShippingQuoteAsync(b, "B", (p, 1));
            Assert.Equal(HttpStatusCode.OK, (await a.PostAsync("/api/orders/checkout", Json(new { shippingAddress = quoteA.Destination, shippingQuoteId = quoteA.Id, idempotencyKey = key, items = new[] { new { productId = p, quantity = 1 } } }))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await b.PostAsync("/api/orders/checkout", Json(new { shippingAddress = quoteB.Destination, shippingQuoteId = quoteB.Id, idempotencyKey = key, items = new[] { new { productId = p, quantity = 1 } } }))).StatusCode);
            var guest = f.CreateClient(); var body = new { shippingAddress = "G", receiverName = "Guest", receiverPhone = "0901234567", idempotencyKey = "guest-key", items = new[] { new { productId = p, quantity = 1 } } };
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await guest.PostAsync("/api/orders/checkout", Json(body))).StatusCode);
        }
    }

    [Fact]
    public async Task CartCheckoutReplayWorksAfterCartSnapshotDeletion()
    {
        var (f, admin) = await StartAsync(); using (f) using (admin) { var p = await ProductAsync(admin, 2); using var c = await UserAsync(f); Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsync("/api/cart", Json(new { productId = p, quantity = 1 }))).StatusCode); const string key = "cart-replay";
            var quote = await ShippingQuoteAsync(c, "A");
            var first = await c.PostAsync("/api/orders/checkout", Json(new { shippingAddress = quote.Destination, shippingQuoteId = quote.Id, idempotencyKey = key, items = Array.Empty<object>() })); var id = await OrderIdAsync(first); using (var cartDoc = JsonDocument.Parse(await (await c.GetAsync("/api/cart")).Content.ReadAsStringAsync())) Assert.Empty(cartDoc.RootElement.EnumerateArray());
            var replay = await c.PostAsync("/api/orders/checkout", Json(new { shippingAddress = quote.Destination, shippingQuoteId = quote.Id, idempotencyKey = key, items = Array.Empty<object>() })); Assert.Equal(HttpStatusCode.OK, replay.StatusCode); Assert.Equal(id, await OrderIdAsync(replay));
        }
    }

    [Fact]
    public async Task DeliveryLifecycleEnforcesStatesAndOwnerTrackingAuthorization()
    {
        var (f, admin) = await StartAsync(); using (f) using (admin) { var p = await ProductAsync(admin, 1); using var c = await UserAsync(f); using var other = await UserAsync(f); var (order, item) = await OrderAsync(c, p);
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/orders/{order}/status", Json(new { status = "Processing" }))).StatusCode);
            var delivery = await admin.PostAsync($"/api/orders/{order}/deliveries", Json(new { items = new[] { new { orderItemId = item, quantity = 1 } } })); delivery.EnsureSuccessStatusCode(); using var dd = JsonDocument.Parse(await delivery.Content.ReadAsStringAsync()); var did = dd.RootElement.GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/orders/{order}/deliveries/{did}/status", Json(new { status = "Completed" }))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/orders/{order}/deliveries")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/orders/{order}/deliveries/{did}/tracking")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/orders/{order}/deliveries/{did}/tracking")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/orders/{order}/deliveries/{did}/tracking")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/orders/{Guid.NewGuid()}/deliveries")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/payments/order/{order}", Json(new { transactionCode = "PAY-" + Guid.NewGuid(), paymentMethod = "Cash", amount = 142000m }))).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/orders/{order}/status", Json(new { status = "Delivering" }))).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/orders/{order}/status", Json(new { status = "Completed" }))).StatusCode);
            foreach (var status in new[] { "Shipped", "Delivered" }) Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/orders/{order}/deliveries/{did}/status", Json(new { status }))).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/orders/{order}/status", Json(new { status = "Completed" }))).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/orders/{order}/status", Json(new { status = "Completed" }))).StatusCode);
            using var s = f.Services.CreateScope(); var db = s.ServiceProvider.GetRequiredService<HoaSapDbContext>(); var inv = await db.Inventories.SingleAsync(x => x.ProductId == p); Assert.Equal(0, inv.QuantityReserved); Assert.Equal(0, inv.QuantityAvailable);
        }
    }

    [Fact]
    public async Task ActiveDeliveryBlocksCancel_ThenCancelledDeliveryAllowsOneCancel()
    {
        var (f, admin) = await StartAsync(); using (f) using (admin) { var p = await ProductAsync(admin, 1); using var c = await UserAsync(f); var (order, item) = await OrderAsync(c, p); await admin.PostAsync($"/api/orders/{order}/status", Json(new { status = "Processing" })); var r = await admin.PostAsync($"/api/orders/{order}/deliveries", Json(new { items = new[] { new { orderItemId = item, quantity = 1 } } })); using var d = JsonDocument.Parse(await r.Content.ReadAsStringAsync()); var did = d.RootElement.GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync($"/api/orders/{order}/cancel", null)).StatusCode); Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/orders/{order}/deliveries/{did}/status", Json(new { status = "Cancelled" }))).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsync($"/api/orders/{order}/cancel", null)).StatusCode); Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsync($"/api/orders/{order}/cancel", null)).StatusCode);
            using var s = f.Services.CreateScope(); var inv = await s.ServiceProvider.GetRequiredService<HoaSapDbContext>().Inventories.SingleAsync(x => x.ProductId == p); Assert.Equal(1, inv.QuantityAvailable); Assert.Equal(0, inv.QuantityReserved);
        }
    }

    [Fact]
    public async Task FailedDeliveryCanBeRetriedThenReturnedAndTheItemBecomesShippableAgain()
    {
        var (f, admin) = await StartAsync(); using (f) using (admin)
        {
            var p = await ProductAsync(admin, 1); using var c = await UserAsync(f); var (order, item) = await OrderAsync(c, p);
            (await admin.PostAsync($"/api/orders/{order}/status", Json(new { status = "Processing" }))).EnsureSuccessStatusCode();
            var delivery = await admin.PostAsync($"/api/orders/{order}/deliveries", Json(new { items = new[] { new { orderItemId = item, quantity = 1 } } }));
            delivery.EnsureSuccessStatusCode(); using var dd = JsonDocument.Parse(await delivery.Content.ReadAsStringAsync()); var did = dd.RootElement.GetProperty("id").GetGuid();
            (await admin.PostAsync($"/api/orders/{order}/status", Json(new { status = "Delivering" }))).EnsureSuccessStatusCode();

            var statusPath = $"/api/orders/{order}/deliveries/{did}/status";
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync(statusPath, Json(new { status = "Shipped" }))).StatusCode);
            // Delivery attempt fails, the shop retries, the retry fails again, then the parcel goes back.
            foreach (var status in new[] { "Failed", "Shipped", "Failed", "Returned" })
                Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync(statusPath, Json(new { status }))).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/orders/{order}/status", Json(new { status = "Completed" }))).StatusCode);

            // Returned goods are physically back at the shop, so the same order item can be shipped again.
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/orders/{order}/deliveries", Json(new { items = new[] { new { orderItemId = item, quantity = 1 } } }))).StatusCode);
        }
    }

    [Fact]
    public async Task ReturnedDeliveryLetsAnUnpaidOrderBeCancelledAndReleasesStock()
    {
        var (f, admin) = await StartAsync(); using (f) using (admin)
        {
            var p = await ProductAsync(admin, 1); using var c = await UserAsync(f); var (order, item) = await OrderAsync(c, p);
            (await admin.PostAsync($"/api/orders/{order}/status", Json(new { status = "Processing" }))).EnsureSuccessStatusCode();
            var delivery = await admin.PostAsync($"/api/orders/{order}/deliveries", Json(new { items = new[] { new { orderItemId = item, quantity = 1 } } }));
            delivery.EnsureSuccessStatusCode(); using var dd = JsonDocument.Parse(await delivery.Content.ReadAsStringAsync()); var did = dd.RootElement.GetProperty("id").GetGuid();
            (await admin.PostAsync($"/api/orders/{order}/status", Json(new { status = "Delivering" }))).EnsureSuccessStatusCode();
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/orders/{order}/deliveries/{did}/status", Json(new { status = "Shipped" }))).StatusCode);

            // While the parcel is still with the carrier the order cannot be cancelled.
            Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/orders/{order}/status", Json(new { status = "Cancelled" }))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/orders/{order}/deliveries/{did}/status", Json(new { status = "Returned" }))).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/orders/{order}/status", Json(new { status = "Cancelled" }))).StatusCode);

            using var s = f.Services.CreateScope(); var inv = await s.ServiceProvider.GetRequiredService<HoaSapDbContext>().Inventories.SingleAsync(x => x.ProductId == p);
            Assert.Equal(1, inv.QuantityAvailable); Assert.Equal(0, inv.QuantityReserved);
        }
    }


    [Fact]
    public async Task MaliciousDuplicateAndNonPositiveLinesAreBadRequest()
    {
        var (f, admin) = await StartAsync(); using (f) using (admin) { var p = await ProductAsync(admin, 3); using var c = await UserAsync(f);
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/orders/checkout", Json(new { shippingAddress = "A", items = new[] { new { productId = p, quantity = 0 }, new { productId = p, quantity = -1 } } }))).StatusCode);
            var (order, item) = await OrderAsync(c, p); await admin.PostAsync($"/api/orders/{order}/status", Json(new { status = "Processing" })); Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/orders/{order}/deliveries", Json(new { items = new[] { new { orderItemId = item, quantity = 0 } } }))).StatusCode);
        }
    }

    [Fact]
    public async Task ShipmentUsesTheSamePackagingModelAsTheQuote()
    {
        var (f, admin) = await StartAsync(); using (f) using (admin)
        {
            var p = await ProductAsync(admin, 3); using var c = await UserAsync(f); var (order, item) = await OrderAsync(c, p, quantity: 2);
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/orders/{order}/status", Json(new { status = "Processing" }))).StatusCode);
            (await admin.PostAsync($"/api/orders/{order}/deliveries", Json(new { items = new[] { new { orderItemId = item, quantity = 2 } } }))).EnsureSuccessStatusCode();
            var shipment = f.LastShipment!;
            Assert.Equal(1.0m, shipment.WeightKg);   // 2 x 500 g, the same product weight the quote priced
            Assert.Equal(30, shipment.LengthCm);     // largest footprint of the parcel
            Assert.Equal(20, shipment.WidthCm);
            Assert.Equal(20, shipment.HeightCm);     // 2 boxes stacked, 10 cm each
            Assert.Equal("Phường Thủ Đức", shipment.ToWardName);
            Assert.Equal("Hồ Chí Minh", shipment.ToProvinceName);
        }
    }

    [Fact]
    public async Task ParallelLastStockCheckoutHasAtMostOneSuccess()
    {
        var (f, admin) = await StartAsync(); using (f) using (admin) { var p = await ProductAsync(admin, 1); using var a = await UserAsync(f); using var b = await UserAsync(f);
            var quoteA = await ShippingQuoteAsync(a, "A", (p, 1)); var quoteB = await ShippingQuoteAsync(b, "A", (p, 1));
            var requests = new[] { (a, "race-a", quoteA), (b, "race-b", quoteB) }.Select(x => x.Item1.PostAsync("/api/orders/checkout", Json(new { shippingAddress = x.Item3.Destination, shippingQuoteId = x.Item3.Id, idempotencyKey = x.Item2, items = new[] { new { productId = p, quantity = 1 } } }))).ToArray(); var results = await Task.WhenAll(requests); Assert.Equal(1, results.Count(x => x.IsSuccessStatusCode)); Assert.True(results.All(x => x.IsSuccessStatusCode || x.StatusCode == HttpStatusCode.Conflict));
            using var scope = f.Services.CreateScope(); var stock = await scope.ServiceProvider.GetRequiredService<HoaSapDbContext>().Inventories.SingleAsync(x => x.ProductId == p); Assert.Equal(0, stock.QuantityAvailable); Assert.Equal(1, stock.QuantityReserved);
        }
    }

    [Fact]
    public async Task ParallelLastWorkshopSeatHasAtMostOneSuccess()
    {
        var (f, admin) = await StartAsync(); using (f) using (admin) { var w = await admin.PostAsync("/api/workshops", Json(new { code = "W-" + Guid.NewGuid().ToString("N")[..8], name = "QA", basePrice = 100000m, duration = 60, maxCapacity = 1, isActive = true })); w.EnsureSuccessStatusCode(); using var wd = JsonDocument.Parse(await w.Content.ReadAsStringAsync()); var wid = wd.RootElement.GetProperty("id").GetGuid(); var date = DateTime.UtcNow.AddDays(2).Date; var sr = await admin.PostAsync($"/api/workshops/{wid}/sessions", Json(new { sessionDate = date, startTime = "10:00:00", endTime = "11:00:00", location = "QA", capacity = 1 })); sr.EnsureSuccessStatusCode(); using var sd = JsonDocument.Parse(await sr.Content.ReadAsStringAsync()); var sid = sd.RootElement.GetProperty("id").GetGuid(); using var a = await UserAsync(f); using var b = await UserAsync(f);
            var results = await Task.WhenAll(a.PostAsync($"/api/bookings/sessions/{sid}", Json(new { ticketCount = 1, customerName = "A", customerPhone = "0901234567", idempotencyKey = "seat-a" })), b.PostAsync($"/api/bookings/sessions/{sid}", Json(new { ticketCount = 1, customerName = "B", customerPhone = "0901234568", idempotencyKey = "seat-b" }))); Assert.Equal(1, results.Count(x => x.IsSuccessStatusCode)); Assert.True(results.All(x => x.IsSuccessStatusCode || x.StatusCode == HttpStatusCode.Conflict));
            using var scope = f.Services.CreateScope(); Assert.Equal(1, (await scope.ServiceProvider.GetRequiredService<HoaSapDbContext>().WorkshopSessions.FindAsync(sid))!.BookedSlots);
        }
    }
}
