using System.Net;
using Ucu.Andis.DemoTests.Support;

namespace Ucu.Andis.DemoTests.Deployability;

// Demo 30: una fracción de las solicitudes va a la versión v2. Si la v2 falla,
// el error lo ve solo esa fracción, y volver atrás es mandar todo a la v1.
public class Demo30CanaryTests : DemoTest
{
    [TearDown]
    public Task BackToV1Async() => Shell.DemoAsync("v2_reset_chaos; deploy_target users-service:8000");

    [Test]
    public async Task AFractionGoesToTheNewVersion()
    {
        await Shell.DemoAsync("deploy_canary 10");
        var ten = await Api.VersionsAsync(DemoData.Deploy, 50);
        await Shell.DemoAsync("deploy_canary 100");
        var all = await Api.VersionsAsync(DemoData.Deploy, 20);

        Assert.That(ten.Count(r => r.Version == "2"), Is.InRange(1, 20), "cerca del 10% a la versión v2");
        Assert.That(all.Select(r => r.Version), Has.All.EqualTo("2"));
    }

    [Test]
    public async Task OnlyTheCanarySeesTheError()
    {
        await Shell.DemoAsync("deploy_canary 10; v2_chaos 1 0");
        var responses = await Api.VersionsAsync(DemoData.Deploy, 50);

        var failed = responses.Count(r => r.Status != HttpStatusCode.OK);
        Assert.That(failed, Is.InRange(1, 20), "fallan solo las solicitudes que fueron a la versión v2");

        await Shell.DemoAsync("deploy_target users-service:8000");
        var back = await Api.VersionsAsync(DemoData.Deploy, 20);
        Assert.That(Probe.Failures(back), Is.Empty);
        Assert.That(back.Select(r => r.Version), Has.All.EqualTo("1"));
    }
}
