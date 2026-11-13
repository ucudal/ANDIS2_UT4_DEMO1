using Ucu.Andis.LegacyErp;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<Erp>();
builder.Build().Run();

/// <summary>
/// Representa un ERP legado simulado.
/// </summary>
/// <remarks>
/// Solo sabe leer registros de ancho fijo de la cola <c>ERP.PEDIDOS</c> de ActiveMQ
/// y escribir sus respuestas en <c>ERP.FACTURAS</c>: una factura, o un rechazo si
/// el pedido supera las unidades que factura sin aprobación. No conoce RabbitMQ, ni
/// JSON, ni la aplicación: no comparte código con ella. Es el sistema que no se
/// puede cambiar.
/// <code>
/// PEDIDO  | pedido: 10 | cliente: 24 | artículo: 8 | cantidad: 5
/// FACTURA | pedido: 10 | factura: 12 | fecha AAAAMMDD: 8
/// RECHAZO | pedido: 10 | motivo: 30
/// </code>
/// </remarks>
partial class Program { }
