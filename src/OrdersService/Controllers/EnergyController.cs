using Microsoft.AspNetCore.Mvc;

namespace Ucu.Andis.OrdersService.Controllers;

/// <summary>
/// Representa el controlador de la ventana en que el worker procesa la cola
/// <c>orders.process</c>.
/// </summary>
/// <remarks>
/// Queue Non-Urgent Processing Requests -ver la demo 37-. Sin ventana, el worker
/// procesaría cada Order en cuanto llega, aunque la energía de ese momento sea la
/// más sucia o la más cara; con ella, el trabajo que puede esperar se hace cuando
/// conviene.
///
/// La ventana es la clave <c>Energy:WindowOpen</c> del almacén de configuración: en
/// producción la cambiaría un pronóstico de intensidad de carbono. KEDA consulta
/// este endpoint y pone el worker en tantas réplicas como indica <c>replicas</c>: 0
/// con la ventana cerrada. Está en <c>orders-service</c> y no en el worker porque,
/// con la ventana cerrada, el worker no tiene réplicas que respondan.
/// </remarks>
[ApiController]
[Route("energy")]
public class EnergyController : ControllerBase
{
    private readonly IConfiguration _config;

    public EnergyController(IConfiguration config) => _config = config;

    /// <summary>
    /// Devuelve el estado de la ventana, en <c>GET /energy/window</c>.
    /// </summary>
    /// <remarks>
    /// Lee la configuración en cada consulta: un cambio en el almacén llega a KEDA en
    /// unos 2 segundos, sin reiniciar <c>orders-service</c>.
    /// </remarks>
    [HttpGet("window")]
    public IActionResult GetWindow()
    {
        var open = _config.GetValue<bool>("Energy:WindowOpen");
        var replicas = open ? _config.GetValue("Energy:WindowReplicas", 3) : 0;
        return Ok(new { open, replicas });
    }
}
