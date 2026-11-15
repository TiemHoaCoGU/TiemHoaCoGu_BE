using System.Net;
using System.Text.Json;
using Xunit;

namespace HoaSap.Tests;

[Collection("api")]
public sealed class ProductionRefundTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task OrderRefund_EndToEnd_IsProcessableAndSupportsSecondPartialRequest()
    {
        var admin = await AuthenticatedAsync(await AdminTokenAsync());
        var category = await admin.PostAsync("/api/management/categories", Json(new { code = "RF-" + Guid.NewGuid().ToString("N")[..8], name = "Refund QA" }));
        category.EnsureSuccessStatusCode();
        using var categoryJson = JsonDocument.Parse(await category.Content.ReadAsStringAsync());
        var categoryId = categoryJson.RootElement.GetProperty("id").GetGuid();
        // Synthetic measurements are only for this isolated fake-carrier test.
        var product = await admin.PostAsync("/api/management/products", Json(new { code = "RFP-" + Guid.NewGuid().ToString("N")[..8], name = "Refund Flower", categoryId, price = 100000m, shippingWeightGrams = 500, packageLengthCm = 30m, packageWidthCm = 20m, packageHeightCm = 10m, packagingSpecsConfirmed = true, isActive = true }));
        product.EnsureSuccessStatusCode();
        using var productJson = JsonDocument.Parse(await product.Content.ReadAsStringAsync());
        var productId = productJson.RootElement.GetProperty("id").GetGuid();
        (await admin.PutAsync($"/api/management/inventory/{productId}?quantity=1", Json(new { }))).EnsureSuccessStatusCode();

        using var customer = await AuthenticatedAsync(await RegisterAsync($"refund-e2e-{Guid.NewGuid():N}@example.test"));
        var quote = await ShippingQuoteAsync(customer, "QA", (productId, 1));
        var created = await customer.PostAsync("/api/orders/checkout", Json(new { shippingAddress = quote.Destination, shippingQuoteId = quote.Id, items = new[] { new { productId, quantity = 1 } } }));
        created.EnsureSuccessStatusCode();
        using var orderJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var orderId = orderJson.RootElement.GetProperty("id").GetGuid();
        (await admin.PostAsync($"/api/payments/order/{orderId}", Json(new { transactionCode = "RF-PAY-" + Guid.NewGuid().ToString("N"), paymentMethod = "Cash", amount = 142000m }))).EnsureSuccessStatusCode();

        var first = await admin.PostAsync($"/api/refunds/order/{orderId}", Json(new { amount = 40000m, reason = "Partial QA" }));
        first.EnsureSuccessStatusCode();
        using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var firstId = firstJson.RootElement.GetProperty("id").GetGuid();
        (await admin.PostAsync($"/api/refunds/{firstId}/approve", Json(new { }))).EnsureSuccessStatusCode();
        var processed = await admin.PostAsync($"/api/refunds/{firstId}/process", Json(new { transactionCode = "RF-REF-1" }));
        processed.EnsureSuccessStatusCode();
        var replay = await admin.PostAsync($"/api/refunds/{firstId}/process", Json(new { transactionCode = "RF-REF-1" }));
        replay.EnsureSuccessStatusCode();

        var second = await admin.PostAsync($"/api/refunds/order/{orderId}", Json(new { amount = 102000m, reason = "Remaining QA" }));
        second.EnsureSuccessStatusCode();
        using var secondJson = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        var secondId = secondJson.RootElement.GetProperty("id").GetGuid();
        (await admin.PostAsync($"/api/refunds/{secondId}/approve", Json(new { }))).EnsureSuccessStatusCode();
        var secondProcessed = await admin.PostAsync($"/api/refunds/{secondId}/process", Json(new { transactionCode = "RF-REF-2" }));
        secondProcessed.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task RefundList_RequiresStaffAuthorization()
    {
        var response = await Client.GetAsync("/api/refunds?page=1&pageSize=200");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RefundList_IsBoundedAndDoesNotExposePaymentSecrets()
    {
        var admin = await AuthenticatedAsync(await AdminTokenAsync());
        var response = await admin.GetAsync("/api/refunds?page=1&pageSize=1000");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, json.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(100, json.RootElement.GetProperty("pageSize").GetInt32());
        Assert.False(json.RootElement.ToString().Contains("PaymentGatewayResponse", StringComparison.OrdinalIgnoreCase));
        Assert.False(json.RootElement.ToString().Contains("PasswordHash", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RefundDecisions_AreAdminOnly()
    {
        var staffToken = await RegisterAsync($"refund-staff-{Guid.NewGuid():N}@example.test", "096" + Random.Shared.Next(1000000, 9999999));
        var staff = await AuthenticatedAsync(staffToken);
        var response = await staff.PostAsync($"/api/refunds/{Guid.NewGuid()}/approve", Json(new { }));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CustomerRequestRefund_Succeeds_WhenPreDelivery()
    {
        var admin = await AuthenticatedAsync(await AdminTokenAsync());
        var category = await admin.PostAsync("/api/management/categories", Json(new { code = "CRF-" + Guid.NewGuid().ToString("N")[..8], name = "Customer Refund Category" }));
        category.EnsureSuccessStatusCode();
        using var categoryJson = JsonDocument.Parse(await category.Content.ReadAsStringAsync());
        var categoryId = categoryJson.RootElement.GetProperty("id").GetGuid();

        var product = await admin.PostAsync("/api/management/products", Json(new { code = "CRFP-" + Guid.NewGuid().ToString("N")[..8], name = "Customer Refund Flower", categoryId, price = 100000m, shippingWeightGrams = 500, packageLengthCm = 30m, packageWidthCm = 20m, packageHeightCm = 10m, packagingSpecsConfirmed = true, isActive = true }));
        product.EnsureSuccessStatusCode();
        using var productJson = JsonDocument.Parse(await product.Content.ReadAsStringAsync());
        var productId = productJson.RootElement.GetProperty("id").GetGuid();
        (await admin.PutAsync($"/api/management/inventory/{productId}?quantity=5", Json(new { }))).EnsureSuccessStatusCode();

        using var customer = await AuthenticatedAsync(await RegisterAsync($"cust-refund-{Guid.NewGuid():N}@example.test"));
        var quote = await ShippingQuoteAsync(customer, "QA 123", (productId, 1));
        var created = await customer.PostAsync("/api/orders/checkout", Json(new { shippingAddress = quote.Destination, shippingQuoteId = quote.Id, items = new[] { new { productId, quantity = 1 } } }));
        created.EnsureSuccessStatusCode();
        using var orderJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var orderId = orderJson.RootElement.GetProperty("id").GetGuid();

        // Customer pays for order
        (await admin.PostAsync($"/api/payments/order/{orderId}", Json(new { transactionCode = "CRF-PAY-" + Guid.NewGuid().ToString("N"), paymentMethod = "Cash", amount = 142000m }))).EnsureSuccessStatusCode();

        // Customer requests refund before delivery
        var refundResp = await customer.PostAsync($"/api/orders/{orderId}/refund-request", Json(new { reason = "Khách hàng muốn đổi địa chỉ và đặt lại đơn mới" }));
        refundResp.EnsureSuccessStatusCode();
        using var refundDoc = JsonDocument.Parse(await refundResp.Content.ReadAsStringAsync());
        Assert.Equal("Requested", refundDoc.RootElement.GetProperty("refundStatus").GetString());
        Assert.Equal(142000m, refundDoc.RootElement.GetProperty("refundAmount").GetDecimal());

        // Verify order status transitioned to Cancelled
        var getOrder = await customer.GetAsync($"/api/orders/{orderId}");
        getOrder.EnsureSuccessStatusCode();
        using var getOrderDoc = JsonDocument.Parse(await getOrder.Content.ReadAsStringAsync());
        Assert.Equal("Cancelled", getOrderDoc.RootElement.GetProperty("orderStatus").GetString());

        // Verify customer can list their refunds for this order
        var listResp = await customer.GetAsync($"/api/orders/{orderId}/refunds");
        listResp.EnsureSuccessStatusCode();
        using var listDoc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, listDoc.RootElement.ValueKind);
        Assert.Single(listDoc.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task CustomerRequestRefund_Blocks_WhenDeliveryInTransit()
    {
        var admin = await AuthenticatedAsync(await AdminTokenAsync());
        var category = await admin.PostAsync("/api/management/categories", Json(new { code = "CRFB-" + Guid.NewGuid().ToString("N")[..8], name = "Delivery Barrier Category" }));
        category.EnsureSuccessStatusCode();
        using var categoryJson = JsonDocument.Parse(await category.Content.ReadAsStringAsync());
        var categoryId = categoryJson.RootElement.GetProperty("id").GetGuid();

        var product = await admin.PostAsync("/api/management/products", Json(new { code = "CRFBP-" + Guid.NewGuid().ToString("N")[..8], name = "Delivery Barrier Flower", categoryId, price = 80000m, shippingWeightGrams = 400, packageLengthCm = 25m, packageWidthCm = 15m, packageHeightCm = 10m, packagingSpecsConfirmed = true, isActive = true }));
        product.EnsureSuccessStatusCode();
        using var productJson = JsonDocument.Parse(await product.Content.ReadAsStringAsync());
        var productId = productJson.RootElement.GetProperty("id").GetGuid();
        (await admin.PutAsync($"/api/management/inventory/{productId}?quantity=5", Json(new { }))).EnsureSuccessStatusCode();

        using var customer = await AuthenticatedAsync(await RegisterAsync($"cust-barrier-{Guid.NewGuid():N}@example.test"));
        var quote = await ShippingQuoteAsync(customer, "QA 456", (productId, 1));
        var created = await customer.PostAsync("/api/orders/checkout", Json(new { shippingAddress = quote.Destination, shippingQuoteId = quote.Id, items = new[] { new { productId, quantity = 1 } } }));
        created.EnsureSuccessStatusCode();
        using var orderJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var orderId = orderJson.RootElement.GetProperty("id").GetGuid();
        var orderItemId = orderJson.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid();

        // Customer pays
        (await admin.PostAsync($"/api/payments/order/{orderId}", Json(new { transactionCode = "BAR-PAY-" + Guid.NewGuid().ToString("N"), paymentMethod = "Cash", amount = 122000m }))).EnsureSuccessStatusCode();

        // Staff moves order to Processing and dispatches delivery
        (await admin.PostAsync($"/api/orders/{orderId}/status", Json(new { status = "Processing" }))).EnsureSuccessStatusCode();
        var deliveryResp = await admin.PostAsync($"/api/orders/{orderId}/deliveries", Json(new { items = new[] { new { orderItemId, quantity = 1 } } }));
        deliveryResp.EnsureSuccessStatusCode();
        using var deliveryDoc = JsonDocument.Parse(await deliveryResp.Content.ReadAsStringAsync());
        var deliveryId = deliveryDoc.RootElement.GetProperty("id").GetGuid();

        // Mark delivery as Shipped (in transit with carrier)
        (await admin.PostAsync($"/api/orders/{orderId}/deliveries/{deliveryId}/status", Json(new { status = "Shipped" }))).EnsureSuccessStatusCode();

        // Customer tries to request refund -> must be blocked with Conflict (409)
        var blockedResp = await customer.PostAsync($"/api/orders/{orderId}/refund-request", Json(new { reason = "Muốn hủy ngang khi đang giao" }));
        Assert.Equal(HttpStatusCode.Conflict, blockedResp.StatusCode);
    }

    [Fact]
    public async Task CustomerRequestRefund_Blocks_WhenNotOwner()
    {
        var admin = await AuthenticatedAsync(await AdminTokenAsync());
        var category = await admin.PostAsync("/api/management/categories", Json(new { code = "CRFO-" + Guid.NewGuid().ToString("N")[..8], name = "Owner Check Category" }));
        category.EnsureSuccessStatusCode();
        using var categoryJson = JsonDocument.Parse(await category.Content.ReadAsStringAsync());
        var categoryId = categoryJson.RootElement.GetProperty("id").GetGuid();

        var product = await admin.PostAsync("/api/management/products", Json(new { code = "CRFOP-" + Guid.NewGuid().ToString("N")[..8], name = "Owner Check Flower", categoryId, price = 50000m, shippingWeightGrams = 300, packageLengthCm = 20m, packageWidthCm = 10m, packageHeightCm = 10m, packagingSpecsConfirmed = true, isActive = true }));
        product.EnsureSuccessStatusCode();
        using var productJson = JsonDocument.Parse(await product.Content.ReadAsStringAsync());
        var productId = productJson.RootElement.GetProperty("id").GetGuid();
        (await admin.PutAsync($"/api/management/inventory/{productId}?quantity=5", Json(new { }))).EnsureSuccessStatusCode();

        using var customer1 = await AuthenticatedAsync(await RegisterAsync($"cust-owner1-{Guid.NewGuid():N}@example.test"));
        var quote = await ShippingQuoteAsync(customer1, "QA 789", (productId, 1));
        var created = await customer1.PostAsync("/api/orders/checkout", Json(new { shippingAddress = quote.Destination, shippingQuoteId = quote.Id, items = new[] { new { productId, quantity = 1 } } }));
        created.EnsureSuccessStatusCode();
        using var orderJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var orderId = orderJson.RootElement.GetProperty("id").GetGuid();

        (await admin.PostAsync($"/api/payments/order/{orderId}", Json(new { transactionCode = "OWN-PAY-" + Guid.NewGuid().ToString("N"), paymentMethod = "Cash", amount = 92000m }))).EnsureSuccessStatusCode();

        // Customer 2 tries to request refund on customer 1's order
        using var customer2 = await AuthenticatedAsync(await RegisterAsync($"cust-owner2-{Guid.NewGuid():N}@example.test"));
        var hackerResp = await customer2.PostAsync($"/api/orders/{orderId}/refund-request", Json(new { reason = "Giả mạo yêu cầu hoàn tiền" }));
        Assert.Equal(HttpStatusCode.NotFound, hackerResp.StatusCode);
    }
}
