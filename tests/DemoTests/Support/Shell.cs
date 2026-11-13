using System.Diagnostics;
using System.Globalization;

namespace Ucu.Andis.DemoTests.Support;

/// <summary>
/// Proporciona la ejecución de comandos de bash desde la raíz del repositorio.
/// </summary>
/// <remarks>
/// Las operaciones de infraestructura -detener un contenedor, recargar nginx,
/// escalar réplicas- se hacen con las mismas funciones de <c>demos/common.sh</c>
/// que usan los scripts, para que los tests prueben lo mismo que muestran las
/// demos.
/// </remarks>
public static class Shell
{
    public static readonly string RepoRoot = FindRepoRoot();

    public record Result(int ExitCode, string Output, string Error);

    /// <summary>
    /// Ejecuta un script de bash y devuelve su salida, sin fallar si termina con un
    /// código distinto de 0.
    /// </summary>
    /// <remarks>
    /// El test decide qué verificar.
    /// </remarks>
    public static async Task<Result> RunAsync(string script, TimeSpan? timeout = null)
    {
        var info = new ProcessStartInfo("bash")
        {
            WorkingDirectory = RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // Con bash -c, el argumento siguiente es $0. common.sh hace
        // cd "$(dirname "$0")/..": con demos/tests, queda en la raíz.
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(script);
        info.ArgumentList.Add("demos/tests");

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"El comando no terminó a tiempo: {script}");
        }
        return new Result(process.ExitCode, (await output).Trim(), (await error).Trim());
    }

    /// <summary>
    /// Ejecuta funciones de <c>demos/common.sh</c>.
    /// </summary>
    /// <remarks>
    /// Se quita el trap de <c>common.sh</c>, que al salir borra el chaos y los
    /// patrones: los tests los manejan por su cuenta.
    /// </remarks>
    public static Task<Result> DemoAsync(string commands, TimeSpan? timeout = null) =>
        RunAsync($"source demos/common.sh; trap - EXIT; set +e; {commands}", timeout);

    /// <summary>
    /// Ejecuta funciones de <c>demos/common.sh</c> y de <c>k8s/common.sh</c>, para las
    /// demos de Kubernetes.
    /// </summary>
    public static Task<Result> KubernetesAsync(string commands, TimeSpan? timeout = null) =>
        RunAsync($"source demos/common.sh; source k8s/common.sh; trap - EXIT; set +e; {commands}", timeout);

    public static Task<Result> ComposeAsync(string arguments, TimeSpan? timeout = null) =>
        RunAsync($"docker compose {arguments}", timeout);

    /// <summary>
    /// Ejecuta una consulta en la base de Orders, sin encabezados de columnas.
    /// </summary>
    public static async Task<string> OrdersSqlAsync(string query)
    {
        var result = await RunAsync(
            $"docker compose exec -T orders-db mysql -N -B -u ordersusr -pPa55w0rd ordersdb -e \"{query.Replace("\"", "\\\"")}\" 2> /dev/null");
        return result.Output;
    }

    /// <summary>
    /// Devuelve los mensajes en una cola de RabbitMQ, esperando o en proceso, y los
    /// consumidores conectados, con <c>rabbitmqctl</c>, como <c>queue_info</c> en
    /// <c>demos/common.sh</c>.
    /// </summary>
    public static async Task<(int Messages, int Consumers)> QueueInfoAsync(string queue)
    {
        var result = await RunAsync(
            "docker compose exec -T rabbitmq rabbitmqctl list_queues -q --no-table-headers name messages consumers");
        foreach (var line in result.Output.Split('\n'))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && parts[0] == queue)
                return (int.Parse(parts[1], CultureInfo.InvariantCulture), int.Parse(parts[2], CultureInfo.InvariantCulture));
        }
        throw new InvalidOperationException($"No existe la cola {queue}");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docker-compose.yaml")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("No se encontró la raíz del repositorio");
    }
}
