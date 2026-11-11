using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Ucu.Andis.Common;
using Ucu.Andis.OrdersService.Data;
using Ucu.Andis.OrdersService.Models;

namespace Ucu.Andis.OrdersService.Messaging;

// Materialized View: mantiene la tabla order_details a partir de los eventos.
// Publisher/Subscriber: se suscribe con su propia cola a los eventos de Orders,
// Users y Products; recibe order.created igual que el worker, sin que Orders
// sepa que existe.
//
// Corre en orders-projector, con tres réplicas que consumen la misma cola, como
// el worker. Cada mensaje va a una réplica distinta y se procesa en paralelo:
// los eventos de una misma Order pueden aplicarse en cualquier orden, y dos
// copias del mismo evento -el outbox y el puente entregan al menos una vez-
// pueden procesarse a la vez. Los proyectores no guardan nada en memoria, pero
// eso no alcanzaba: con uno solo, su resultado dependía del orden de los
// mensajes. Sin lo que sigue, con varias réplicas pasaría esto, sin errores
// visibles:
//
// a. Estado perdido por desorden. La réplica A recibe order.created y llama a
//    Users y a Products, que tardan. El worker confirma la Order y la réplica B
//    recibe order.confirmed, no encuentra la fila y descarta el estado. A
//    inserta la fila como Pending, y la vista queda Pending para siempre.
//    Solución: el estado solo avanza -Pending pasa a Confirmed o Rejected, que
//    son finales-, así que el resultado no depende del orden. SetStatusAsync
//    crea la fila si no existe, y AddOrderAsync nunca pisa el estado.
// b. Duplicados que pasan el chequeo. Verificar si la fila o el evento ya
//    existen y después insertarlos son dos pasos: dos copias en dos réplicas
//    pasan las dos el chequeo. Solución: una sola sentencia atómica, un upsert,
//    o un índice único que hace fallar la segunda inserción.
// c. Una réplica trabada. Una excepción no capturada deja el mensaje sin ack ni
//    nack; con prefetch 1, RabbitMQ no le entrega más mensajes a esa réplica.
//    Solución: toda excepción devuelve el mensaje a la cola.
// d. Nombres aplicados al revés. Dos cambios del nombre de un User -"Ana",
//    después "Ana María"- llegan a réplicas distintas y el más viejo se aplica
//    último. Cada cambio es idempotente, pero no conmutativo. Solución: cada
//    nombre guarda la fecha en que cambió, y uno más viejo no pisa uno más
//    nuevo.
public class OrderDetailsProjector : BackgroundService
{
    public const string DetailsQueue = "orders.details-view";

    private static readonly TimeSpan RequeueDelay = TimeSpan.FromSeconds(5);

    private readonly RabbitMq _rabbitMq;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OrderDetailsProjector> _logger;

    public OrderDetailsProjector(RabbitMq rabbitMq, IServiceScopeFactory scopeFactory, ILogger<OrderDetailsProjector> logger)
    {
        _rabbitMq = rabbitMq;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connection = await ConnectAsync(stoppingToken);
        var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
        await RabbitMq.DeclareSubscriberQueueAsync(channel, DetailsQueue,
            ["order.*", Events.UserUpdated, Events.ProductUpdated], stoppingToken);
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            bool applied;
            try
            {
                applied = await ApplyAsync(delivery.RoutingKey, delivery.Body, stoppingToken);
            }
            catch (JsonException)
            {
                // Un mensaje mal formado no se va a poder aplicar nunca.
                _logger.LogWarning("[MATERIALIZED VIEW] Se descarta un mensaje {RoutingKey} mal formado", delivery.RoutingKey);
                applied = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Cualquier otra falla -la base no responde, un error de SQL-
                // devuelve el mensaje a la cola. Sin este catch, RabbitMQ.Client
                // registra la excepción y deja el mensaje sin ack ni nack: con
                // prefetch 1, esta réplica no recibe más mensajes hasta que se
                // cierre el canal. Trade-off: un mensaje que falla siempre -un
                // poison message- vuelve a la cola para siempre; la alternativa
                // es una dead-letter queue, que la demo no tiene.
                _logger.LogError(ex, "[MATERIALIZED VIEW] No se pudo aplicar {RoutingKey}; vuelve a la cola", delivery.RoutingKey);
                applied = false;
            }

            if (applied)
            {
                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, stoppingToken);
            }
            else
            {
                await Task.Delay(RequeueDelay, stoppingToken);
                await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, stoppingToken);
            }
        };

        await channel.BasicConsumeAsync(DetailsQueue, autoAck: false, consumer, stoppingToken);
        _logger.LogInformation("[PUB/SUB] Proyector escuchando la cola {Queue}", DetailsQueue);

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task<IConnection> ConnectAsync(CancellationToken stoppingToken)
    {
        while (true)
        {
            try
            {
                return await _rabbitMq.GetConnectionAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Esperando a RabbitMQ: {Message}", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }
    }

    // Devuelve false si el evento no se pudo aplicar porque una dependencia no
    // está disponible; en ese caso el mensaje vuelve a la cola.
    private async Task<bool> ApplyAsync(string routingKey, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        _logger.LogInformation("[PUB/SUB] Proyector recibió {RoutingKey}", routingKey);

        switch (routingKey)
        {
            case Events.OrderCreated:
                var created = JsonSerializer.Deserialize<OrderCreated>(body.Span)!;
                return await AddOrderAsync(db, scope.ServiceProvider.GetRequiredService<IHttpClientFactory>(), created, cancellationToken);

            case Events.OrderConfirmed:
                var confirmed = JsonSerializer.Deserialize<OrderConfirmed>(body.Span)!;
                await SetStatusAsync(db, confirmed.OrderId, OrderStatus.Confirmed,
                    confirmed.UserId, confirmed.ProductId, confirmed.Quantity, cancellationToken);
                return true;

            case Events.OrderRejected:
                // OrderRejected no trae los datos de la Order: si la fila no
                // existe, se crea con valores por defecto y order.created los
                // completa cuando llegue.
                var rejected = JsonSerializer.Deserialize<OrderRejected>(body.Span)!;
                await SetStatusAsync(db, rejected.OrderId, OrderStatus.Rejected, 0, null, 0, cancellationToken);
                return true;

            // Messaging Bridge: la factura del ERP queda en el historial de la
            // Order. Es idempotente: el puente entrega cada mensaje al menos una
            // vez. No se pregunta antes si la factura ya está, porque dos copias
            // en dos réplicas pasarían las dos el chequeo y la factura quedaría
            // dos veces. Se inserta directamente: el índice único de
            // order_events por Order y tipo hace fallar la segunda copia -ver
            // mysqlinit.sql-.
            case Events.OrderInvoiced:
                var invoiced = JsonSerializer.Deserialize<OrderInvoiced>(body.Span)!;
                db.OrderEvents.Add(OrderEvent.Create(invoiced.OrderId, OrderEventTypes.OrderInvoiced,
                    new OrderInvoicedData(invoiced.InvoiceNumber, invoiced.IssuedOn)));
                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException ex) when (OrdersDbContext.IsDuplicateKey(ex))
                {
                    // EF Core igual registra la sentencia fallida en el log, con
                    // el nivel de error: acá es el resultado esperado.
                    _logger.LogInformation("[MESSAGING BRIDGE] La factura {InvoiceNumber} de la Order {OrderId} ya estaba registrada; se ignora la copia",
                        invoiced.InvoiceNumber, invoiced.OrderId);
                    return true;
                }
                _logger.LogInformation("[MESSAGING BRIDGE] Order {OrderId} facturada: {InvoiceNumber}", invoiced.OrderId, invoiced.InvoiceNumber);
                return true;

            // Los nombres copiados en la vista se actualizan cuando cambian en el
            // servicio que es dueño del dato. Hasta que llega el evento, la vista
            // muestra el nombre anterior: consistencia eventual.
            //
            // Solo se aplica un nombre más nuevo que el que tiene la fila. Sin
            // esa condición, dos cambios seguidos procesados por réplicas
            // distintas podrían aplicarse al revés y dejar el nombre viejo. Un
            // evento sin fecha, de una versión anterior, se toma como de ahora.
            case Events.UserUpdated:
                var user = JsonSerializer.Deserialize<UserUpdated>(body.Span)!;
                var userChangedAt = user.OccurredAt ?? DateTime.UtcNow;
                var users = await db.OrderDetails
                    .Where(d => d.UserId == user.UserId && (d.CustomerNameAt == null || d.CustomerNameAt < userChangedAt))
                    .ExecuteUpdateAsync(d => d
                        .SetProperty(x => x.CustomerName, user.Name)
                        .SetProperty(x => x.CustomerNameAt, userChangedAt)
                        .SetProperty(x => x.UpdatedAt, DateTime.UtcNow), cancellationToken);
                _logger.LogInformation("[MATERIALIZED VIEW] Nombre del User {UserId} actualizado a {Name} en {Rows} filas",
                    user.UserId, user.Name, users);
                return true;

            case Events.ProductUpdated:
                var product = JsonSerializer.Deserialize<ProductUpdated>(body.Span)!;
                var productChangedAt = product.OccurredAt ?? DateTime.UtcNow;
                var products = await db.OrderDetails
                    .Where(d => d.ProductId == product.ProductId && (d.ProductNameAt == null || d.ProductNameAt < productChangedAt))
                    .ExecuteUpdateAsync(d => d
                        .SetProperty(x => x.ProductName, product.Name)
                        .SetProperty(x => x.ProductNameAt, productChangedAt)
                        .SetProperty(x => x.UpdatedAt, DateTime.UtcNow), cancellationToken);
                _logger.LogInformation("[MATERIALIZED VIEW] Nombre del Product {ProductId} actualizado a {Name} en {Rows} filas",
                    product.ProductId, product.Name, products);
                return true;

            default:
                return true;
        }
    }

    // Busca una sola vez el nombre del cliente y del producto, al crearse la
    // Order, en lugar de en cada consulta de detalle. Usa los mismos HttpClient
    // que la consulta síncrona, con Retry, Circuit Breaker y Bulkhead.
    //
    // Es idempotente y tolera el desorden, con una sola sentencia: inserta la
    // fila o, si ya existe, completa sus datos. Buscarla primero y después
    // insertarla serían dos pasos: con dos copias de order.created en dos
    // réplicas, las dos la buscarían, ninguna la encontraría, y la segunda
    // inserción fallaría por la clave primaria.
    //
    // Si la fila ya existe, no se toca el estado: puede haber llegado antes la
    // confirmación o el rechazo -ver SetStatusAsync-. Los nombres se escriben
    // solo si son más nuevos que los de la fila: la fecha es el momento en que
    // se leyeron, y un user.updated posterior gana. En ON DUPLICATE KEY UPDATE
    // MySQL asigna de izquierda a derecha, así que cada nombre se asigna antes
    // que su fecha, mientras la condición todavía ve la fecha anterior.
    private async Task<bool> AddOrderAsync(OrdersDbContext db, IHttpClientFactory httpClientFactory, OrderCreated created, CancellationToken cancellationToken)
    {
        var user = await Dependencies.GetUserAsync(httpClientFactory.CreateClient("UsersService"), created.UserId, cancellationToken);
        var product = await Dependencies.GetAsync<ProductDto>(httpClientFactory.CreateClient("ProductsService"), $"{created.ProductId}", cancellationToken);

        if (!user.Available || !product.Available)
        {
            _logger.LogWarning("[MATERIALIZED VIEW] La Order {OrderId} vuelve a la cola [usuarios: {Users}, productos: {Products}]",
                created.OrderId, user.Failure ?? "ok", product.Failure ?? "ok");
            return false;
        }

        var readAt = DateTime.UtcNow;
        var customerName = user.Value?.Name;
        var productName = product.Value?.Name;

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO order_details
                (order_id, user_id, customer_name, customer_name_at, product_id, product_name, product_name_at, quantity, status, updated_at)
            VALUES
                ({created.OrderId}, {created.UserId}, {customerName}, {readAt}, {created.ProductId}, {productName}, {readAt}, {created.Quantity}, {OrderStatus.Pending}, {readAt})
            AS new
            ON DUPLICATE KEY UPDATE
                user_id = new.user_id,
                customer_name = IF(order_details.customer_name_at IS NULL OR order_details.customer_name_at < new.customer_name_at,
                                   new.customer_name, order_details.customer_name),
                customer_name_at = IF(order_details.customer_name_at IS NULL OR order_details.customer_name_at < new.customer_name_at,
                                      new.customer_name_at, order_details.customer_name_at),
                product_id = new.product_id,
                product_name = IF(order_details.product_name_at IS NULL OR order_details.product_name_at < new.product_name_at,
                                  new.product_name, order_details.product_name),
                product_name_at = IF(order_details.product_name_at IS NULL OR order_details.product_name_at < new.product_name_at,
                                     new.product_name_at, order_details.product_name_at),
                quantity = new.quantity,
                updated_at = new.updated_at
            """, cancellationToken);

        _logger.LogInformation("[MATERIALIZED VIEW] Order {OrderId} agregada a la vista: {Customer}, {Product}",
            created.OrderId, customerName ?? "User inexistente", productName ?? "Product inexistente");
        return true;
    }

    // Pone la Order en su estado final, con una sola sentencia. Si la fila
    // todavía no existe -order.created lo procesa otra réplica, que tarda
    // porque llama a Users y a Products-, la crea con el estado final y los
    // datos que trae el evento; AddOrderAsync completa los nombres después.
    // Sin esto, el estado se descartaba y la fila quedaba Pending para siempre.
    //
    // Alcanza con que el estado solo avance: Pending pasa a Confirmed o a
    // Rejected, y esos dos son finales. Por eso no hace falta numerar los
    // eventos de cada Order: aplicados en cualquier orden, el resultado es el
    // mismo. Si la fila ya tiene un estado final, no se cambia.
    private async Task SetStatusAsync(OrdersDbContext db, int orderId, string status,
        int userId, string? productId, int quantity, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        // MySQL cuenta 1 fila si insertó, y 2 o 0 si la fila ya existía.
        var rows = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO order_details (order_id, user_id, product_id, quantity, status, updated_at)
            VALUES ({orderId}, {userId}, {productId}, {quantity}, {status}, {now})
            AS new
            ON DUPLICATE KEY UPDATE
                status = IF(order_details.status = {OrderStatus.Pending}, new.status, order_details.status),
                updated_at = new.updated_at
            """, cancellationToken);

        if (rows == 1)
            _logger.LogInformation("[MATERIALIZED VIEW] La Order {OrderId} todavía no estaba en la vista: se agrega como {Status}", orderId, status);
        else
            _logger.LogInformation("[MATERIALIZED VIEW] Order {OrderId} pasa a {Status}", orderId, status);
    }
}
