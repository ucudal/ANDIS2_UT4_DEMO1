using Microsoft.AspNetCore.Mvc;

namespace Ucu.Andis.OrdersService.Controllers;

// Queue Non-Urgent Processing Requests: la ventana en que el worker procesa la
// cola orders.process -ver la demo 37-. Sin ventana, el worker procesaría cada
// Order en cuanto llega, aunque la energía de ese momento sea la más sucia o la
// más cara; con ella, el trabajo que puede esperar se hace cuando conviene.
//
// La ventana es la clave Energy:WindowOpen del almacén de configuración: en
// producción la cambiaría un pronóstico de intensidad de carbono. KEDA consulta
// este endpoint y pone el worker en tantas réplicas como indica replicas: 0 con
// la ventana cerrada. Está en orders-service y no en el worker porque, con la
// ventana cerrada, el worker no tiene réplicas que respondan.
[ApiController]
[Route("energy")]
public class EnergyController : ControllerBase
{
    private readonly IConfiguration _config;

    public EnergyController(IConfiguration config) => _config = config;

    // GET /energy/window
    // Lee la configuración en cada consulta: un cambio en el almacén llega a
    // KEDA en unos 2 segundos, sin reiniciar orders-service.
    [HttpGet("window")]
    public IActionResult GetWindow()
    {
        var open = _config.GetValue<bool>("Energy:WindowOpen");
        var replicas = open ? _config.GetValue("Energy:WindowReplicas", 3) : 0;
        return Ok(new { open, replicas });
    }
}
