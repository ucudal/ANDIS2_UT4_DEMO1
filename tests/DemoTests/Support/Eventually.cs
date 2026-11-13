namespace Ucu.Andis.DemoTests.Support;

/// <summary>
/// Proporciona la espera de lo asíncrono: repite una consulta hasta que su
/// resultado cumple la condición o vence el plazo.
/// </summary>
/// <remarks>
/// Lo asíncrono -un worker que procesa, un proyector que actualiza la vista- no pasa
/// enseguida. <c>Eventually</c> devuelve el último resultado: el test lo verifica
/// con <c>Assert.That</c>, y si vence el plazo el mensaje muestra lo último que vio.
/// </remarks>
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
