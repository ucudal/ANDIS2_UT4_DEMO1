using MessagingBridge;
using Ucu.Andis.Common;

// Messaging Bridge: conecta dos infraestructuras de mensajería, RabbitMQ, la de
// la aplicación, y ActiveMQ, la del ERP. Ni Orders ni el ERP cambian: cada uno
// sigue hablando con su propio broker, y ninguno sabe que el otro existe.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<RabbitMq>();
builder.Services.AddSingleton<LegacyBroker>();
builder.Services.AddHostedService<OrdersToErpBridge>();
builder.Services.AddHostedService<ErpToOrdersBridge>();
builder.Build().Run();
