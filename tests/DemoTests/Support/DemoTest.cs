using System.Net;

namespace Ucu.Andis.DemoTests.Support;

// La base de los tests de las demos que corren con Docker Compose. Antes del
// primer test verifica que la aplicación esté levantada, y al terminar cada
// test deja los servicios sin fallas simuladas y con todos los patrones
// prendidos, como el trap de demos/common.sh.
//
// NUnit corre el TearDown de la clase derivada antes que este: si una demo
// restaura datos llamando a un servicio con chaos, tiene que llamar antes a
// Api.ResetChaosAsync.
public abstract class DemoTest
{
    private static bool _checked;

    [OneTimeSetUp]
    public async Task CheckServicesAsync()
    {
        if (_checked)
            return;
        // Se espera un poco: un contenedor que se está recreando tarda en volver.
        foreach (var service in new[] { "users", "products", "orders" })
        {
            var status = await Eventually.GetAsync(async () =>
            {
                using var response = await Api.Http.GetAsync($"{DemoData.Gateway}/health/{service}");
                return response.StatusCode;
            }, s => s == HttpStatusCode.OK, TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(3));
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK),
                $"/health/{service} no responde 200. Hay que levantar la aplicación con docker compose up -d --build --wait");
        }
        _checked = true;
    }

    [TearDown]
    public async Task ResetAsync()
    {
        await Api.ResetChaosAsync();
        await Api.ResetPatternsAsync();
    }
}
