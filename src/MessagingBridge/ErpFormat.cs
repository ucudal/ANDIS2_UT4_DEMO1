using Ucu.Andis.Common;

namespace MessagingBridge;

// El traductor entre los contratos de la aplicación y los registros del ERP, de
// ancho fijo. En el catálogo de Azure, el Messaging Bridge reenvía los mensajes
// sin cambiarlos; acá los dos sistemas además hablan formatos distintos, y este
// otro patrón -Message Translator, en Enterprise Integration Patterns- se
// agrega aparte: el reenvío no sabe nada de los formatos.
//
//   PEDIDO  | pedido: 10 | cliente: 24 | artículo: 8 | cantidad: 5
//   FACTURA | pedido: 10 | factura: 12 | fecha AAAAMMDD: 8
//   RECHAZO | pedido: 10 | motivo: 30
public static class ErpFormat
{
    private const string OrderRecord = "PEDIDO  ";
    private const string InvoiceRecord = "FACTURA ";
    private const string RejectionRecord = "RECHAZO ";

    public static string ToOrderRecord(OrderConfirmed order) =>
        OrderRecord
        + order.OrderId.ToString("D10")
        + (order.UserId ?? "").PadRight(24)
        + order.ProductId.ToString("D8")
        + order.Quantity.ToString("D5");

    // La respuesta del ERP a un pedido, como el evento que corresponde:
    // order.invoiced o, si el ERP lo rechazó, order.invoice-failed.
    // FormatException si el registro no es ninguno de los dos.
    public static (string RoutingKey, object Event, int OrderId) ToEvent(string record)
    {
        if (record.StartsWith(RejectionRecord))
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

        return new OrderInvoiceFailed(int.Parse(record[8..18]), record[18..48].Trim());
    }

    // FormatException si el registro no es una factura válida.
    private static OrderInvoiced ToOrderInvoiced(string record)
    {
        if (record.Length != 38 || !record.StartsWith(InvoiceRecord))
            throw new FormatException($"No es un registro FACTURA: '{record}'");

        var issuedOn = record[30..38];
        return new OrderInvoiced(
            int.Parse(record[8..18]),
            record[18..30].Trim(),
            $"{issuedOn[..4]}-{issuedOn[4..6]}-{issuedOn[6..8]}");
    }
}
