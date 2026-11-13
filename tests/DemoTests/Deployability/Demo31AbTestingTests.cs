using Ucu.Andis.DemoTests.Support;

namespace Ucu.Andis.DemoTests.Deployability;

/// <summary>
/// Contiene las pruebas de la demo 31: Unleash decide qué variante del experimento
/// ve cada User. La variante es fija para cada User, se puede forzar por segmento,
/// y el reparto cambia sin desplegar.
/// </summary>
[Category(TestEnvironment.FeatureFlags)]
public class Demo31AbTestingTests : DemoTest
{
    [SetUp]
    public Task StartExperimentAsync() =>
        Shell.DemoAsync("experiment_start; deploy_target users-service-v2:8000", TimeSpan.FromMinutes(5));

    [TearDown]
    public async Task RestoreAsync()
    {
        await Shell.DemoAsync("deploy_target users-service:8000; experiment_start", TimeSpan.FromMinutes(5));
    }

    [Test]
    public async Task EachUserAlwaysSeesTheSameVariant()
    {
        var variants = new List<string?>();
        for (var i = 0; i < 3; i++)
            variants.Add((await Api.GetUserAsync(DemoData.Deploy, DemoData.User2)).Variant);

        Assert.That(variants, Has.All.Not.Null);
        Assert.That(variants.Distinct(), Has.Exactly(1).Items);
    }

    [Test]
    public async Task TheBetaSegmentSeesVariantB()
    {
        var user = await Api.GetUserAsync(DemoData.Deploy, DemoData.User2, segment: "beta");

        Assert.That(user.Variant, Is.EqualTo("B"));
    }

    [Test]
    public async Task TheSplitChangesWithoutDeploying()
    {
        await Shell.DemoAsync("experiment_split 100");

        var variants = await Eventually.GetAsync(async () => new[]
        {
            (await Api.GetUserAsync(DemoData.Deploy, DemoData.User1)).Variant,
            (await Api.GetUserAsync(DemoData.Deploy, DemoData.User2)).Variant,
        }, v => v.All(x => x == "B"), TimeSpan.FromSeconds(15));

        Assert.That(variants, Has.All.EqualTo("B"));
    }

    [Test]
    [Category(TestEnvironment.Disruptive)]
    public async Task WithoutUnleashUsersKeepsTheLastConfiguration()
    {
        var before = (await Api.GetUserAsync(DemoData.Deploy, DemoData.User2)).Variant;
        await Shell.ComposeAsync("stop unleash");

        var after = (await Api.GetUserAsync(DemoData.Deploy, DemoData.User2)).Variant;

        Assert.That(after, Is.EqualTo(before));
    }
}
