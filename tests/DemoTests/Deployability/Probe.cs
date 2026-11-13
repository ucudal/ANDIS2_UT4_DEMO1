using Ucu.Andis.DemoTests.Support;

namespace Ucu.Andis.DemoTests.Deployability;

/// <summary>
/// Representa una sonda que pide el User de alice cada 0,1 segundos mientras dura
/// un cambio, y anota cada respuesta, como <c>deploy_probe</c> en
/// <c>demos/common.sh</c>.
/// </summary>
/// <remarks>
/// Sirve para verificar que un despliegue no corta el servicio.
/// </remarks>
public sealed class Probe : IAsyncDisposable
{
    private readonly CancellationTokenSource _running = new();
    private readonly Task<List<Api.UserResponse>> _task;

    private Probe(string baseUrl) => _task = RunAsync(baseUrl, _running.Token);

    public static async Task<Probe> StartAsync(string baseUrl)
    {
        var probe = new Probe(baseUrl);
        await Task.Delay(TimeSpan.FromSeconds(2));
        return probe;
    }

    /// <summary>
    /// Detiene la sonda y devuelve las respuestas.
    /// </summary>
    public async Task<List<Api.UserResponse>> StopAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(2));
        _running.Cancel();
        return await _task;
    }

    /// <summary>
    /// Devuelve las respuestas que no fueron 200, con su código y el comienzo del
    /// cuerpo o del error.
    /// </summary>
    /// <remarks>
    /// Si un test falla, el mensaje muestra qué pasó.
    /// </remarks>
    public static List<string> Failures(IEnumerable<Api.UserResponse> responses) =>
        responses.Where(r => r.Status != System.Net.HttpStatusCode.OK)
            .Select(r => $"{(int)r.Status} v{r.Version}: {r.Body[..Math.Min(r.Body.Length, 120)]}")
            .ToList();

    public async ValueTask DisposeAsync()
    {
        _running.Cancel();
        try
        {
            await _task;
        }
        catch (OperationCanceledException)
        {
        }
        _running.Dispose();
    }

    private static async Task<List<Api.UserResponse>> RunAsync(string baseUrl, CancellationToken token)
    {
        var responses = new List<Api.UserResponse>();
        while (!token.IsCancellationRequested)
        {
            responses.Add(await Api.GetUserAsync(baseUrl, DemoData.User1));
            // Sin el token: al detenerse, la sonda termina la vuelta y devuelve lo
            // que juntó, en lugar de cortar con una excepción.
            await Task.Delay(TimeSpan.FromMilliseconds(100), CancellationToken.None);
        }
        return responses;
    }
}
