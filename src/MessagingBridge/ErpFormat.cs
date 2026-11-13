using System.Globalization;
using Ucu.Andis.Common;

namespace Ucu.Andis.MessagingBridge;

/// <summary>
/// Proporciona el traductor entre los contratos de la aplicación y los registros
/// del ERP, de ancho fijo.
/// </summary>
/// <remarks>
/// En el catálogo de Azure, el Messaging Bridge reenvía los mensajes sin
/// cambiarlos; acá los dos sistemas además hablan formatos distintos, y este otro
/// patrón -Message Translator, en Enterprise Integration Patterns- se agrega
/// aparte: el reenvío no sabe nada de los formatos.
/// <code>
/// PEDIDO  | pedido: 10 | cliente: 24 | artículo: 8 | cantidad: 5
/// FACTURA | pedido: 10 | factura: 12 | fecha AAAAMMDD: 8
/// RECHAZO | pedido: 10 | motivo: 30
/// </code>
/// </remarks>
public static class ErpFormat
{
    private const string OrderRecord = "PEDIDO  ";
    private const string InvoiceRecord = "FACTURA ";
    private const string RejectionRecord = "RECHAZO ";

    public static string ToOrderRecord(OrderConfirmed order) =>
        OrderRecord
        + order.OrderId.ToString("D10", CultureInfo.InvariantCulture)
        + (order.UserId ?? "").PadRight(24)
        + order.ProductId.ToString("D8", CultureInfo.InvariantCulture)
        + order.Quantity.ToString("D5", CultureInfo.InvariantCulture);

    /// <summary>
    /// Convierte la respuesta del ERP a un pedido en el evento que corresponde:
    /// <c>order.invoiced</c> o, si el ERP lo rechazó, <c>order.invoice-failed</c>.
    /// </summary>
    /// <remarks>
    /// Lanza <c>FormatException</c> si el registro no es ninguno de los dos.
    /// </remarks>
    public static (string RoutingKey, object Event, int OrderId) ToEvent(string record)
    {
        if (record.StartsWith(RejectionRecord, StringComparison.Ordinal))
        {
            var failed = ToOrderInvoiceFailed(record);
            return (Events.OrderInvoiceFailed, failed, failed.OrderId);
        }

        var invoiced = ToOrderInvoiced(record);
        return (Events.OrderInvoiced, invoiced, invoiced.OrderId);
    }

    private static OrderInvoiceFailed ToOrderInvoiceFailed(string record)
    {
        if (record.Length != 48)
            throw new FormatException($"No es un registro RECHAZO: '{record}'");

        return new OrderInvoiceFailed(int.Parse(record[8..18], CultureInfo.InvariantCulture), record[18..48].Trim());
    }

    // FormatException si el registro no es una factura válida.
    private static OrderInvoiced ToOrderInvoiced(string record)
    {
        if (record.Length != 38 || !record.StartsWith(InvoiceRecord, StringComparison.Ordinal))
            throw new FormatException($"No es un registro FACTURA: '{record}'");

        var issuedOn = record[30..38];
        return new OrderInvoiced(
            int.Parse(record[8..18], CultureInfo.InvariantCulture),
            record[18..30].Trim(),
            $"{issuedOn[..4]}-{issuedOn[4..6]}-{issuedOn[6..8]}");
    }
}
