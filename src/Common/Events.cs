namespace Ucu.Andis.Common;

// Publisher/Subscriber: los servicios publican eventos en el exchange
// andis.events, de tipo topic. Cada suscriptor tiene su propia cola y la enlaza
// con las claves de ruteo que le interesan; el publicador no sabe quiénes son
// los suscriptores ni cuántos hay.
public static class Events
{
    public const string Exchange = "andis.events";

    public const string OrderCreated = "order.created";
    public const string OrderConfirmed = "order.confirmed";
    public const string OrderRejected = "order.rejected";
    public const string OrderInvoiced = "order.invoiced";
    public const string UserUpdated = "user.updated";
    public const string ProductUpdated = "product.updated";
}

// Contratos de los eventos que viajan entre servicios. Son distintos de los
// eventos de Event Sourcing de Orders -ver OrderEvent.cs-: esos son internos de
// Orders y pueden cambiar; estos son públicos y otros servicios dependen de
// ellos.
public record OrderCreated(int OrderId, string UserId, int ProductId, int Quantity);

// Messaging Bridge: la Order completa, y no solo su id, para que un suscriptor
// externo -el puente hacia el ERP- no tenga que consultar a Orders. Los campos
// nuevos son opcionales: un mensaje anterior sigue siendo válido.
public record OrderConfirmed(int OrderId, string? UserId = null, int ProductId = 0, int Quantity = 0);

public record OrderRejected(int OrderId, string Reason);

// Messaging Bridge: el ERP facturó una Order. Lo publica el puente; Orders no
// sabe que viene de otro broker.
public record OrderInvoiced(int OrderId, string InvoiceNumber, string IssuedOn);

// Materialized View: OccurredAt es el momento del cambio, según el reloj del
// servicio que lo publica. Con varios proyectores, dos cambios del
// mismo nombre pueden aplicarse en otro orden; la fecha permite descartar el
// más viejo -ver OrderDetailsProjector.cs-. Es opcional, como los campos nuevos
// de OrderConfirmed: un mensaje anterior sigue siendo válido.
public record UserUpdated(string UserId, string Name, DateTime? OccurredAt = null);

public record ProductUpdated(int ProductId, string? Name, DateTime? OccurredAt = null);
