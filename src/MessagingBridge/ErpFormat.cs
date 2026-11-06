using Ucu.Andis.Common;

namespace MessagingBridge;

// El traductor entre los contratos de la aplicación y los registros del ERP, de
// ancho fijo. En el catálogo de Azure, el Messaging Bridge reenvía los mensajes
// sin cambiarlos; acá los dos sistemas además hablan formatos distintos, y este
// otro patrón -Message Translator, en Enterprise Integration Patterns- se
// agrega aparte: el reenvío no sabe nada de los formatos.
//
//   PEDIDO  | pedido: 10 | cliente: 8 | artículo: 24 | cantidad: 5
//   FACTURA | pedido: 10 | factura: 12 | fecha AAAAMMDD: 8
public static class ErpFormat
{
    private const string OrderRecord = "PEDIDO  ";
    private const string InvoiceRecord = "FACTURA ";

    public static string ToOrderRecord(OrderConfirmed order) =>
        OrderRecord
        + order.OrderId.ToString("D10")
        + order.UserId.ToString("D8")
        + (order.ProductId ?? "").PadRight(24)
        + order.Quantity.ToString("D5");

    // FormatException si el registro no es una factura válida.
    public static OrderInvoiced ToOrderInvoiced(string record)
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
