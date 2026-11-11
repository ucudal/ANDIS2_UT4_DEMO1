using System.Net;
using Ucu.Andis.DemoTests.Support;

namespace Ucu.Andis.DemoTests.Deployability;

// Demo 27: la versión v2 está desplegada al lado de la v1, sin tráfico.
// Conmutar es cambiar el destino del gateway, sin errores, y volver atrás es
// conmutar de nuevo.
public class Demo27BlueGreenTests : DemoTest
{
    [TearDown]
    public Task BackToV1Async() => Shell.DemoAsync("deploy_target users-service:8000");

    [Test]
    public async Task SwitchingHasNoErrors()
    {
        Assert.That((await Api.VersionsAsync(DemoData.Deploy, 5)).Select(r => r.Version), Has.All.EqualTo("1"));
        var inside = await Shell.DemoAsync("get_user_inside users-service-v2 1 -i | grep -i '^x-version'");
        Assert.That(inside.Output, Does.Contain("2"), "la versión v2 se prueba antes de recibir tráfico");

        var toV2 = await SwitchAsync("users-service-v2:8000");
        Assert.That(Probe.Failures(toV2), Is.Empty);
        Assert.That((await Api.VersionsAsync(DemoData.Deploy, 5)).Select(r => r.Version), Has.All.EqualTo("2"));

        var back = await SwitchAsync("users-service:8000");
        Assert.That(Probe.Failures(back), Is.Empty);
        Assert.That((await Api.VersionsAsync(DemoData.Deploy, 5)).Select(r => r.Version), Has.All.EqualTo("1"));
    }

    private static async Task<List<Api.UserResponse>> SwitchAsync(string target)
    {
        await using var probe = await Probe.StartAsync(DemoData.Deploy);
        await Shell.DemoAsync($"deploy_target {target}");
        return await probe.StopAsync();
    }
}
