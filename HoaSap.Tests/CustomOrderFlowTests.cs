using System.Net;
using System.Text.Json;
using HoaSap.DAL;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace HoaSap.Tests;

[Collection("api")]
public sealed class CustomOrderFlowTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    sealed record Flow(HttpClient Customer, HttpClient Admin, Guid Product, string Email, string Phone);

    async Task<Flow> SetupAsync()
    {
        var admin = await AuthenticatedAsync(await AdminTokenAsync());
        var category = await admin.PostAsync("/api/management/categories", Json(new { code = "FLW-" + Guid.NewGuid().ToString("N")[..8], name = "Hoa tùy biến" }));
        category.EnsureSuccessStatusCode();
        using var c = JsonDocument.Parse(await category.Content.ReadAsStringAsync());
        var product = await admin.PostAsync("/api/management/products", Json(new { categoryId = c.RootElement.GetProperty("id").GetGuid(), code = "FLW-P-" + Guid.NewGuid().ToString("N")[..8], name = "Sản phẩm đặt trước", price = 100000m, isActive = true, isCustomizable = true, isPreOrder = true }));
        product.EnsureSuccessStatusCode();
        using var p = JsonDocument.Parse(await product.Content.ReadAsStringAsync());
        var email = "custom-flow-" + Guid.NewGuid().ToString("N") + "@example.test";
        var phone = "09" + Random.Shared.Next(100000000, 999999999).ToString();
        var customer = await AuthenticatedAsync(await RegisterAsync(email, phone));
        return new(customer, admin, p.RootElement.GetProperty("id").GetGuid(), email, phone);
    }

    static async Task<Guid> IdAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetGuid();
    }

    static async Task<JsonElement> FindAsync(HttpClient customer, Guid id)
    {
        var response = await customer.GetAsync("/api/custom-orders");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == id).Clone();
    }

    static Task<HttpResponseMessage> QuoteAsync(HttpClient admin, Guid id, decimal unitPrice = 200000m, decimal depositPercent = 50m) =>
        admin.PostAsync($"/api/custom-orders/{id}/quote", Json(new { unitPrice, depositPercent, completionDate = DateTime.UtcNow.AddDays(4), adminNote = "Báo giá" }));

    static async Task<Guid> RequestAsync(Flow flow, int quantity = 1, string description = "Mẫu hoa theo yêu cầu")
    {
        var response = await flow.Customer.PostAsync("/api/custom-orders", Json(new { productId = flow.Product, quantity, description }));
        return await IdAsync(response);
    }

    static async Task<(Guid RequestId, Guid OrderId)> CheckedOutAsync(Flow flow, int quantity = 1)
    {
        var id = await RequestAsync(flow, quantity);
        (await QuoteAsync(flow.Admin, id)).EnsureSuccessStatusCode();
        (await flow.Customer.PostAsync($"/api/custom-orders/{id}/approve", Json(new { version = 1 }))).EnsureSuccessStatusCode();
        var checkout = await flow.Customer.PostAsync($"/api/custom-orders/{id}/checkout",
            Json(new { shippingAddress = "12 Đường Hoa, TP.HCM", idempotencyKey = "flow-" + Guid.NewGuid().ToString("N") }));
        checkout.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await checkout.Content.ReadAsStringAsync());
        return (id, body.RootElement.GetProperty("orderId").GetGuid());
    }

    [Fact]
    public async Task RevisingQuotedRequestSupersedesRevisionAndBlocksApprovingOldVersion()
    {
        var flow = await SetupAsync();
        var id = await RequestAsync(flow, 2, "Bó hoa cưới");
        (await QuoteAsync(flow.Admin, id)).EnsureSuccessStatusCode();

        var revise = await flow.Customer.PutAsync($"/api/custom-orders/{id}", Json(new { productId = flow.Product, quantity = 3, description = "Bó hoa cưới lớn hơn", quoteVersion = 1 }));
        Assert.Equal(HttpStatusCode.OK, revise.StatusCode);
        using var revised = JsonDocument.Parse(await revise.Content.ReadAsStringAsync());
        Assert.Equal("Requested", revised.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, revised.RootElement.GetProperty("quoteVersion").GetInt32());

        var quote = await flow.Customer.GetAsync($"/api/custom-orders/{id}");
        quote.EnsureSuccessStatusCode();
        using var q = JsonDocument.Parse(await quote.Content.ReadAsStringAsync());
        Assert.Equal("Requested", q.RootElement.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, q.RootElement.GetProperty("expiresAt").ValueKind);
        Assert.Equal("Superseded", q.RootElement.GetProperty("revisions")[0].GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await flow.Customer.PostAsync($"/api/custom-orders/{id}/approve", Json(new { version = 1 }))).StatusCode);

        (await QuoteAsync(flow.Admin, id)).EnsureSuccessStatusCode();
        var requoted = await flow.Customer.GetAsync($"/api/custom-orders/{id}");
        using var rq = JsonDocument.Parse(await requoted.Content.ReadAsStringAsync());
        Assert.Equal(2, rq.RootElement.GetProperty("currentRevision").GetInt32());
        Assert.Equal("Quoted", rq.RootElement.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, (await flow.Customer.PostAsync($"/api/custom-orders/{id}/approve", Json(new { version = 2 }))).StatusCode);
    }

    [Fact]
    public async Task RevisingWithStaleQuoteVersionIsRejected()
    {
        var flow = await SetupAsync();
        var id = await RequestAsync(flow);
        (await QuoteAsync(flow.Admin, id)).EnsureSuccessStatusCode();
        (await QuoteAsync(flow.Admin, id)).EnsureSuccessStatusCode();

        var stale = await flow.Customer.PutAsync($"/api/custom-orders/{id}", Json(new { productId = flow.Product, quantity = 2, description = "Sửa sau báo giá", quoteVersion = 1 }));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var current = await flow.Customer.PutAsync($"/api/custom-orders/{id}", Json(new { productId = flow.Product, quantity = 2, description = "Sửa sau báo giá", quoteVersion = 2 }));
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
    }

    [Fact]
    public async Task CancelIsOwnerOnlyIdempotentAndBlockedAfterCheckout()
    {
        var flow = await SetupAsync();
        var id = await RequestAsync(flow);
        var other = await AuthenticatedAsync(await RegisterAsync("custom-flow-other-" + Guid.NewGuid().ToString("N") + "@example.test"));
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsync($"/api/custom-orders/{id}/cancel", null)).StatusCode);

        var first = await flow.Customer.PostAsync($"/api/custom-orders/{id}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using (var body = JsonDocument.Parse(await first.Content.ReadAsStringAsync())) Assert.Equal("Cancelled", body.RootElement.GetProperty("status").GetString());
        var again = await flow.Customer.PostAsync($"/api/custom-orders/{id}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        using (var body = JsonDocument.Parse(await again.Content.ReadAsStringAsync())) Assert.Equal("Cancelled", body.RootElement.GetProperty("status").GetString());

        var (checkedOut, _) = await CheckedOutAsync(flow);
        Assert.Equal(HttpStatusCode.Conflict, (await flow.Customer.PostAsync($"/api/custom-orders/{checkedOut}/cancel", null)).StatusCode);
    }

    [Fact]
    public async Task DuplicateCreateWithinTenMinutesReturnsTheExistingRequest()
    {
        var flow = await SetupAsync();
        var payload = new { productId = flow.Product, quantity = 2, description = "Giống hệt nhau", referenceImageUrl = "https://example.test/hoa.png" };
        var first = await IdAsync(await flow.Customer.PostAsync("/api/custom-orders", Json(payload)));
        var second = await IdAsync(await flow.Customer.PostAsync("/api/custom-orders", Json(payload)));
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task CheckoutStoresComposedReceiverAddressAndReplayReturnsSameOrder()
    {
        var flow = await SetupAsync();
        var id = await RequestAsync(flow);
        (await QuoteAsync(flow.Admin, id)).EnsureSuccessStatusCode();
        (await flow.Customer.PostAsync($"/api/custom-orders/{id}/approve", Json(new { version = 1 }))).EnsureSuccessStatusCode();
        var key = "flow-" + Guid.NewGuid().ToString("N");
        var body = new { shippingAddress = "12 Đường Hoa, TP.HCM", receiverName = "Nguyễn An", receiverPhone = "+84 90 123 4567", idempotencyKey = key };
        var checkout = await flow.Customer.PostAsync($"/api/custom-orders/{id}/checkout", Json(body));
        checkout.EnsureSuccessStatusCode();
        using var created = JsonDocument.Parse(await checkout.Content.ReadAsStringAsync());
        var orderId = created.RootElement.GetProperty("orderId").GetGuid();

        var order = await flow.Customer.GetAsync($"/api/orders/{orderId}");
        order.EnsureSuccessStatusCode();
        using var o = JsonDocument.Parse(await order.Content.ReadAsStringAsync());
        Assert.Equal($"ReceiverName: Nguyễn An; ReceiverPhone: +84 90 123 4567; ReceiverEmail: {flow.Email}; Address: 12 Đường Hoa, TP.HCM", o.RootElement.GetProperty("shippingAddress").GetString());

        var replay = await flow.Customer.PostAsync($"/api/custom-orders/{id}/checkout", Json(body));
        replay.EnsureSuccessStatusCode();
        using var replayed = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(orderId, replayed.RootElement.GetProperty("orderId").GetGuid());
    }

    [Fact]
    public async Task CheckoutWithoutReceiverFallsBackToCustomerProfileAndRejectsMalformedPhone()
    {
        var flow = await SetupAsync();
        var id = await RequestAsync(flow);
        (await QuoteAsync(flow.Admin, id)).EnsureSuccessStatusCode();
        (await flow.Customer.PostAsync($"/api/custom-orders/{id}/approve", Json(new { version = 1 }))).EnsureSuccessStatusCode();
        var malformed = await flow.Customer.PostAsync($"/api/custom-orders/{id}/checkout", Json(new { shippingAddress = "12 Hoa", receiverPhone = "not-a-phone", idempotencyKey = "flow-" + Guid.NewGuid().ToString("N") }));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);

        var checkout = await flow.Customer.PostAsync($"/api/custom-orders/{id}/checkout", Json(new { shippingAddress = "12 Hoa", idempotencyKey = "flow-" + Guid.NewGuid().ToString("N") }));
        checkout.EnsureSuccessStatusCode();
        using var created = JsonDocument.Parse(await checkout.Content.ReadAsStringAsync());
        var order = await flow.Customer.GetAsync($"/api/orders/{created.RootElement.GetProperty("orderId").GetGuid()}");
        order.EnsureSuccessStatusCode();
        using var o = JsonDocument.Parse(await order.Content.ReadAsStringAsync());
        Assert.Equal($"ReceiverName: QA Customer; ReceiverPhone: {flow.Phone}; ReceiverEmail: {flow.Email}; Address: 12 Hoa", o.RootElement.GetProperty("shippingAddress").GetString());
    }

    [Fact]
    public async Task RequestListFlagsTrackDepositAndFullPayment()
    {
        var flow = await SetupAsync();
        var (id, orderId) = await CheckedOutAsync(flow, 1);
        var fresh = await RequestAsync(flow, 2, "Chưa báo giá");

        var before = await FindAsync(flow.Customer, id);
        Assert.False(before.GetProperty("depositReady").GetBoolean());
        Assert.False(before.GetProperty("fullyPaid").GetBoolean());
        Assert.False(before.GetProperty("deliveryComplete").GetBoolean());
        Assert.Equal(orderId, before.GetProperty("orderId").GetGuid());
        Assert.False(string.IsNullOrWhiteSpace(before.GetProperty("orderCode").GetString()));
        Assert.Equal("Sản phẩm đặt trước", before.GetProperty("productName").GetString());
        Assert.Equal(1, before.GetProperty("quoteVersion").GetInt32());
        var pending = await FindAsync(flow.Customer, fresh);
        Assert.Equal(JsonValueKind.Null, pending.GetProperty("orderId").ValueKind);
        Assert.False(pending.GetProperty("depositReady").GetBoolean());

        (await flow.Admin.PostAsync($"/api/payments/order/{orderId}", Json(new { transactionCode = "FLOW-DEP-" + Guid.NewGuid().ToString("N"), paymentMethod = "Cash", amount = 100000m }))).EnsureSuccessStatusCode();
        var afterDeposit = await FindAsync(flow.Customer, id);
        Assert.True(afterDeposit.GetProperty("depositReady").GetBoolean());
        Assert.False(afterDeposit.GetProperty("fullyPaid").GetBoolean());
        Assert.False(afterDeposit.GetProperty("deliveryComplete").GetBoolean());

        (await flow.Admin.PostAsync($"/api/payments/order/{orderId}", Json(new { transactionCode = "FLOW-REST-" + Guid.NewGuid().ToString("N"), paymentMethod = "Cash", amount = 100000m }))).EnsureSuccessStatusCode();
        var paid = await FindAsync(flow.Customer, id);
        Assert.True(paid.GetProperty("depositReady").GetBoolean());
        Assert.True(paid.GetProperty("fullyPaid").GetBoolean());
        Assert.False(paid.GetProperty("deliveryComplete").GetBoolean());

        (await flow.Admin.PostAsync($"/api/custom-orders/{id}/production/start", Json(new { }))).EnsureSuccessStatusCode();
        var order = await flow.Customer.GetAsync($"/api/orders/{orderId}");
        order.EnsureSuccessStatusCode();
        using var orderBody = JsonDocument.Parse(await order.Content.ReadAsStringAsync());
        var itemId = orderBody.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid();
        var delivery = await flow.Admin.PostAsync($"/api/orders/{orderId}/deliveries", Json(new { items = new[] { new { orderItemId = itemId, quantity = 1 } } }));
        delivery.EnsureSuccessStatusCode();
        using var deliveryBody = JsonDocument.Parse(await delivery.Content.ReadAsStringAsync());
        var deliveryId = deliveryBody.RootElement.GetProperty("id").GetGuid();
        foreach (var status in new[] { "Shipped", "Delivered" })
            (await flow.Admin.PostAsync($"/api/orders/{orderId}/deliveries/{deliveryId}/status", Json(new { status }))).EnsureSuccessStatusCode();
        Assert.True((await FindAsync(flow.Customer, id)).GetProperty("deliveryComplete").GetBoolean());
    }

    [Fact]
    public async Task CheckoutWithGhnWardComposesTwoLevelAddressAndStoresCodes()
    {
        var flow = await SetupAsync();
        var id = await RequestAsync(flow);
        (await QuoteAsync(flow.Admin, id)).EnsureSuccessStatusCode();
        (await flow.Customer.PostAsync($"/api/custom-orders/{id}/approve", Json(new { version = 1 }))).EnsureSuccessStatusCode();
        var checkout = await flow.Customer.PostAsync($"/api/custom-orders/{id}/checkout", Json(new
        {
            shippingAddress = "12 Đường Hoa",
            provinceId = 1000001,
            wardId = 1003580,
            idempotencyKey = "ghn-" + Guid.NewGuid().ToString("N")
        }));
        checkout.EnsureSuccessStatusCode();
        using var created = JsonDocument.Parse(await checkout.Content.ReadAsStringAsync());
        var orderId = created.RootElement.GetProperty("orderId").GetGuid();

        using var scope = Fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HoaSapDbContext>();
        var order = await db.Orders.SingleAsync(x => x.Id == orderId);
        Assert.Equal(1000001, order.ShippingProvinceId);
        Assert.Equal("Hồ Chí Minh", order.ShippingProvinceName);
        Assert.Equal(1003580, order.ShippingWardId);
        Assert.Equal("Phường Thủ Đức", order.ShippingWardName);
        Assert.Contains("Address: 12 Đường Hoa, Phường Thủ Đức, Hồ Chí Minh", order.ShippingAddress);
    }

    [Fact]
    public async Task RequestStoresGhnDeliveryAreaAndRejectsInvalidWard()
    {
        var flow = await SetupAsync();
        var created = await flow.Customer.PostAsync("/api/custom-orders", Json(new
        {
            productId = flow.Product,
            quantity = 1,
            description = "Hoa giao khu vực Thủ Đức",
            provinceId = 1000001,
            wardId = 1003580
        }));
        created.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("id").GetGuid();
        Assert.Equal(1000001, body.RootElement.GetProperty("deliveryProvinceId").GetInt32());
        Assert.Equal("Hồ Chí Minh", body.RootElement.GetProperty("deliveryProvinceName").GetString());
        Assert.Equal(1003580, body.RootElement.GetProperty("deliveryWardId").GetInt32());
        Assert.Equal("Phường Thủ Đức", body.RootElement.GetProperty("deliveryWardName").GetString());

        using var scope = Fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HoaSapDbContext>();
        var stored = await db.Set<HoaSap.Domain.Entities.CustomOrderRequest>().SingleAsync(x => x.Id == id);
        Assert.Equal(1003580, stored.DeliveryWardId);
        Assert.Equal("Phường Thủ Đức", stored.DeliveryWardName);

        var unknownWard = await flow.Customer.PostAsync("/api/custom-orders", Json(new
        {
            productId = flow.Product, quantity = 1, description = "Phường không tồn tại", provinceId = 1000001, wardId = 999999
        }));
        Assert.Equal(HttpStatusCode.BadRequest, unknownWard.StatusCode);

        var provinceOnly = await flow.Customer.PostAsync("/api/custom-orders", Json(new
        {
            productId = flow.Product, quantity = 1, description = "Thiếu phường", provinceId = 1000001
        }));
        Assert.Equal(HttpStatusCode.BadRequest, provinceOnly.StatusCode);

        var noArea = await flow.Customer.PostAsync("/api/custom-orders", Json(new
        {
            productId = flow.Product, quantity = 1, description = "Không chọn khu vực"
        }));
        noArea.EnsureSuccessStatusCode();
        using var plain = JsonDocument.Parse(await noArea.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, plain.RootElement.GetProperty("deliveryProvinceId").ValueKind);
    }
}
