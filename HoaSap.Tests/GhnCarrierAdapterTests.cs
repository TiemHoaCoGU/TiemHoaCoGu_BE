using System.Net;
using System.Text;
using System.Text.Json;
using HoaSap.BLL;
using HoaSap.BLL.Commerce;
using Microsoft.Extensions.Options;
using Xunit;

namespace HoaSap.Tests;

public sealed class GhnCarrierAdapterTests
{
    private sealed record Captured(HttpMethod Method, Uri Uri, string? Body, string? Token, string? ShopId);

    private sealed class StubHandler : HttpMessageHandler
    {
        public List<Captured> Requests { get; } = [];
        public Queue<(HttpStatusCode Status, string Body)> Responses { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Captured(
                request.Method,
                request.RequestUri!,
                body,
                request.Headers.TryGetValues("Token", out var token) ? string.Join(",", token) : null,
                request.Headers.TryGetValues("ShopId", out var shop) ? string.Join(",", shop) : null));
            var (status, payload) = Responses.Count > 0 ? Responses.Dequeue() : (HttpStatusCode.OK, "{\"code\":200,\"message\":\"Success\",\"data\":null}");
            return new HttpResponseMessage(status) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        }
    }

    private static GhnStagingCarrierAdapter CreateAdapter(StubHandler handler, bool emulation = false) =>
        new(new HttpClient(handler), Options.Create(new CarrierOptions
        {
            Provider = "GhnSandbox",
            Origin = "72 Lê Thánh Tôn, Bến Nghé, Quận 1, Hồ Chí Minh",
            Token = "staging-token",
            ShopId = 92837,
            SenderWardName = "Phường Bến Nghé",
            SenderProvinceName = "Hồ Chí Minh",
            SandboxEmulation = emulation
        }), Options.Create(new GhnStagingOptions()));

    private static JsonElement Payload(Captured request) => JsonDocument.Parse(request.Body!).RootElement;

    [Fact]
    public async Task Quote_calls_preview_with_two_level_address_and_reads_total_fee()
    {
        var handler = new StubHandler();
        handler.Responses.Enqueue((HttpStatusCode.OK, "{\"code\":200,\"message\":\"Success\",\"data\":{\"total_fee\":20900}}"));
        var carrier = CreateAdapter(handler);

        var result = await carrier.QuoteAsync(new CarrierRateRequest(
            "72 Lê Thánh Tôn, Bến Nghé, Quận 1, Hồ Chí Minh",
            "42 Test Street, Phường Bến Nghé, Hồ Chí Minh",
            0.6m, 25, 20, 8, 1000001, 1003580,
            ToName: "Tran Minh Anh", ToPhone: "0987654321",
            ToWardName: "Phường Bến Nghé", ToProvinceName: "Hồ Chí Minh",
            Items: [new CarrierShipmentItem("Hoa hồng", "SP-01", 2, 142_500m, 300, 25, 20, 8)]));

        Assert.True(result.Accepted);
        Assert.Equal(20_900m, result.Amount);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://dev-online-gateway.ghn.vn/shiip/public-api/v2/shipping-order/preview", request.Uri.ToString());
        Assert.Equal("staging-token", request.Token);
        Assert.Equal("92837", request.ShopId);
        var body = Payload(request);
        Assert.True(body.GetProperty("is_new_to_address").GetBoolean());
        Assert.Equal("Phường Bến Nghé", body.GetProperty("to_ward_name").GetString());
        Assert.Equal("Hồ Chí Minh", body.GetProperty("to_province_name").GetString());
        Assert.Equal(800, body.GetProperty("weight").GetInt32());
        Assert.Equal(2, body.GetProperty("service_type_id").GetInt32());
        Assert.False(body.TryGetProperty("to_district_id", out _));
        Assert.False(body.TryGetProperty("to_ward_code", out _));
        Assert.Equal("Hoa hồng", body.GetProperty("items")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Quote_fails_loudly_when_ghn_rejects_instead_of_using_local_tariff()
    {
        var handler = new StubHandler();
        handler.Responses.Enqueue((HttpStatusCode.OK, "{\"code\":400,\"message\":\"WARD_NOT_FOUND\",\"data\":null}"));
        var carrier = CreateAdapter(handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => carrier.QuoteAsync(new CarrierRateRequest(
            "72 Lê Thánh Tôn, Bến Nghé, Quận 1, Hồ Chí Minh", "42 Test Street, Phường Bến Nghé, Hồ Chí Minh", 0.6m,
            ToWardName: "Phường Bến Nghé", ToProvinceName: "Hồ Chí Minh")));

        Assert.Contains("WARD_NOT_FOUND", error.Message);
    }

    [Fact]
    public async Task Quote_fails_when_credentials_are_missing()
    {
        var handler = new StubHandler();
        var carrier = new GhnStagingCarrierAdapter(new HttpClient(handler), Options.Create(new CarrierOptions
        {
            Provider = "GhnSandbox",
            Origin = "72 Lê Thánh Tôn, Bến Nghé, Quận 1, Hồ Chí Minh"
        }), Options.Create(new GhnStagingOptions()));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => carrier.QuoteAsync(new CarrierRateRequest(
            "72 Lê Thánh Tôn, Bến Nghé, Quận 1, Hồ Chí Minh", "42 Test Street, Phường Bến Nghé, Hồ Chí Minh", 0.6m,
            ToWardName: "Phường Bến Nghé", ToProvinceName: "Hồ Chí Minh")));

        Assert.Contains("Token", error.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CreateShipment_sends_two_level_address_client_order_code_and_items()
    {
        var handler = new StubHandler();
        handler.Responses.Enqueue((HttpStatusCode.OK, "{\"code\":200,\"message\":\"Success\",\"data\":{\"order_code\":\"LADH94\",\"total_fee\":31500,\"expected_delivery_time\":\"2026-07-16T16:59:59Z\"}}"));
        var carrier = CreateAdapter(handler);

        var result = await carrier.CreateShipmentAsync(new CarrierShipmentRequest(
            "ORD-20261113-ABC", "DEL-abc", "Tran Minh Anh", "0987654321",
            "42 Test Street, Phường Bến Nghé, Hồ Chí Minh", null, null, 0.6m, 25, 20, 8, 285_000m,
            "Giao giờ hành chính",
            [new CarrierShipmentItem("Hoa hồng", "SP-01", 2, 142_500m, 300, 25, 20, 8)],
            "Phường Bến Nghé", "Hồ Chí Minh"));

        Assert.True(result.Accepted);
        Assert.Equal("LADH94", result.TrackingNumber);
        Assert.Equal(31_500m, result.ShippingFee);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://dev-online-gateway.ghn.vn/shiip/public-api/v2/shipping-order/create", request.Uri.ToString());
        var body = Payload(request);
        Assert.Equal("DEL-abc", body.GetProperty("client_order_code").GetString());
        Assert.True(body.GetProperty("is_new_to_address").GetBoolean());
        Assert.Equal("Phường Bến Nghé", body.GetProperty("to_ward_name").GetString());
        Assert.Equal("Hồ Chí Minh", body.GetProperty("to_province_name").GetString());
        Assert.Equal(285_000, body.GetProperty("cod_amount").GetInt32());
        Assert.Equal(600, body.GetProperty("weight").GetInt32());
        Assert.Equal(1, body.GetProperty("payment_type_id").GetInt32());
        Assert.Equal("CHOXEMHANGKHONGTHU", body.GetProperty("required_note").GetString());
        Assert.Equal("Phường Bến Nghé", body.GetProperty("from_ward_name").GetString());
        var item = body.GetProperty("items")[0];
        Assert.Equal("Hoa hồng", item.GetProperty("name").GetString());
        Assert.Equal(2, item.GetProperty("quantity").GetInt32());
        Assert.Equal(300, item.GetProperty("weight").GetInt32());
    }

    [Fact]
    public async Task CreateShipment_requires_two_level_names_for_new_address()
    {
        var handler = new StubHandler();
        var carrier = CreateAdapter(handler);

        var error = await Assert.ThrowsAsync<BusinessException>(() => carrier.CreateShipmentAsync(new CarrierShipmentRequest(
            "ORD-1", "DEL-1", "Receiver", "0987654321", "42 Test Street", null, null, 0.6m, null, null, null, 0, "", [])));

        Assert.Equal(409, error.StatusCode);
        Assert.Contains("phường/tỉnh", error.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CreateShipment_fails_loudly_when_ghn_rejects()
    {
        var handler = new StubHandler();
        handler.Responses.Enqueue((HttpStatusCode.OK, "{\"code\":400,\"message\":\"ORDER_INFO_INVALID\",\"data\":null}"));
        var carrier = CreateAdapter(handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => carrier.CreateShipmentAsync(new CarrierShipmentRequest(
            "ORD-1", "DEL-1", "Receiver", "0987654321", "42 Test Street, Phường Bến Nghé, Hồ Chí Minh", null, null, 0.6m, null, null, null, 0, "",
            [], "Phường Bến Nghé", "Hồ Chí Minh")));

        Assert.Contains("ORDER_INFO_INVALID", error.Message);
    }

    [Fact]
    public async Task Tracking_uses_get_detail_and_reads_updated_date()
    {
        var handler = new StubHandler();
        handler.Responses.Enqueue((HttpStatusCode.OK, "{\"code\":200,\"message\":\"Success\",\"data\":{\"status\":\"delivering\",\"updated_date\":\"2026-07-15T04:49:47.811Z\"}}"));
        var carrier = CreateAdapter(handler);

        var result = await carrier.TrackShipmentAsync("LADH94");

        Assert.True(result.Accepted);
        Assert.Equal("delivering", result.Status);
        Assert.Equal("Đang giao hàng", result.StatusDescription);
        // No log[] in the payload: the panel still gets one event instead of an empty timeline.
        var single = Assert.Single(result.Events!);
        Assert.Equal("delivering", single.Status);
        Assert.Equal("Đang giao hàng", single.Description);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://dev-online-gateway.ghn.vn/shiip/public-api/v2/shipping-order/detail?order_code=LADH94", request.Uri.ToString());
        Assert.Null(request.Body);
    }

    [Fact]
    public async Task Tracking_reads_ghn_log_history_cod_and_leadtime()
    {
        var handler = new StubHandler();
        handler.Responses.Enqueue((HttpStatusCode.OK, """
        {"code":200,"message":"Success","data":{
          "status":"delivered",
          "updated_date":"2026-07-15T09:00:00Z",
          "cod_amount":285000,
          "is_cod_collected":true,
          "cod_collect_date":"2026-07-15T08:55:00Z",
          "leadtime":"2026-07-15T12:00:00Z",
          "log":[
            {"status":"ready_to_pick","updated_date":"2026-07-15T04:49:47Z"},
            {"status":"delivering","updated_date":"2026-07-15T07:30:00Z","content":"Shipper đã lấy hàng"},
            {"status":"delivered","updated_date":"2026-07-15T09:00:00Z"}
          ]}}
        """));
        var carrier = CreateAdapter(handler);

        var result = await carrier.TrackShipmentAsync("LADH94");

        Assert.True(result.Accepted);
        Assert.Equal("delivered", result.Status);
        Assert.Equal(3, result.Events!.Count);
        Assert.Equal("ready_to_pick", result.Events[0].Status);
        Assert.Equal("Mới tạo - Chờ lấy hàng", result.Events[0].Description);
        Assert.Equal("Shipper đã lấy hàng", result.Events[1].Note);
        Assert.Equal("Giao hàng thành công", result.Events[2].Description);
        Assert.True(result.Events[0].OccurredAt < result.Events[2].OccurredAt);
        Assert.Equal(285_000m, result.CodAmount);
        Assert.True(result.CodCollected);
        Assert.Equal(new DateTime(2026, 7, 15, 8, 55, 0, DateTimeKind.Utc), result.CodCollectedAt);
        Assert.Equal(new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc), result.LeadTime);
    }

    [Fact]
    public async Task Cancel_sends_order_code_and_reports_rejection()
    {
        var handler = new StubHandler();
        handler.Responses.Enqueue((HttpStatusCode.OK, "{\"code\":400,\"message\":\"ORDER_NOT_FOUND\",\"data\":null}"));
        var carrier = CreateAdapter(handler);

        var result = await carrier.CancelShipmentAsync("LADH94");

        Assert.False(result.Accepted);
        Assert.Contains("ORDER_NOT_FOUND", result.Message);
        var body = Payload(Assert.Single(handler.Requests));
        Assert.Equal("LADH94", body.GetProperty("order_codes")[0].GetString());
    }

    [Fact]
    public async Task SandboxEmulation_never_calls_http()
    {
        var handler = new StubHandler();
        var carrier = CreateAdapter(handler, emulation: true);

        var quote = await carrier.QuoteAsync(new CarrierRateRequest(
            "72 Lê Thánh Tôn, Bến Nghé, Quận 1, Hồ Chí Minh", "42 Test Street, Phường Bến Nghé, Hồ Chí Minh", 1.5m));
        var shipment = await carrier.CreateShipmentAsync(new CarrierShipmentRequest(
            "ORD-1", "DEL-1", "Receiver", "0987654321", "42 Test Street, Phường Bến Nghé, Hồ Chí Minh", null, null, 1.5m, null, null, null, 0, "", []));

        Assert.Equal(22_000m, quote.Amount);
        Assert.StartsWith("GHN", shipment.TrackingNumber);
        Assert.Empty(handler.Requests);
    }
}
