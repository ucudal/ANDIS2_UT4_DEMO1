namespace Ucu.Andis.DemoTests.Support;

// Lo asíncrono -un worker que procesa, un proyector que actualiza la vista- no
// pasa enseguida. Eventually repite una consulta hasta que su resultado cumple
// la condición o vence el plazo, y devuelve el último resultado: el test lo
// verifica con Assert.That, y si vence el plazo el mensaje muestra lo último
// que vio.
public static class Eventually
{
    public static async Task<T> GetAsync<T>(Func<Task<T>> probe, Func<T, bool> done,
        TimeSpan? timeout = null, TimeSpan? interval = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));
        while (true)
        {
            T value;
            try
            {
                value = await probe();
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(interval ?? TimeSpan.FromSeconds(1));
                continue;
            }
            if (done(value) || DateTime.UtcNow >= deadline)
                return value;
            await Task.Delay(interval ?? TimeSpan.FromSeconds(1));
        }
    }
}
