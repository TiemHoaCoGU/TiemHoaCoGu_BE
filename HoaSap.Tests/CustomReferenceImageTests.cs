using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Xunit;

namespace HoaSap.Tests;

[Collection("api")]
public sealed class CustomReferenceImageTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    private static MultipartFormDataContent Form(byte[] bytes)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "reference.png");
        return form;
    }

    [Fact]
    public async Task Customer_upload_is_private_and_validates_image_content()
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aP1sAAAAASUVORK5CYII=");
        using (var form = Form(png)) Assert.Equal(HttpStatusCode.Unauthorized, (await Client.PostAsync("/api/custom-orders/reference-images", form)).StatusCode);
        using var owner = await AuthenticatedAsync(await RegisterAsync($"image-owner-{Guid.NewGuid():N}@test.example"));
        using var other = await AuthenticatedAsync(await RegisterAsync($"image-other-{Guid.NewGuid():N}@test.example"));
        using var admin = await AuthenticatedAsync(await AdminTokenAsync());
        using (var bad = Form(Encoding.UTF8.GetBytes("not an image"))) Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync("/api/custom-orders/reference-images", bad)).StatusCode);
        using (var large = Form(new byte[900 * 1024 + 1])) Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync("/api/custom-orders/reference-images", large)).StatusCode);
        using var upload = Form(png);
        var result = await owner.PostAsync("/api/custom-orders/reference-images", upload);
        Assert.Equal(HttpStatusCode.Created, result.StatusCode);
        using var json = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
        var path = json.RootElement.GetProperty("path").GetString()!;
        Assert.Matches(@"^/api/reference-images/[a-f0-9]{32}/[a-f0-9]{32}\.png$", path);
        var apiPath = path.Replace("/api/reference-images/", "/api/custom-orders/reference-images/");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync(apiPath)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(apiPath)).StatusCode);
        var own = await owner.GetAsync(apiPath);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(png, await own.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(apiPath)).StatusCode);
    }
}
