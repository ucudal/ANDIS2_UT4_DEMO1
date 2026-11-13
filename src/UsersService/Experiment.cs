using System.Diagnostics.Metrics;
using OpenFeature;
using OpenFeature.Model;
using Ucu.Andis.Users.Models;

namespace Ucu.Andis.Users;

/// <summary>
/// Representa el experimento de A/B Testing que trae la versión 2 de Users.
/// </summary>
/// <remarks>
/// Cada User ve su nombre en una de dos variantes, y se cuenta cuántas veces se
/// mostró cada una, para compararlas:
/// <ul>
/// <li>A: <c>Models.User.DisplayName</c> es el nombre.</li>
/// <li>B: <c>Models.User.DisplayName</c> es el nombre y el email.</li>
/// </ul>
///
/// Qué variante ve cada User no lo decide este código: lo decide Unleash, en el
/// flag <c>users-display-name</c>, y se cambia sin desplegar. Users se lo pregunta
/// a OpenFeature, el estándar de feature flags: esta clase no depende de Unleash,
/// solo del proveedor que se registra en <c>Program.cs</c>. El flag decide así -ver
/// <c>experiment_split</c> en <c>demos/common.sh</c>-:
/// <ul>
/// <li>Por segmento: si la solicitud trae el encabezado <c>X-Segment: beta</c>, va
/// a la B. Es la forma de elegir a quién se le muestra, por ejemplo a los que se
/// anotaron para probar lo nuevo.</li>
/// <li>Por porcentaje: si no, según el reparto del flag. Es aleatoria entre los
/// Users, pero el mismo User cae siempre en la misma variante: si cambiara en cada
/// consulta, no se podría comparar el efecto de cada una. Unleash calcula un hash
/// del id del User, así todas las réplicas eligen la misma variante.</li>
/// </ul>
///
/// Si Unleash no responde, Users sigue con la última configuración que leyó. Si
/// nunca la leyó, OpenFeature devuelve la variante A, el valor por omisión.
///
/// La versión 1 no tiene experimento: no completa <c>Models.User.DisplayName</c>.
/// </remarks>
public class Experiment
{
    public const string MeterName = "Ucu.Andis.UsersExperiment";
    public const string Flag = "users-display-name";
    public const string SegmentHeader = "X-Segment";
    public const string VariantHeader = "X-Variant";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Shown = Meter.CreateCounter<long>("users.experiment", "{response}",
        "Respuestas de GetUser por variante del experimento: A o B");

    private readonly IHttpContextAccessor _http;
    private readonly IFeatureClient _flags;

    public Experiment(IHttpContextAccessor http, IFeatureClient flags)
    {
        _http = http;
        _flags = flags;
    }

    /// <summary>
    /// Completa <c>Models.User.DisplayName</c> según la variante del User, y la informa en el
    /// encabezado <c>X-Variant</c> de la respuesta.
    /// </summary>
    public async Task ApplyAsync(User user)
    {
        var context = _http.HttpContext;
        var segment = context?.Request.Headers[SegmentHeader].ToString();

        // El contexto de la evaluación: el id del User, que fija la variante, y
        // el segmento, que usa la restricción del flag.
        var evaluation = EvaluationContext.Builder().SetTargetingKey((user.Id ?? string.Empty).ToString());
        if (!string.IsNullOrEmpty(segment))
            evaluation.Set("segment", segment);
        var variant = await _flags.GetStringValueAsync(Flag, "A", evaluation.Build());

        user.DisplayName = variant == "B" ? $"{user.Name} <{user.Email}>" : user.Name;

        if (context is not null)
            context.Response.Headers.Append(VariantHeader, variant);
        Shown.Add(1, new KeyValuePair<string, object?>("variant", variant),
            new KeyValuePair<string, object?>("segment", string.IsNullOrEmpty(segment) ? "ninguno" : segment));
    }
}
