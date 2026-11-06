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
public record OrderCreated(int OrderId, int UserId, string? ProductId, int Quantity);

// Messaging Bridge: la Order completa, y no solo su id, para que un suscriptor
// externo -el puente hacia el ERP- no tenga que consultar a Orders. Los campos
// nuevos son opcionales: un mensaje anterior sigue siendo válido.
public record OrderConfirmed(int OrderId, int UserId = 0, string? ProductId = null, int Quantity = 0);

public record OrderRejected(int OrderId, string Reason);

// Messaging Bridge: el ERP facturó una Order. Lo publica el puente; Orders no
// sabe que viene de otro broker.
public record OrderInvoiced(int OrderId, string InvoiceNumber, string IssuedOn);

public record UserUpdated(int UserId, string Name);

public record ProductUpdated(string ProductId, string? Name);
