using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Ucu.Andis.DemoTests.Support;

namespace Ucu.Andis.DemoTests.Security;

// Demo 22: Products entrega una URL firmada para subir la imagen de un Product
// directamente a Azurite. La clave sirve solo para escribir ese blob, y vence.
public class Demo22ValetKeyTests : DemoTest
{
    private static readonly DemoData.Product Product = DemoData.Trousers;

    [Test]
    public async Task TheKeyAllowsOnlyWritingThatImage()
    {
        var url = await UploadUrlAsync();

        Assert.That(await UploadAsync(url), Is.EqualTo(HttpStatusCode.Created));
        using var read = await Api.Http.GetAsync(url);
        Assert.That(read.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden), "la clave de escritura no sirve para leer");
        Assert.That(await UploadAsync(url.Replace($"/{Product.Id}?", $"/{DemoData.Shoes.Id}?")), Is.EqualTo(HttpStatusCode.Forbidden),
            "la clave no sirve para otro Product");
        using var unsigned = await Api.Http.GetAsync(url.Split('?')[0]);
        Assert.That(unsigned.StatusCode, Is.Not.EqualTo(HttpStatusCode.OK), "sin la firma no hay acceso");
    }

    [Test]
    public async Task TheKeyExpires()
    {
        var url = await UploadUrlAsync(seconds: 5);

        Assert.That(await UploadAsync(url), Is.EqualTo(HttpStatusCode.Created));
        await Task.Delay(TimeSpan.FromSeconds(7));
        Assert.That(await UploadAsync(url), Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task DownloadsRedirectToASignedUrl()
    {
        await UploadAsync(await UploadUrlAsync());
        var carol = await Identity.TokenAsync("carol");
        var https = await Identity.HttpsAsync();

        using var response = await https.SendAsync(Identity.Request(HttpMethod.Get,
            $"{DemoData.HttpsGateway}/products/{Product.Id}/image", carol));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        var location = response.Headers.Location!.ToString();
        Assert.That(location, Does.Contain("sig="));
        using var image = await Api.Http.GetAsync(location);
        Assert.That(image.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task WithoutValetKeyTheImageGoesThroughTheGateway()
    {
        var carol = await Identity.TokenAsync("carol");
        var https = await Identity.HttpsAsync();
        var image = new byte[5_000_000];

        using var http = await Api.Http.PutAsync($"{DemoData.Gateway}/products/{Product.Id}/image", ImageContent(image));
        using var request = Identity.Request(HttpMethod.Put, $"{DemoData.HttpsGateway}/products/{Product.Id}/image", carol);
        request.Content = ImageContent(image);
        using var secure = await https.SendAsync(request);

        Assert.That(http.IsSuccessStatusCode, Is.True, "por HTTP, la imagen pasa por el gateway y por Products");
        Assert.That(secure.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge), "por HTTPS, el Gatekeeper no la deja pasar");
    }

    private static async Task<string> UploadUrlAsync(int? seconds = null)
    {
        var carol = await Identity.TokenAsync("carol");
        var https = await Identity.HttpsAsync();
        var query = seconds is null ? "" : $"?seconds={seconds}";
        using var response = await https.SendAsync(Identity.Request(HttpMethod.Post,
            $"{DemoData.HttpsGateway}/products/{Product.Id}/image/upload-url{query}", carol));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!["uploadUrl"]!.GetValue<string>();
    }

    private static async Task<HttpStatusCode> UploadAsync(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = ImageContent(new byte[1000]) };
        request.Headers.Add("x-ms-blob-type", "BlockBlob");
        using var response = await Api.Http.SendAsync(request);
        return response.StatusCode;
    }

    private static ByteArrayContent ImageContent(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new("image/jpeg");
        return content;
    }
}
