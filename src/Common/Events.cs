namespace Ucu.Andis.Common;

/// <summary>
/// Proporciona el exchange y las claves de ruteo de los eventos de
/// Publisher/Subscriber.
/// </summary>
/// <remarks>
/// Los servicios publican eventos en el exchange <c>andis.events</c>, de tipo
/// topic. Cada suscriptor tiene su propia cola y la enlaza con las claves de
/// ruteo que le interesan; el publicador no sabe quiénes son los suscriptores
/// ni cuántos hay.
///
/// Los contratos de los eventos que viajan entre servicios son los records de
/// este namespace, como <c>Common.OrderCreated</c>. Son distintos de los
/// eventos de Event Sourcing de Orders -ver
/// <c>OrdersService.Models.OrderEvent</c>-: esos son internos de Orders y
/// pueden cambiar; estos son públicos y otros servicios dependen de ellos.
/// </remarks>
public static class Events
{
    public const string Exchange = "andis.events";
    public const string OrderCreated = "order.created";
    public const string OrderConfirmed = "order.confirmed";
    public const string OrderRejected = "order.rejected";
    public const string OrderInvoiced = "order.invoiced";
    public const string OrderInvoiceFailed = "order.invoice-failed";
    public const string OrderCancelled = "order.cancelled";
    public const string StockReserved = "stock.reserved";
    public const string StockRejected = "stock.rejected";
    public const string StockReleased = "stock.released";
    public const string UserUpdated = "user.updated";
    public const string ProductUpdated = "product.updated";
}

/// <summary>
/// Representa el evento de una Order que Orders aceptó y todavía no procesó.
/// </summary>
/// <remarks>
/// Saga: es el primer paso de la coreografía. Orders lo publica a través del
/// outbox; Products reserva el stock y responde con <c>Common.StockReserved</c>
/// o <c>Common.StockRejected</c>. Materialized View: el proyector de Orders
/// agrega la Order a la vista como <c>OrdersService.Models.OrderStatus.Pending</c>.
/// </remarks>
public record OrderCreated(int OrderId, string UserId, int ProductId, int Quantity);

/// <summary>
/// Representa el evento de una Order confirmada.
/// </summary>
/// <remarks>
/// Messaging Bridge: lleva la Order completa, y no solo su id, para que un
/// suscriptor externo -el puente hacia el ERP- no tenga que consultar a Orders.
/// Los campos nuevos son opcionales: un mensaje anterior sigue siendo válido.
/// </remarks>
public record OrderConfirmed(int OrderId, string? UserId = null, int ProductId = 0, int Quantity = 0);

/// <summary>
/// Representa el evento de una Order que Orders rechazó.
/// </summary>
/// <remarks>
/// Saga: el worker la rechaza si Products no reservó el stock o si el User no
/// existe. Products libera la reserva, si la hay. Materialized View: el
/// proyector de Orders cambia el estado de la Order en la vista.
/// </remarks>
public record OrderRejected(int OrderId, string Reason);

/// <summary>
/// Representa el evento de una Order que el ERP facturó.
/// </summary>
/// <remarks>
/// Messaging Bridge: lo publica el puente; Orders no sabe que viene de otro
/// broker.
/// </remarks>
public record OrderInvoiced(int OrderId, string InvoiceNumber, string IssuedOn);

/// <summary>
/// Representa el evento de una Order confirmada que el ERP no facturó.
/// </summary>
/// <remarks>
/// Saga: lo publica el puente; Orders cancela la Order y publica
/// <c>Common.OrderCancelled</c>.
/// </remarks>
public record OrderInvoiceFailed(int OrderId, string Reason);

/// <summary>
/// Representa la compensación de una Order confirmada que no se pudo
/// completar.
/// </summary>
/// <remarks>
/// Saga: Products libera el stock que había reservado.
/// </remarks>
public record OrderCancelled(int OrderId, string Reason);

/// <summary>
/// Representa el evento de Products que reservó el stock de una Order.
/// </summary>
/// <remarks>
/// Saga: Products reservó el stock de una Order, o no pudo porque falta
/// stock o porque el Product no existe. Orders sigue con el paso
/// siguiente.
/// </remarks>
public record StockReserved(int OrderId, int ProductId, int Quantity);

/// <summary>
/// Representa el evento de Products que no pudo reservar el stock de una
/// Order.
/// </summary>
/// <remarks>
/// Saga: falta stock o el Product no existe. Products no reservó nada,
/// así que no hay nada que compensar; el worker de Orders rechaza la
/// Order.
/// </remarks>
public record StockRejected(int OrderId, string Reason);

/// <summary>
/// Representa el evento de Products que devolvió el stock de una Order
/// rechazada o cancelada.
/// </summary>
/// <remarks>
/// Saga: es la compensación de la reserva.
/// </remarks>
public record StockReleased(int OrderId, int ProductId, int Quantity);

/// <summary>
/// Representa el evento de un User que cambió.
/// </summary>
/// <remarks>
/// Materialized View: <c>OccurredAt</c> es el momento del cambio, según el
/// reloj del servicio que lo publica. Con varios proyectores, dos cambios del
/// mismo nombre pueden aplicarse en otro orden; la fecha permite descartar el
/// más viejo -ver <c>OrdersService.Messaging.OrderDetailsProjector</c>-. Es opcional, como los campos
/// nuevos de <c>Common.OrderConfirmed</c>: un mensaje anterior sigue siendo
/// válido.
/// </remarks>
public record UserUpdated(string UserId, string Name, DateTime? OccurredAt = null);

/// <summary>
/// Representa el evento de un Product que cambió.
/// </summary>
/// <remarks>
/// Publisher/Subscriber: Products lo publica al cambiar el nombre o el precio
/// de un Product. Materialized View: el proyector de Orders actualiza el
/// nombre en la vista. <c>OccurredAt</c> cumple el mismo papel que en
/// <c>Common.UserUpdated</c>.
/// </remarks>
public record ProductUpdated(int ProductId, string? Name, DateTime? OccurredAt = null);
