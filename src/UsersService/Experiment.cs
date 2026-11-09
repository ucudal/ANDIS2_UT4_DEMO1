using System.Diagnostics.Metrics;
using UsersService.Models;

namespace Ucu.Andis.Users;

// A/B Testing: la versión 2 de Users trae un experimento. Cada User ve su
// nombre en una de dos variantes, y se cuenta cuántas veces se mostró cada una,
// para compararlas:
//
// - A: DisplayName es el nombre.
// - B: DisplayName es el nombre y el email.
//
// La variante se decide por segmento o al azar:
//
// - Por segmento: si la solicitud trae el encabezado X-Segment: beta, va a la B.
//   Es la forma de elegir a quién se le muestra, por ejemplo a los que se
//   anotaron para probar lo nuevo.
// - Al azar: si no, la decide el id del User. Es aleatoria entre los Users,
//   pero el mismo User cae siempre en la misma variante: si cambiara en cada
//   consulta, no se podría comparar el efecto de cada una.
//
// La versión 1 no tiene experimento: no completa DisplayName.
public class Experiment
{
    public const string MeterName = "Ucu.Andis.UsersExperiment";
    public const string SegmentHeader = "X-Segment";
    public const string VariantHeader = "X-Variant";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Shown = Meter.CreateCounter<long>("users.experiment", "{response}",
        "Respuestas de GetUser por variante del experimento: A o B");

    private readonly IHttpContextAccessor _http;

    public Experiment(IHttpContextAccessor http)
    {
        _http = http;
    }

    // Completa DisplayName según la variante del User, y la informa en el
    // encabezado X-Variant de la respuesta.
    public void Apply(User user)
    {
        var context = _http.HttpContext;
        var segment = context?.Request.Headers[SegmentHeader].ToString();
        var variant = segment == "beta" ? "B" : VariantFor(user.Id);

        user.DisplayName = variant == "A" ? user.Name : $"{user.Name} <{user.Email}>";

        context?.Response.Headers.Append(VariantHeader, variant);
        Shown.Add(1, new KeyValuePair<string, object?>("variant", variant),
            new KeyValuePair<string, object?>("segment", string.IsNullOrEmpty(segment) ? "ninguno" : segment));
    }

    // Un hash fijo del id, el multiplicativo de Knuth, del que se toma el bit
    // más alto. No se usa GetHashCode, que cambia en cada proceso: dos réplicas
    // tienen que elegir la misma variante para el mismo User.
    private static string VariantFor(int userId) =>
        unchecked((uint)userId * 2654435761u) >> 31 == 0 ? "A" : "B";
}
