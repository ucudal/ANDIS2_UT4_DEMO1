using System.Net;
using Ucu.Andis.DemoTests.Support;

namespace Ucu.Andis.DemoTests.Modifiability;

// Demo 25: la fachada manda cada ruta al monolito o a un microservicio. Migrar
// una ruta es cambiar una línea; el cliente hace las mismas solicitudes y
// recibe las mismas respuestas.
[Category(TestEnvironment.Disruptive)]
public class Demo25StranglerFigTests : DemoTest
{
    [TearDown]
    public async Task BackToTheMonolithAsync()
    {
        await Api.UpdateProductAsync(DemoData.Shirt.Id, DemoData.Shirt.Name, DemoData.Shirt.Price);
        await Shell.ComposeAsync("up -d --force-recreate --wait monolith");
        await Shell.DemoAsync("strangler_route users monolith:8000; strangler_route products monolith:8000; strangler_route orders monolith:8000");
    }

    [Test]
    public async Task RoutesMigrateOneByOne()
    {
        var before = await ServedAsync();
        Assert.That(before.Select(s => s.ServedBy), Has.All.EqualTo("monolith:8000"));

        await Shell.DemoAsync("strangler_route products products-service:8000");
        var products = await ServedAsync();
        Assert.That(products.Select(s => s.ServedBy),
            Is.EqualTo(new[] { "monolith:8000", "products-service:8000", "monolith:8000" }));

        await Shell.DemoAsync("strangler_route users users-service:8000; strangler_route orders orders-service:8000");
        await Shell.ComposeAsync("stop monolith");
        var after = await ServedAsync();

        Assert.That(after.Select(s => s.ServedBy),
            Is.EqualTo(new[] { "users-service:8000", "products-service:8000", "orders-service:8000" }));
        Assert.That(after.Select(s => s.Status), Has.All.EqualTo(HttpStatusCode.OK), "sin el monolito, todo sigue respondiendo");
        Assert.That(after.Select(s => s.Body), Is.EqualTo(before.Select(s => s.Body)), "el cliente recibe las mismas respuestas");
    }

    [Test]
    public async Task WhileTheyCoexistTheDataIsInTwoPlaces()
    {
        await Shell.DemoAsync("strangler_route products products-service:8000");
        using (await Api.Http.PutAsync($"{DemoData.Facade}/products/{DemoData.Shirt.Id}",
            System.Net.Http.Json.JsonContent.Create(new { name = "Camisa nueva", price = DemoData.Shirt.Price }))) { }

        var product = await Api.Http.GetStringAsync($"{DemoData.Facade}/products/{DemoData.Shirt.Id}");
        var details = await Api.Http.GetStringAsync($"{DemoData.Facade}/orders/user/{DemoData.User1}/details");

        Assert.That(product, Does.Contain("Camisa nueva"));
        Assert.That(details, Does.Not.Contain("Camisa nueva"), "el JOIN del monolito sigue viendo el nombre viejo");
    }

    private static async Task<List<(HttpStatusCode Status, string? ServedBy, string Body)>> ServedAsync()
    {
        var results = new List<(HttpStatusCode, string?, string)>();
        using var users = Api.SoapRequest($"{DemoData.Facade}/users/UserService.svc", "GetUser", $"<id>{DemoData.User1}</id>");
        foreach (var request in new[]
        {
            users,
            new HttpRequestMessage(HttpMethod.Get, $"{DemoData.Facade}/products/{DemoData.Shirt.Id}"),
            new HttpRequestMessage(HttpMethod.Get, $"{DemoData.Facade}/orders/user/{DemoData.User1}/details"),
        })
        {
            using var response = await Api.Http.SendAsync(request);
            results.Add((response.StatusCode, Api.Header(response, "X-Served-By"), Summary(await response.Content.ReadAsStringAsync())));
        }
        return results;
    }

    // Los nombres de la respuesta, en JSON o en XML: el monolito y los servicios
    // no tienen por qué formatear igual el resto.
    private static string Summary(string body) =>
        string.Join(", ", System.Text.RegularExpressions.Regex.Matches(body, @"[Nn]ame(?:""?:""|>)([^"",<]*)").Select(m => m.Groups[1].Value));
}
