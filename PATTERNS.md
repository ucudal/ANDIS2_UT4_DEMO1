# Demos de la UT4: patrones de arquitectura sobre una misma aplicación

* [Aplicación base](#aplicación-base)
* [Demos de disponibilidad](#demos-de-disponibilidad)
  * [Qué agrega a la aplicación base](#qué-agrega-a-la-aplicación-base)
  * [Patrones de disponibilidad](#patrones-de-disponibilidad)
    * [Retry](#retry)
    * [Circuit Breaker](#circuit-breaker)
    * [Bulkhead](#bulkhead)
    * [Throttling](#throttling)
    * [Rate Limiting](#rate-limiting)
    * [Health Endpoint Monitoring](#health-endpoint-monitoring)
    * [Competing Consumers para disponibilidad](#competing-consumers-para-disponibilidad)
    * [Event Sourcing](#event-sourcing)
    * [Transactional Outbox](#transactional-outbox)
* [Demos de rendimiento y procesamiento asíncrono](#demos-de-rendimiento-y-procesamiento-asíncrono)
  * [Qué agrega a las demos de disponibilidad](#qué-agrega-a-las-demos-de-disponibilidad)
  * [Patrones de rendimiento](#patrones-de-rendimiento)
    * [Cache-Aside](#cache-aside)
    * [Queue-Based Load Leveling](#queue-based-load-leveling)
    * [Competing Consumers para rendimiento](#competing-consumers-para-rendimiento)
    * [Publisher/Subscriber](#publishersubscriber)
    * [Asynchronous Request-Reply](#asynchronous-request-reply)
    * [CQRS](#cqrs)
    * [Materialized View](#materialized-view)
* [Demos de seguridad](#demos-de-seguridad)
  * [Qué agrega a las demos de rendimiento](#qué-agrega-a-las-demos-de-rendimiento)
  * [Patrones de seguridad](#patrones-de-seguridad)
    * [Federated Identity](#federated-identity)
    * [Gateway Offloading](#gateway-offloading)
    * [Gatekeeper](#gatekeeper)
    * [Sidecar](#sidecar)
    * [Valet Key](#valet-key)
* [Demos de facilidad de modificación](#demos-de-facilidad-de-modificación)
* [Demos de facilidad de despliegue](#demos-de-facilidad-de-despliegue)
* [Cobertura](#cobertura)

## Aplicación base

Todas las demos parten de la misma aplicación de microservicios, que es la de
este repositorio:

| Servicio | Responsabilidad | Base de datos |
| -------- | --------------- | ------------- |
| `users-service` | Alta y consulta de [`Users`](src/UsersService/Models/Users.cs) | PostgreSQL |
| `products-service` | Catálogo de [`Products`](src/ProductsService/Models/Product.cs) | MongoDB |
| `orders-service` | Alta y consulta de [`Orders`](src/OrdersService/Models/Order.cs); arma el detalle de las `Orders` de un `User` consultando a `Users` y `Products` por HTTP | MySQL |
| `api-gateway` | Punto de entrada único -nginx- que enruta `/users/`, `/products/` y `/orders/` | — |

La funcionalidad base no cambia entre demos: dar de alta `Users` y `Products`,
crear `Orders` y consultar el detalle de las `Orders` de un `User`. Cada demo
agrega patrones sobre esta base, así se puede comparar el comportamiento sin
cambiar la experiencia de uso.

<!-- TODO: Revisar lo siguiente una vez implementadas las demos -->
Algunos patrones necesitan infraestructura que hoy no está -un broker de
mensajes, una caché, un proveedor de identidad, un almacenamiento de archivos-.
Se agregan como contenedores en el `docker-compose.yaml` de la demo que los use.
No se agregan servicios de dominio nuevos.

<!-- TODO: Revisar lo siguiente una vez implementadas las demos -->
La dependencia que más se aprovecha es la de `GET
/orders/user/{userId}/details`: `Orders` llama de forma sincrónica a `Users` y a
`Products`. Es el punto natural para mostrar fallas en cascada, cachés y vistas
materializadas.

## Demos de disponibilidad

Escenario: [`Users`](src/UsersService) o [`Products`](src/ProductsService) fallan o
responden lento de forma intermitente. [`Orders`](src/OrdersService) debe seguir
funcionando y el resto del sistema no debe caerse.

Qué se muestra:

* Aislamiento de recursos para que la lentitud de un servicio no agote los
  recursos que usan los demás.
* Degradación controlada del detalle de `Orders` cuando `Products` o `Users` no
  responden.
* Protección contra el exceso de solicitudes, tanto en el borde como en el
  servicio.
* Monitoreo activo de la salud de cada servicio.
* Procesamiento de `Orders` en segundo plano, repartido entre varias réplicas.
* Historial inmutable de eventos de cada `Order`.
* Mensajes que no se pierden aunque RabbitMQ no esté disponible.

Resultado observable: si `Products` se cae, el circuito se abre, `Orders` deja
de esperar *timeouts* y el detalle devuelve las `Orders` con "no disponible
-circuito abierto-" en lugar de un error. `Users` y `Products` siguen atendiendo
sus propias rutas.

### Qué agrega a la aplicación base

| Componente | Para qué |
| ---------- | -------- |
| `rabbitmq` | Exchange `andis.events`, donde se publican los eventos, y cola `orders.process` con las `Orders` pendientes de procesar. Consola en <http://localhost:15672> -usr: `guest` pwd: `guest`-. |
| `orders-worker` -3 réplicas- | La misma imagen que `orders-service`, con el *worker* habilitado -`Worker__Enabled=true`-. Consume la cola y confirma o rechaza cada `Order`. |
| Estado de la [`Order`](src/OrdersService/Models/Order.cs) | `POST /orders/` guarda la `Order` como `Pending`. Un *worker* verifica que el `User` y el `Product` existan y la pasa a `Confirmed` o `Rejected`. |
| Inyección de fallas | `PUT /users/chaos`, `PUT /products/chaos` y `PUT /orders/chaos` hacen que el servicio falle o demore a propósito, sin reiniciarlo. Así se provocan las situaciones que activan cada patrón. Está en el método `UseChaos` de la clase `ChaosExtensions`, en [Common/Chaos.cs](src/Common/Chaos.cs). |
| Interruptores de los patrones | `PUT /orders/patterns` prende y apaga el Retry, el Circuit Breaker, el Bulkhead y el Throttling de `Orders`, sin reiniciarlo; `DELETE /orders/patterns` los vuelve a prender. Cada demo deja prendido solo su patrón, para que no intervengan los demás. Está en la clase `PatternSettings` y en el método `MapPatterns` de la clase `PatternExtensions`, en [OrdersService/Patterns.cs](src/OrdersService/Patterns.cs). |
| Observabilidad y carga -opcional- | Perfil `observability` de Docker Compose con Prometheus y un dashboard de Grafana ya configurado, y escenarios de carga con k6 en [k6/](k6). Muestra los patrones bajo carga y a lo largo del tiempo. Cómo usarlos está en el [README](README.md#observabilidad-y-pruebas-de-carga). |

Ejemplo de inyección de fallas:

```bash
# Products falla en el 50 % de las solicitudes y demora 1 segundo en todas
curl -X PUT http://localhost/products/chaos -H "Content-Type: application/json" \
  -d '{"failureRate": 0.5, "delayMs": 1000}'

# Vuelve a la normalidad
curl -X DELETE http://localhost/products/chaos
```

> [!TIP]
>
> Consulta [Write custom ASP.NET Core middleware](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/middleware/write?view=aspnetcore-10.0)
> para tener más información sobre cómo está implementada la inyección de
> fallas: el método `UseChaos` agrega un *middleware* que atiende `/chaos` y
> hace fallar o demorar las demás solicitudes.

### Patrones de disponibilidad

Cada patrón tiene un script en [demos/](demos) que provoca la situación y
muestra el resultado. Los de Retry, Circuit Breaker, Bulkhead y Throttling
dejan prendido solo su patrón, para que se vea el efecto de cada uno por
separado. Cómo ejecutarlos está en el
[README](README.md#cómo-probar-los-patrones). Los eventos de cada patrón también
aparecen en los *logs* de Docker de `orders-service`, con una etiqueta: `[RETRY]`,
`[CIRCUIT BREAKER]`, `[BULKHEAD]`, `[THROTTLING]` y `[OUTBOX]`. Los patrones que dependen de
la carga -Circuit Breaker, Bulkhead, Throttling, Rate Limiting y Competing
Consumers- tienen además un escenario de k6 para verlos en el dashboard.

#### Retry

**Problema:** una falla transitoria -un error puntual de red, un servicio que se
reinicia- hace fallar una operación que habría funcionado un momento después.

**Implementación:** los `HttpClient` de `Orders` hacia `Users` y `Products`
reintentan hasta 3 veces con retroceso exponencial -0,5 s, 1 s, 2 s, con
*jitter*-. Solo se reintentan los errores transitorios: 5xx, 408, 429, errores
de red y *timeouts*; un 404 no se reintenta. Está en el método
`AddResilience` de la clase `Resilience`, en
[OrdersService/Resilience.cs](src/OrdersService/Resilience.cs), con
`Microsoft.Extensions.Http.Resilience`.

**Cómo funciona:**

![Secuencia de un reintento de Orders a Products](assets/01-retry.svg)

Si fallan los 3 reintentos, la consulta dice `"no disponible [error HTTP 503]"`.
Las esperas crecen: 0,5 s, 1 s y 2 s -`DelayBackoffType.Exponential`-.

**Código:** la estrategia de reintento del *pipeline* de cada dependencia, en
el método `AddResilience` de la clase `Resilience`, en
[OrdersService/Resilience.cs](src/OrdersService/Resilience.cs). `ShouldHandle` decide qué se
reintenta; el `patterns.Retry` permite apagarlo en las demos:

```csharp
pipeline.AddRetry(new HttpRetryStrategyOptions
{
    ShouldHandle = args => patterns.Retry ? shouldRetry(args) : PredicateResult.False(),
    MaxRetryAttempts = MaxRetries,
    BackoffType = DelayBackoffType.Exponential,
    UseJitter = true,
    Delay = RetryBaseDelay,
    OnRetry = args => { /* registra [RETRY] en el log */ }
});
```

**Cómo verlo:** el detalle de las `Orders` de un `User` lo arma `Orders`, que le
pide a `Users` el nombre del cliente y a `Products` el nombre del producto de
cada `Order`. [demos/01-retry.sh](demos/01-retry.sh) hace que `Products` falle
en el 30 % de las solicitudes y consulta el detalle 8 veces con el Retry
apagado y 8 con el Retry prendido. Sin reintentos, cerca de la mitad de las
respuestas trae algún producto "no disponible"; con reintentos, casi todas
traen el nombre del producto, porque los reintentos ocultan las fallas. Las que
tardan más son las que necesitaron reintentos. Los *logs* de
Docker de `orders-service` muestran cada `[RETRY]` con su demora y su causa; el comando
para verlos está en
[commands/02-availability.azcli](commands/02-availability.azcli).

> [!TIP]
>
> Consulta [Build resilient HTTP apps: Key development
> patterns](https://learn.microsoft.com/en-us/dotnet/core/resilience/http-resilience) para tener más
> información sobre cómo `AddResilienceHandler` arma el *pipeline* de cada
> `HttpClient`, y [HTTP requests with IHttpClientFactory](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/http-requests?view=aspnetcore-10.0)
> sobre los `HttpClient` con nombre, uno por dependencia.

#### Circuit Breaker

**Problema:** si una dependencia está caída, seguir llamándola -y reintentando-
desperdicia recursos, demora las respuestas y la sobrecarga justo cuando intenta
recuperarse.

**Implementación:** un circuito por dependency, en el mismo pipeline que el
Retry. Se abre si fallan al menos la mitad de las llamadas en 10 segundos, con
un mínimo de 5 llamadas. Abierto, las llamadas fallan de inmediato durante 15
segundos; después pasa a semiabierto, deja pasar una llamada de prueba y, si
funciona, se cierra. El detalle de `Orders` no devuelve error: muestra `"no
disponible [circuito abierto]"` en lugar del nombre: lo arma el método
`GetOrdersForUser` de la clase `OrdersController`, en
[OrdersService/Controllers/OrdersController.cs](src/OrdersService/Controllers/OrdersController.cs).

**Cómo funciona:**

![Estados del circuito: cerrado, abierto y semiabierto](assets/02-circuit-breaker.svg)

Los eventos `OnOpened`, `OnHalfOpened` y `OnClosed` son los del panel "Eventos
de resiliencia", y el estado es el del panel "Estado del circuito de cada
dependencia".

**Código:** la estrategia del *pipeline* de cada dependencia, en
[OrdersService/Resilience.cs](src/OrdersService/Resilience.cs). Va dentro del Retry, así que cada
reintento cuenta como una llamada más para el circuito:

```csharp
pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
{
    ShouldHandle = args => patterns.CircuitBreaker ? shouldBreak(args) : PredicateResult.False(),
    FailureRatio = CircuitFailureRatio,            // 0,5
    MinimumThroughput = CircuitMinimumThroughput,  // 5
    SamplingDuration = CircuitSamplingDuration,    // 10 s
    BreakDuration = CircuitBreakDuration,          // 15 s
    OnOpened = args => { /* registra [CIRCUIT BREAKER] ABIERTO */ },
    OnHalfOpened = args => { /* SEMIABIERTO */ },
    OnClosed = args => { /* CERRADO */ }
});
```

**Cómo verlo:** [demos/02-circuit-breaker.sh](demos/02-circuit-breaker.sh) hace
que `Products` falle en el 100 % de las solicitudes y consulta el detalle 6
veces con todos los patrones apagados y 6 con solo el Circuit Breaker prendido.
Sin el circuito, `Products` recibe 12 llamadas, aunque falle siempre; con el
circuito, 5. Sin reintentos, cada falla cuenta una vez: las dos primeras consultas dicen `"no
disponible [error HTTP 503]"`, en la tercera el circuito se abre, y desde ahí
responden con `"no disponible [circuito abierto]"` sin llamar a `Products`. Cuando
`Products` se recupera, el circuito sigue abierto hasta que pasan los 15
segundos. Los *logs* de Docker muestran las transiciones ABIERTO → SEMIABIERTO →
CERRADO. Con el Retry prendido, la primera consulta tardaría unos 3 segundos,
porque reintentaría cada llamada antes de que se abriera el circuito.

> [!TIP]
>
> Consulta [Introduction to resilient app development](https://learn.microsoft.com/en-us/dotnet/core/resilience/)
> para tener más información sobre las estrategias de Polly que usa el
> *pipeline*, entre ellas el Circuit Breaker y su `CircuitBreakerManualControl`.

#### Bulkhead

**Problema:** si una dependencia se pone lenta, las llamadas pendientes se
acumulan y consumen los recursos -conexiones, memoria, hilos- que necesitan las
demás funciones.

**Implementación:** un límite de concurrencia por dependencia, en el método
`AddResilience` de la clase `Resilience`, en
[OrdersService/Resilience.cs](src/OrdersService/Resilience.cs). Las llamadas que
exceden el límite se rechazan de inmediato. Como cada dependencia tiene su
propio compartimento, la lentitud de `Products` no afecta las llamadas a
`Users`. Cada compartimento se dimensiona según su dependencia, al llamar a
`AddResilience` en [OrdersService/Program.cs](src/OrdersService/Program.cs): `Products` tiene 5
llamadas en curso y 5 en espera; `Users`, que responde rápido pero se llama en
cada consulta, tiene 20 en curso y 5 en espera.

**Cómo funciona:**

![Compartimentos separados para Users y Products](assets/03-bulkhead.svg)

Cada llamada toma un lugar del compartimento de su dependencia. Si no hay lugar,
espera en la fila; si la fila también está llena, se rechaza. Con `Products`
lento se llena su compartimento, pero el de `Users` sigue libre.

**Código:** un `ConcurrencyLimiter` por dependencia, en el método
`AddResilience` de la clase `Resilience`, en
[OrdersService/Resilience.cs](src/OrdersService/Resilience.cs), y el tamaño de cada compartimento,
en las llamadas a `AddResilience` de [OrdersService/Program.cs](src/OrdersService/Program.cs):

```csharp
// Resilience.cs
var bulkhead = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
{
    PermitLimit = bulkheadPermitLimit,  // llamadas en curso
    QueueLimit = bulkheadQueueLimit     // llamadas en espera
});
pipeline.AddRateLimiter(new HttpRateLimiterStrategyOptions
{
    RateLimiter = args => patterns.Bulkhead
        ? bulkhead.AcquireAsync(1, args.Context.CancellationToken)
        : ValueTask.FromResult<RateLimitLease>(AcquiredLease.Instance),
    OnRejected = _ => { /* registra [BULKHEAD] en el log */ }
});

// Program.cs
builder.Services.AddHttpClient("UsersService", ...).AddResilience("users", bulkheadPermitLimit: 20);
builder.Services.AddHttpClient("ProductsService", ...).AddResilience("products");
```

**Cómo verlo:** [demos/03-bulkhead.sh](demos/03-bulkhead.sh) hace que `Products`
demore 1,5 segundos y lanza 15 consultas simultáneas, primero con el Bulkhead
apagado y después prendido. Sin el Bulkhead, las 15 traen los nombres, pero
`Products` llega a atender 15 llamadas a la vez. Con el Bulkhead, nunca más de
5: 10 consultas entran al compartimento de `Products` y traen los nombres de los
productos, y las otras 5 se rechazan de inmediato con `"no disponible [bulkhead
lleno]"`. El nombre del `User` aparece en todas las respuestas.

> [!TIP]
>
> Consulta [ConcurrencyLimiter Class](https://learn.microsoft.com/en-us/dotnet/api/system.threading.ratelimiting.concurrencylimiter)
> para tener más información sobre el limitador que funciona como
> compartimento, con sus lugares en curso y en espera.

#### Throttling

**Problema:** un pico de carga puede saturar un servicio y degradarlo para todos
sus clientes.

**Implementación:** `Orders` atiende como máximo 20 consultas de detalle a la
vez. Las que exceden el límite reciben `503 Service Unavailable` con
`Retry-After: 2`, en lugar de encolarse. La política `throttling` se define
con `AddRateLimiter` en [OrdersService/Program.cs](src/OrdersService/Program.cs), y
se aplica con el atributo `[EnableRateLimiting("throttling")]` en el método
`GetOrdersForUser` de la clase `OrdersController`, en
[OrdersService/Controllers/OrdersController.cs](src/OrdersService/Controllers/OrdersController.cs).

El gateway también aplica Throttling, pero por cliente: cada IP puede crear como
máximo 1 `Order` por segundo, con una ráfaga de hasta 5. Lo que excede el límite
recibe `429 Too Many Requests` sin llegar a `orders-service`; las consultas no
tienen límite. Está en
[ApiGateway/nginxconf/nginx.conf](src/ApiGateway/nginxconf/nginx.conf)
-`limit_req_zone` y `limit_req`-. Es el límite que respeta el cliente de la
demo de Rate Limiting.

**Cómo funciona:**

![Decisión del Throttling al recibir una consulta](assets/04-throttling.svg)

Es el mismo tipo de limitador que el [Bulkhead](#bulkhead), en la dirección
opuesta: el Bulkhead limita las llamadas que `Orders` hace; el Throttling, las
solicitudes que `Orders` recibe.

**Código:** la política, con `AddRateLimiter` en
[OrdersService/Program.cs](src/OrdersService/Program.cs), y el atributo que la
aplica, en el método `GetOrdersForUser` de la clase `OrdersController`, en
[OrdersService/Controllers/OrdersController.cs](src/OrdersService/Controllers/OrdersController.cs):

```csharp
// Program.cs
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status503ServiceUnavailable;
    options.AddPolicy("throttling", context =>
        context.RequestServices.GetRequiredService<PatternSettings>().Throttling
            ? RateLimitPartition.GetConcurrencyLimiter("throttling", _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = 20,
                QueueLimit = 0
            })
            : RateLimitPartition.GetNoLimiter("sin throttling"));
    options.OnRejected = (context, _) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "2";
        // registra [THROTTLING] en el log
        return ValueTask.CompletedTask;
    };
});

// OrdersController.cs
[HttpGet("user/{userId}/details")]
[EnableRateLimiting("throttling")]
public async Task<ActionResult<List<object>>> GetOrdersForUser(int userId)
```

**Cómo verlo:** [demos/04-throttling.sh](demos/04-throttling.sh) hace que
`Orders` demore 5 segundos en cada consulta y lanza 40 simultáneas, primero con
el Throttling apagado y después prendido. Sin el Throttling, las 40 responden
200 y `Orders` llega a atender 40 a la vez. Con el Throttling, 20 responden 200
con todos los nombres y 20 se rechazan al instante con 503. La demo apaga
el Bulkhead; con el Bulkhead prendido, algunas respuestas 200 traerían un
producto "no disponible", porque las 20 consultas atendidas llaman a `Products`
a la vez y llenan su compartimento: en un sistema real, el Throttling y el
Bulkhead actúan juntos. El Throttling por cliente del gateway se ve en la demo
de Rate Limiting.

> [!TIP]
>
> Consulta [Rate limiting middleware in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-10.0)
> para tener más información sobre cómo está implementada la política
> `throttling` con `AddRateLimiter` y `[EnableRateLimiting]`.

#### Rate Limiting

**Problema:** un cliente que envía solicitudes más rápido de lo que acepta el
servicio al que llama recibe rechazos y tiene que reintentarlos. Las solicitudes
rechazadas no avanzan el trabajo, pero consumen capacidad del cliente, de la red
y del servicio, y los reintentos agregan esperas. Es el caso típico de un
proceso por lotes que envía muchos datos a un servicio con límite de capacidad.

**Implementación:** el cliente regula su propio ritmo para no superar el límite
del servicio. Rate Limiting y Throttling son las dos caras del mismo límite: el
servicio aplica Throttling para protegerse, y el cliente aplica Rate Limiting
para no chocar contra ese Throttling. En esta aplicación, el servicio es el
gateway, con su Throttling por cliente -1 `POST /orders/` por segundo por IP,
con una ráfaga de 5; ver [Throttling](#throttling)-, y el cliente es un proceso
por lotes que crea `Orders`: [demos/05-rate-limiting.sh](demos/05-rate-limiting.sh)
y [k6/rate-limiting.js](k6/rate-limiting.js).

> [!NOTE]
>
> **Rate limiting en nginx y en Azure.** nginx llama *rate limiting* a
> `limit_req`, y muchos API gateways y proveedores de APIs usan el término en el
> mismo sentido: un límite que el **servicio** aplica a cada cliente. En el
> catálogo de patrones de Azure Architecture Center, que sigue este repositorio,
> eso es [Throttling](https://learn.microsoft.com/en-us/azure/architecture/patterns/throttling),
> en este caso por cliente; y
> [Rate Limiting](https://learn.microsoft.com/en-us/azure/architecture/patterns/rate-limiting-pattern)
> es un patrón del **cliente**, que regula su ritmo para no superar el
> Throttling del servicio al que llama. Por eso el gateway usa la
> configuración de *rate limiting* de nginx para implementar Throttling, y el
> patrón Rate Limiting está en el cliente de la demo.

**Cómo funciona:**

Sin Rate Limiting, el cliente envía todo de golpe y reintenta lo rechazado:

![Envío de un lote sin Rate Limiting](assets/05-rate-limiting-sin.svg)

Con Rate Limiting, el cliente respeta el ritmo que acepta el gateway:

![Envío de un lote con Rate Limiting](assets/05-rate-limiting-con.svg)

El límite del gateway es el mismo en las dos formas: 1 `POST /orders/` por
segundo por IP, con una ráfaga de 5. Lo que cambia es cómo envía el cliente.

**Cómo verlo:** [demos/05-rate-limiting.sh](demos/05-rate-limiting.sh) crea 15
`Orders` de dos formas. Sin Rate Limiting, las envía todas de golpe y reintenta
las rechazadas cada 1 segundo: hacen falta unas 55 solicitudes y unas 40
reciben 429. Con Rate Limiting, respeta el límite del gateway: 15 solicitudes y
ningún 429. Las dos formas demoran unos 10 segundos, porque el límite del
gateway fija el ritmo máximo.

#### Health Endpoint Monitoring

**Problema:** sin una forma de saber si un servicio funciona, una falla solo se
detecta cuando un usuario se queja.

**Implementación:** cada servicio expone `/health`, que verifica sus propias
dependencias y devuelve un JSON con el resultado de cada chequeo -200 si está
sano, 503 en caso contrario-:

* `users-service`: conexión a PostgreSQL.
* `products-service`: *ping* a MongoDB.
* `orders-service`: conexión a MySQL y a RabbitMQ. Si RabbitMQ no está, queda
  `Degraded` y responde 200, porque gracias al [Transactional
  Outbox](#transactional-outbox) sigue aceptando `Orders`. `orders-worker` y
  `orders-projector` quedan `Unhealthy`, porque sin RabbitMQ no reciben nada.

`Orders` **no** chequea a `Users` ni a `Products`: si se cae una dependency,
`Orders` sigue funcionando degradado y no debe figurar como caído. El gateway
publica los tres en `/health/users`, `/health/products` y `/health/orders`.
Docker los consulta cada 5 segundos -`healthcheck` en
[docker-compose.yaml](docker-compose.yaml)- y los usa para no levantar un
servicio hasta que sus dependencias estén sanas -`depends_on: condition:
service_healthy`-.

**Cómo funciona:**

![Quién consulta /health y qué chequea cada servicio](assets/06-health.svg)

`Orders` no chequea a `Users` ni a `Products`: si se caen, sigue funcionando
degradado. Grafana no consulta `/health`: cada servicio publica el resultado de
sus chequeos como la métrica `service_health`.

**Código:**

![Clases de los chequeos de salud](assets/06-health-clases.svg)

Cada chequeo es una implementación de `IHealthCheck`, registrada con
`AddHealthChecks` en el `Program.cs` de cada servicio:

* La clase `RabbitMqHealthCheck`, en [Common/RabbitMq.cs](src/Common/RabbitMq.cs),
  y la clase `MongoHealthCheck`, en
  [ProductsService/MongoHealthCheck.cs](src/ProductsService/MongoHealthCheck.cs),
  implementan el método `CheckHealthAsync`.
* El método `WriteJson` de la clase `HealthResponse`, en
  [Common/HealthResponse.cs](src/Common/HealthResponse.cs), escribe el JSON de
  `/health`.
* El método `PublishAsync` de la clase `HealthMetricsPublisher`, en
  [Common/HealthMetrics.cs](src/Common/HealthMetrics.cs), publica la métrica.

**Cómo verlo:** [demos/06-health.sh](demos/06-health.sh) detiene la base de
datos de `Products`. `/health/products` pasa a 503 con `"MongoDB no responde"`,
`Orders` sigue sano, y a los pocos segundos Docker marca `products-service` como
`unhealthy`. Al levantar la base, todo vuelve a la normalidad. Para verlo en
vivo, corre [demos/monitor-health.sh](demos/monitor-health.sh) en otra terminal.
En Grafana, el panel "Salud de los servicios" muestra `products-service` en rojo
mientras la base está detenida.

> [!TIP]
>
> Consulta [Health checks in ASP.NET
> Core](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/health-checks?view=aspnetcore-10.0)
> para tener más información sobre cómo está implementado el chequeo de salud.

#### Competing Consumers para disponibilidad

**Problema:** un solo proceso que atiende una cola es un cuello de botella y un
punto único de falla.

**Implementación:** `POST /orders/` publica el evento `order.created` en el
exchange `andis.events` de RabbitMQ, a través del [Transactional
Outbox](#transactional-outbox). La cola `orders.process` está suscripta a ese
evento -ver [Publisher/Subscriber](#publishersubscriber)-. Tres réplicas de `orders-worker` consumen la misma
cola; RabbitMQ entrega cada mensaje a una sola réplica. Cada réplica toma un
mensaje a la vez -`prefetch = 1`- y lo confirma -*ack*- recién cuando termina;
si se cae antes, RabbitMQ entrega el mensaje a otra. Si `Users` o `Products` no
están disponibles, el *worker* devuelve el mensaje a la cola y lo reintenta 5
segundos después. Está en la clase `OrderProcessingWorker`, en
[OrdersService/Messaging/OrderProcessingWorker.cs](src/OrdersService/Messaging/OrderProcessingWorker.cs):
el método `ExecuteAsync` se suscribe a la cola y confirma o devuelve cada
mensaje, y el método `ProcessAsync` procesa la `Order`.

**Cómo funciona:**

![Tres réplicas del worker consumen la misma cola](assets/07-competing-consumers.svg)

Cada mensaje lo recibe una sola réplica. Si una réplica se cae antes de
confirmarlo, RabbitMQ se lo entrega a otra. El recorrido completo de una
`Order`, desde el `POST` hasta su confirmación, está en
[ARCHITECTURE.md](ARCHITECTURE.md#creación-de-una-order-asíncrona).

**Código:**

![Clases del worker](assets/07-competing-consumers-clases.svg)

`ProcessAsync` devuelve `false` cuando `Users` o `Products` no responden: el
mensaje vuelve a la cola.

**Cómo verlo:**
[demos/07-competing-consumers.sh](demos/07-competing-consumers.sh) crea 9
`Orders`. Cada *worker* tarda 2 segundos por `Order`, y las 9 se procesan en
unos 6 segundos, 3 por réplica. Después crea 9 más y detiene una réplica
mientras procesa: la `Order` que tenía la termina otra réplica. Para ver la cola
y sus consumidores, abre la consola de RabbitMQ.

> [!TIP]
>
> Consulta [Background tasks with hosted services in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-10.0)
> para tener más información sobre `BackgroundService`, la clase base de
> `OrderProcessingWorker`, `OutboxRelay` y `OrderDetailsProjector`.

#### Event Sourcing

**Problema:** si solo se guarda el estado actual, se pierde la historia de cómo
se llegó a él, y una corrupción de ese estado no tiene vuelta atrás.

**Implementación:** cada cambio de una `Order` se guarda como un evento
inmutable en la tabla `order_events`: `OrderCreated`, `OrderConfirmed` u
`OrderRejected`. La tabla `orders` es una **proyección**: el estado actual,
calculado a partir de los eventos. Cada evento es un `OrderEvent`, creado con
el método `Create` de esa clase, en
[OrdersService/Models/OrderEvent.cs](src/OrdersService/Models/OrderEvent.cs); el
método `Replay` de la clase `OrderProjection`, en
[OrdersService/Models/OrderProjection.cs](src/OrdersService/Models/OrderProjection.cs),
los reproduce para obtener el estado.

* `GET /orders/{id}/events` muestra los eventos de una `Order` y el estado que
  resulta de reproducirlos.
* `POST /orders/projections/rebuild` borra la tabla `orders` y la reconstruye a
  partir de los eventos.

**Cómo funciona:**

![Eventos de una Order y su proyección](assets/08-event-sourcing.svg)

Los eventos no se modifican ni se borran: un cambio nuevo es un evento nuevo.
`orders` se puede borrar y recalcular en cualquier momento.

**Código:**

![Clases de los eventos y la proyección](assets/08-event-sourcing-clases.svg)

**Cómo verlo:** [demos/08-event-sourcing.sh](demos/08-event-sourcing.sh) crea
una `Order`, muestra sus eventos, borra la tabla `orders` directamente en MySQL
y la reconstruye. Las `Orders` vuelven con su estado.

#### Transactional Outbox

Este patrón no está en la lista de la lectura de patrones de disponibilidad; se
agrega porque las [demos de rendimiento y procesamiento
asíncrono](#demos-de-rendimiento-y-procesamiento-asíncrono) dependen de él. Con
eventos que alimentan una vista materializada, un evento perdido deja esa vista
desactualizada para siempre.

**Problema:** un servicio que guarda un cambio en su base y después publica un
mensaje hace dos escrituras en dos sistemas distintos, sin una transacción que
las abarque. Si RabbitMQ no está o el servicio se cae entre una y otra, queda
el cambio sin el mensaje: una `Order` `Pending` que ningún *worker* va a
procesar. Si se publica primero, queda el mensaje de un cambio que tal vez no se
guardó.

**Implementación:** `POST /orders/` guarda la `Order`, su evento `OrderCreated`
de Event Sourcing y el mensaje `order.created` en la tabla `outbox_messages`,
**en la misma transacción**: se guardan los tres o ninguno. El *worker* hace lo
mismo con `order.confirmed` y `order.rejected`. Ninguno de los dos publica en
RabbitMQ. Lo hacen el método `CreateOrder` de la clase `OrdersController` y el
método `ProcessAsync` de la clase `OrderProcessingWorker`, con el método
`Create` de la clase `OutboxMessage`, en
[OrdersService/Models/OutboxMessage.cs](src/OrdersService/Models/OutboxMessage.cs).

El *relay* del *outbox* -el método `PublishPendingAsync` de la clase
`OutboxRelay`, en
[OrdersService/Messaging/OutboxRelay.cs](src/OrdersService/Messaging/OutboxRelay.cs)-
corre en `orders-service`. Cada segundo toma hasta 50 mensajes pendientes, en
orden, los publica en `andis.events` y les completa la fecha de publicación.
Publica con confirmación de RabbitMQ: solo marca un mensaje cuando RabbitMQ
confirmó que lo recibió. Si RabbitMQ no está, los mensajes quedan pendientes y
se reintentan en la vuelta siguiente. Los toma con `SELECT ... FOR UPDATE SKIP
LOCKED`: con más de una instancia del *relay*, cada mensaje lo publica una sola.

La entrega es **al menos una vez**: si el *relay* se cae después de publicar y
antes de marcar, publica de nuevo el mismo mensaje. Por eso los suscriptores son
idempotentes: el *worker* ignora las `Orders` que ya no están `Pending`, y el
proyector de la vista materializada actualiza la fila si ya existe.

`Users` y `Products` publican sus eventos directamente, sin *outbox*: si
RabbitMQ no está, el cambio se guarda y el evento se pierde. `Products` usa
MongoDB sin réplicas, que no admite transacciones con varios documentos. Quedó
así a propósito, para comparar; ver las decisiones abiertas en
[ARCHITECTURE.md](ARCHITECTURE.md#decisiones-abiertas-a-discusión).

**Cómo funciona:**

![Secuencia del Transactional Outbox](assets/09-transactional-outbox.svg)

El *worker* guarda `order.confirmed` u `order.rejected` en el *outbox* de la
misma forma. El recorrido completo está en
[ARCHITECTURE.md](ARCHITECTURE.md#creación-de-una-order-asíncrona).

**Código:**

![Clases del Transactional Outbox](assets/09-transactional-outbox-clases.svg)

`PublishedAt` es nulo mientras el mensaje está pendiente.

**Cómo verlo:** [demos/09-transactional-outbox.sh](demos/09-transactional-outbox.sh)
detiene RabbitMQ y crea 5 `Orders`. Todas responden `202`; antes del *outbox*,
`Orders` respondía `503` y la `Order` quedaba `Pending` para siempre.
`/health/orders` responde `Degraded`, y los 5 mensajes esperan en
`outbox_messages`. Al levantar RabbitMQ, el *relay* los publica, los *workers*
confirman las `Orders` y el *outbox* queda vacío. En Grafana, el panel "Outbox
de Orders" muestra los pendientes subir y volver a cero.

> [!TIP]
>
> Consulta [Transactions - EF Core](https://learn.microsoft.com/en-us/ef/core/saving/transactions)
> para tener más información sobre la transacción que guarda la `Order`, su
> evento y el mensaje, y [SQL Queries - EF Core](https://learn.microsoft.com/en-us/ef/core/querying/sql-queries)
> sobre `FromSqlRaw`, con el que el *relay* lee los mensajes pendientes.

## Demos de rendimiento y procesamiento asíncrono

**Estado: implementada en este repositorio, sobre las demos de resiliencia y
disponibilidad.**

Escenario: un pico de tráfico en una campaña. La aplicación debe mantener
latencias bajas y absorber la carga.

Qué se muestra:

* Lecturas rápidas con caché y vistas materializadas.
* Creación de `Orders` encolada, con consumidores en competencia.
* Eventos pub/sub para desacoplar los servicios.

Resultado observable: latencias de lectura estables durante el pico, *backlog*
absorbido por la cola y throughput que crece al agregar *workers*. El pico se
genera con [k6/load.js](k6/load.js) y [k6/load-leveling.js](k6/load-leveling.js),
y se compara antes y después de cada patrón en el dashboard de Grafana, en la
fila "Rendimiento: caché, CQRS y outbox".

### Qué agrega a las demos de disponibilidad

| Componente | Para qué |
| ---------- | -------- |
| `redis` | Caché de `Products`. |
| `orders-projector` | La misma imagen que `orders-service`, con el proyector habilitado -`Projector__Enabled=true`-. Mantiene la vista materializada `order_details`. |
| Exchange `andis.events` | Eventos `order.created`, `order.confirmed`, `order.rejected`, `user.updated` y `product.updated`. Los contratos están en [Common/Events.cs](src/Common/Events.cs). |
| Modificación de `Users` y `Products` | Operación SOAP `UpdateUser` y `PUT /products/{id}`, que publican `user.updated` y `product.updated`. |
| Ajustes de la caché | `PUT /products/cache` prende o apaga la caché y agrega una demora a cada lectura de MongoDB, sin reiniciar, igual que el chaos. `DELETE /products/cache` vuelve a los valores por defecto. |

```bash
# Caché apagada y cada lectura de MongoDB demora 200 ms
curl -X PUT http://localhost/products/cache -H "Content-Type: application/json" \
  -d '{"enabled": false, "dbDelayMs": 200}'

# Vuelve a los valores por defecto: caché prendida y sin demora
curl -X DELETE http://localhost/products/cache
```

### Patrones de rendimiento

Como en las demos de disponibilidad, cada patrón tiene un script en
[demos/](demos), del `10` al `16`. Los *logs* de Docker muestran los eventos con
una etiqueta: `[CACHE-ASIDE]` en `products-service`; `[PUB/SUB]` en
`users-service`, `products-service` y `orders-projector`; y `[MATERIALIZED
VIEW]` en `orders-projector`.

#### Cache-Aside

**Problema:** las lecturas de datos que cambian poco van a la base cada vez, y
la base se vuelve el cuello de botella de un pico de lecturas.

**Implementación:** `Products` busca primero en Redis. Si el dato no está
-*miss*-, lo lee de MongoDB y lo guarda en Redis por 60 segundos; la próxima
lectura sale de Redis -*hit*-. Al crear o cambiar un `Product`, `Products`
borra de Redis las claves afectadas, y la próxima lectura trae el dato nuevo.
Es la aplicación la que maneja la caché: Redis no sabe nada de MongoDB. Si Redis
no responde, `Products` lee MongoDB directamente; la caché acelera, pero no es
una dependencia, y por eso no entra en `/health`. Está en los métodos
`GetOrLoadAsync` e `InvalidateAsync` de la clase `ProductCache`, en
[ProductsService/ProductCache.cs](src/ProductsService/ProductCache.cs), que usan los
métodos `GetProduct` y `UpdateProduct` de la clase `ProductsController`, en
[ProductsService/Controllers/ProductsController.cs](src/ProductsService/Controllers/ProductsController.cs).

Se borra la clave en lugar de escribir el valor nuevo: así la caché nunca guarda
un valor que MongoDB no tiene. El TTL acota cuánto puede durar un dato
desactualizado si una invalidación falla.

**Cómo funciona:**

![Secuencia de una lectura y una invalidación con Cache-Aside](assets/10-cache-aside.svg)

Si Redis no responde, `Products` lee directamente de MongoDB.

**Código:**

![Clases de la caché de Products](assets/10-cache-aside-clases.svg)

`ProductCache` no conoce a MongoDB: su método `GetOrLoadAsync` recibe la
lectura como una función, `load`.

**Cómo verlo:** [demos/10-cache-aside.sh](demos/10-cache-aside.sh) simula una
base que demora 200 ms y lee el mismo `Product` varias veces: la primera lectura
demora 200 ms y las siguientes, 2 o 3 ms. Después cambia su nombre: la lectura
siguiente es un *miss* y trae el nombre nuevo. Con Redis detenido, `Products`
sigue respondiendo, a la velocidad de MongoDB. Bajo carga,
[k6/cache-aside.js](k6/cache-aside.js) lee 50 veces por segundo con la caché
apagada y después prendida: la p95 baja de unos 100 ms a unos 3 ms.

> [!TIP]
>
> Consulta [Caching Guidance](https://learn.microsoft.com/en-us/azure/architecture/best-practices/caching)
> para tener más información sobre cuándo conviene una caché, cuánto tiempo
> guardar cada dato y cómo mantenerla consistente con la base.

#### Queue-Based Load Leveling

**Problema:** un pico de solicitudes que un servicio procesa en el momento lo
satura, y satura también a los servicios que llama.

**Implementación:** `POST /orders/` solo guarda la `Order` y responde; el
trabajo pesado -verificar el `User` y el `Product` con `Users` y `Products`- lo
hacen los *workers* desde la cola `orders.process`, a su propio ritmo. La cola
absorbe el pico: `Users` y `Products` reciben como máximo una llamada por
réplica del *worker*, sin importar cuántas `Orders` lleguen. Ya estaba en las
demos de disponibilidad, como base de [Competing
Consumers](#competing-consumers-para-disponibilidad); las demos de rendimiento lo
muestra bajo un pico.

**Cómo funciona:**

![La cola absorbe un pico de Orders](assets/11-queue-based-load-leveling.svg)

El pico se acumula en la cola, no en `Users` ni en `Products`. El código es el
de [Competing Consumers](#competing-consumers-para-disponibilidad).

**Cómo verlo:** [demos/11-queue-based-load-leveling.sh](demos/11-queue-based-load-leveling.sh)
crea 30 `Orders` a la vez, directamente en `orders-service`: el Throttling por
cliente del gateway rechazaría el pico antes de que llegara a la cola. Todas
responden `202` en pocos milisegundos; la cola baja de a 3 mensajes cada 2
segundos hasta vaciarse. [k6/load-leveling.js](k6/load-leveling.js) envía 10
`POST` por segundo durante 10 segundos: la latencia de `create order` se
mantiene baja y la cola crece durante el pico y se vacía después.

#### Competing Consumers para rendimiento

**Problema:** un solo consumidor procesa la cola a un ritmo fijo; si el pico
dura mucho, la cola crece sin límite.

**Implementación:** la misma de la [demo de
disponibilidad](#competing-consumers-para-disponibilidad). Visto como patrón de
rendimiento, el throughput crece agregando réplicas de `orders-worker`, sin
cambiar el código.

**Cómo funciona:** el mismo diagrama de la [demo de
disponibilidad](#competing-consumers-para-disponibilidad), con 6 réplicas en
lugar de 3.

**Cómo verlo:**
[demos/12-competing-consumers-scaling.sh](demos/12-competing-consumers-scaling.sh)
procesa 30 `Orders` con 3 réplicas, unos 21 segundos, y otras 30 con 6 réplicas,
unos 12 segundos. Al final vuelve a 3 réplicas.

#### Publisher/Subscriber

**Problema:** si un servicio avisa de sus cambios llamando a cada interesado,
tiene que conocerlos a todos, y cada interesado nuevo obliga a cambiarlo.

**Implementación:** `Orders`, `Users` y `Products` publican eventos en el
exchange `andis.events`, de tipo *topic*. En RabbitMQ, un exchange y una cola
son piezas distintas:

* Un **exchange** es donde se publica. No guarda mensajes: según la clave de
  ruteo de cada mensaje, por ejemplo `order.created`, pone una copia en cada
  cola enlazada con esa clave. Si ninguna cola está enlazada, el mensaje se
  descarta. En un exchange de tipo *topic*, las claves de los enlaces admiten
  comodines: `order.*` recibe `order.created`, `order.confirmed` y
  `order.rejected`.
* Una **cola** es donde los mensajes esperan hasta que un consumidor los toma.
  Cada mensaje de una cola lo procesa un solo consumidor.

Cada suscriptor tiene su propia cola y la enlaza con las claves de ruteo que le
interesan:

| Cola | Suscriptor | Claves |
| ---- | ---------- | ------ |
| `orders.process` | `orders-worker` | `order.created` |
| `orders.details-view` | `orders-projector` | `order.*`, `user.updated`, `product.updated` |

Un mismo `order.created` llega a las dos colas. `Orders` no sabe que existe el
proyector: agregarlo no cambió el código que publica. La conexión y la
publicación están en la clase `RabbitMq`, en [Common/RabbitMq.cs](src/Common/RabbitMq.cs):
el método `PublishEventAsync` publica un evento, y el método
`DeclareSubscriberQueueAsync` declara la cola de un suscriptor con sus claves.

El exchange y las colas corresponden a dos patrones distintos. Entre colas,
Publisher/Subscriber: cada suscriptor recibe su propia copia de cada evento.
Dentro de una cola, [Competing
Consumers](#competing-consumers-para-rendimiento): las réplicas de
`orders-worker` comparten `orders.process`, y cada mensaje lo procesa una sola.

**Cómo funciona:**

![Quién publica cada evento y quién se
suscribe](assets/13-publisher-subscriber.svg)

Quien publica no conoce las colas: cada suscriptor declara la suya y elige las
claves de ruteo.

**Código:**

![Clases de Publisher/Subscriber](assets/13-publisher-subscriber-clases.svg)

Los contratos de los eventos y la conexión a RabbitMQ están en `Common`, el
único proyecto que comparten los servicios. Los registros `OrderConfirmed` y
`OrderRejected` también están ahí; el diagrama no los muestra.

**Cómo verlo:**
[demos/13-publisher-subscriber.sh](demos/13-publisher-subscriber.sh) muestra los
enlaces del exchange, crea una `Order` y muestra los *logs* del *worker* y del
proyector, que la reciben los dos. Después cambia el nombre de un `Product` y de
un `User`: esos eventos solo los recibe el proyector.

#### Asynchronous Request-Reply

**Problema:** una operación que tarda no puede tener al cliente esperando la
respuesta: el cliente agota su *timeout* o retiene una conexión que no hace
nada.

**Implementación:** `POST /orders/` responde `202 Accepted` enseguida, con
`Location: /orders/{id}/status` y `Retry-After: 2`. `GET /orders/{id}/status`
responde `200` con el estado mientras la `Order` está `Pending`, también con
`Retry-After`; cuando un *worker* la confirma o la rechaza, responde `303 See
Other` a `/orders/{id}`, y el cliente sigue la redirección para obtener el
resultado. Está en los métodos `CreateOrder` y `GetOrderStatus` de la clase
`OrdersController`, en
[OrdersService/Controllers/OrdersController.cs](src/OrdersService/Controllers/OrdersController.cs).

**Cómo funciona:**

![Secuencia de Asynchronous Request-Reply](assets/14-asynchronous-request-reply.svg)

**Cómo verlo:** [demos/14-asynchronous-request-reply.sh](demos/14-asynchronous-request-reply.sh)
crea una `Order`, muestra el `202` con sus encabezados y consulta el estado
respetando `Retry-After` hasta el `303`. Al final sigue la redirección y muestra
la `Order` confirmada.

> [!TIP]
>
> Consulta [Controller action return types in ASP.NET Core web API](https://learn.microsoft.com/en-us/aspnet/core/web-api/action-return-types?view=aspnetcore-10.0)
> para tener más información sobre los resultados que devuelven `CreateOrder`
> y `GetOrderStatus`, como `Accepted`.

#### CQRS

**Problema:** el modelo que conviene para escribir -tablas normalizadas, cada
dato en el servicio que es su dueño- no es el que conviene para leer. La
consulta de detalle tiene que juntar datos de tres servicios en cada lectura.

**Implementación:** los comandos y las consultas usan modelos distintos.

* **Comandos:** `POST /orders/` y el *worker* escriben `orders`, `order_events`
  y `outbox_messages`, en el método `CreateOrder` de la clase
  `OrdersController`, en
  [OrdersService/Controllers/OrdersController.cs](src/OrdersService/Controllers/OrdersController.cs),
  y en el método `ProcessAsync` de la clase `OrderProcessingWorker`, en
  [OrdersService/Messaging/OrderProcessingWorker.cs](src/OrdersService/Messaging/OrderProcessingWorker.cs).
* **Consultas:** `GET /orders/user/{userId}/details/view` lee solo la vista
  `order_details` -ver [Materialized View](#materialized-view)-, en el método
  `GetOrdersForUser` de la clase `OrdersQueriesController`, en
  [OrdersService/Controllers/OrdersQueriesController.cs](src/OrdersService/Controllers/OrdersQueriesController.cs).
  Es una consulta a MySQL, sin llamadas a `Users` ni a `Products`, y sin
  Throttling.

`GET /orders/user/{userId}/details`, la consulta síncrona de la demo de
disponibilidad, queda como está: la usan los demos de Retry, Circuit Breaker,
Bulkhead y Throttling, y sirve para comparar. El costo es la **consistencia
eventual**: el lado de las consultas se actualiza un momento después que el de
los comandos.

**Cómo funciona:**

![Comandos y consultas con modelos separados](assets/15-cqrs.svg)

La flecha de los eventos es la consistencia eventual: el modelo de lectura se
actualiza un momento después. El recorrido completo de una consulta desde la
vista está en
[ARCHITECTURE.md](ARCHITECTURE.md#consulta-de-detalle-desde-la-vista-eventos).

**Código:**

![Clases de los comandos y las consultas](assets/15-cqrs-clases.svg)

La separación se ve en el código: un controlador para los comandos, sobre
`Order`, y otro para las consultas, sobre `OrderDetails`.

**Cómo verlo:** [demos/15-cqrs.sh](demos/15-cqrs.sh) hace la misma consulta por
los dos lados. Sin fallas, la síncrona demora unos 100 ms y la vista, 3 ms. Con
`Products` demorando 300 ms, la síncrona pasa a casi 2 segundos -una llamada por
`Order`- y la vista no cambia. Después crea una `Order` y consulta enseguida: la
síncrona ya la tiene, la vista todavía no. Bajo carga, [k6/load.js](k6/load.js)
con `-e DETAILS=live` y con `-e DETAILS=view` compara las dos.

#### Materialized View

**Problema:** armar el detalle de las `Orders` en cada consulta cuesta una
llamada a `Users` y una a `Products` por `Order`, y si alguno no responde, el
detalle sale degradado.

**Implementación:** la tabla `order_details` guarda el detalle de cada `Order`
con el nombre del cliente y el nombre del producto ya copiados. La mantiene el
método `ApplyAsync` de la clase `OrderDetailsProjector`, en
[OrdersService/Messaging/OrderDetailsProjector.cs](src/OrdersService/Messaging/OrderDetailsProjector.cs),
que corre en `orders-projector` y consume la cola `orders.details-view`:

* `order.created`: el método `AddOrderAsync` busca el nombre del cliente y del
  producto **una vez**, con los mismos `HttpClient` y la misma resiliencia de la
  demo de disponibilidad, y agrega la fila. Si `Users` o `Products` no
  responden, el mensaje vuelve a la cola.
* `order.confirmed` y `order.rejected`: el método `SetStatusAsync` actualiza el
  estado.
* `user.updated` y `product.updated`: actualizan el nombre en todas las filas
  de ese `User` o `Product`.

Corre con una sola réplica, para que los eventos de una misma `Order` se
apliquen en orden.

**Cómo funciona:**

![Secuencia de la vista materializada](assets/16-materialized-view.svg)

**Código:**

![Clases del proyector de la vista](assets/16-materialized-view-clases.svg)

El proyector es el único que escribe la vista, y el único que llama a `Users` y
a `Products`.

**Cómo verlo:** [demos/16-materialized-view.sh](demos/16-materialized-view.sh)
cambia el nombre de un `Product` y la vista lo refleja. Con `Products` fallando
siempre, la consulta síncrona responde "no disponible" y la vista sigue
respondiendo con los nombres. Por último, detiene el proyector y crea una
`Order`: la `Order` se confirma, pero la vista no la tiene y sus eventos esperan
en la cola. Al levantar el proyector, la vista se pone al día.

> [!TIP]
>
> Consulta [ExecuteUpdate and ExecuteDelete - EF Core](https://learn.microsoft.com/en-us/ef/core/saving/execute-insert-update-delete)
> para tener más información sobre `ExecuteUpdateAsync`, con el que el
> proyector cambia un nombre en todas las filas de la vista con una sola
> sentencia.

## Demos de seguridad

**Estado: implementada en este repositorio, sobre las demos de rendimiento.**

Escenario: la aplicación se abre a clientes reales. Cada usuario tiene que
identificarse, ver solo lo suyo y no poder hacer más de lo que le corresponde,
y nada tiene que llegar a los servicios sin control.

Qué se muestra:

* Inicio de sesión en un proveedor de identidad externo, y decisiones tomadas
  con los claims del token.
* TLS, validación de tokens y compresión en el gateway, sin código en los
  servicios.
* Validación y saneamiento de cada solicitud antes de que llegue a un servicio,
  y servicios que solo se alcanzan a través del gateway.
* Un proxy junto a `Products` que le agrega autenticación y autorización sin
  cambiar su código.
* Acceso directo, temporal y acotado al almacenamiento de las imágenes.

Resultado observable: por la entrada HTTP del gateway, cualquiera llega a
cualquier ruta, incluidas las de administración; por la entrada HTTPS, cada
solicitud necesita un token, los servicios no implementan autenticación ni TLS,
las políticas se aplican en el borde y cada acceso tiene el mínimo privilegio
necesario.

### Qué agrega a las demos de rendimiento

| Componente | Para qué |
| ---------- | -------- |
| `keycloak` | Proveedor de identidad. El realm `andis` tiene los usuarios, sus roles y el cliente `andis-cli`, la aplicación de línea de comandos de las demos; se importa al arrancar desde [Keycloak/andis-realm.json](src/Keycloak/andis-realm.json). Consola en <http://localhost:8080> -usr: `admin` pwd: `Pa55w0rd`-. |
| Entrada HTTPS del gateway | `https://localhost`, con los patrones de seguridad. La entrada HTTP, `http://localhost`, queda como estaba, para las demos anteriores y para comparar; en producción quedaría solo la HTTPS. Está en [ApiGateway/nginxconf/nginx.conf](src/ApiGateway/nginxconf/nginx.conf). |
| `products-sidecar` | Envoy, junto a `products-service` y en su mismo espacio de red. La entrada HTTPS le envía `/products/` al sidecar; la HTTP, directamente a `Products`. Está en [ProductsSidecar/envoy.yaml](src/ProductsSidecar/envoy.yaml). |
| `azurite` | El emulador de Azure Storage, con las imágenes de los `Products`, en <http://localhost:10000>. |
| Imágenes de los `Products` | `PUT /products/{id}/image` sube la imagen a través de `Products`; `POST /products/{id}/image/upload-url` entrega una URL firmada para subirla directamente a Azurite; `GET /products/{id}/image` redirige a una URL firmada para bajarla. |
| [docker-compose.gatekeeper.yaml](docker-compose.gatekeeper.yaml) | Deja de publicar en el host los puertos de los servicios y de las bases: la única entrada a los servicios es el gateway. |

Los usuarios de Keycloak, todos con la contraseña `Pa55w0rd`:

| Usuario | Rol | `User` de la aplicación | Qué puede hacer |
| ------- | --- | ----------------------- | --------------- |
| alice | `customer` | 1 | Crear y consultar sus propias `Orders`; leer los `Products` |
| bob | `customer` | 2 | Lo mismo que alice, con sus `Orders` |
| carol | `admin` | — | Consultar las `Orders` de todos; cambiar los `Products` y sus imágenes. No es cliente: no crea `Orders` |

Ejemplo de una solicitud por la entrada HTTPS:

```bash
# El certificado del gateway es autofirmado: se copia del contenedor para que
# curl lo verifique
docker compose exec -T api-gateway cat /etc/nginx/tls/gateway.crt > gateway.crt

# alice inicia sesión en Keycloak y recibe un token
alice=$(curl -s -X POST http://localhost:8080/realms/andis/protocol/openid-connect/token \
  -d grant_type=password -d client_id=andis-cli -d scope=openid \
  -d username=alice -d password=Pa55w0rd | jq -r .access_token)

# Las Orders de alice, con el token
curl --cacert gateway.crt -H "Authorization: Bearer $alice" \
  https://localhost/orders/user/1/details/view
```

### Patrones de seguridad

Cada patrón tiene un script en [demos/](demos), del `17` al `21`, y un paso a
paso en [commands/04-security.azcli](commands/04-security.azcli) que compara la
misma solicitud por la entrada HTTP, sin el patrón, y por la HTTPS, con él. Los
*logs* de Docker muestran los eventos con una etiqueta: `[HTTPS]` en
`api-gateway`, una línea por solicitud; `[FEDERATED IDENTITY]` en
`orders-service`; `[SIDECAR]` en `products-sidecar`; y `[VALET KEY]` en
`products-service`.

Los patrones están en el orden en que se apoyan unos en otros, que no es el de
la lectura: Gateway Offloading y Gatekeeper usan los tokens de Federated
Identity.

#### Federated Identity

**Problema:** si cada aplicación autentica a sus usuarios, cada una guarda
contraseñas e implementa el inicio de sesión, la recuperación de contraseñas y
el bloqueo de cuentas, y el usuario tiene una cuenta distinta en cada una. Una
falla en cualquiera de ellas expone las contraseñas.

**Implementación:** la autenticación se delega en Keycloak, un proveedor de
identidad que habla OpenID Connect. El usuario inicia sesión en Keycloak, que
emite un token firmado -un JWT- con sus claims:

* `preferred_username`: el nombre de usuario, por ejemplo `alice`.
* `user_id`: el `User` de la aplicación. Keycloak lo agrega a partir de un
  atributo del usuario, y así vincula la identidad externa con los datos de la
  aplicación.
* `realm_access.roles`: los roles, `customer` o `admin`.

La aplicación no guarda contraseñas: la tabla `users` tiene solo el nombre y el
correo. Confía en la firma de Keycloak y usa los claims:

* El gateway valida el token en cada solicitud por la entrada HTTPS y les pasa
  los claims a los servicios en los encabezados `X-User-Name`, `X-User-Id` y
  `X-User-Roles` -ver [Gateway Offloading](#gateway-offloading)-.
* `Orders` decide con esos claims. Un cliente crea `Orders` solo para él y
  consulta solo las suyas; un admin consulta las de todos, pero no crea
  `Orders`, porque no es cliente. Lo hace con el registro `CallerIdentity`, en
  [OrdersService/CallerIdentity.cs](src/OrdersService/CallerIdentity.cs), que
  usan los métodos `CreateOrder`, `ListOrders`, `GetOrder`, `GetOrderStatus`,
  `GetOrderEvents` y `GetOrdersForUser` de la clase `OrdersController`, y el
  método `GetOrdersForUser` de la clase `OrdersQueriesController`.
* Una solicitud sin esos encabezados, por la entrada HTTP, es anónima, y
  `Orders` se comporta como en las demos anteriores.

El cliente `andis-cli` admite dos formas de iniciar sesión. Con el flujo de
dispositivo -*device authorization grant*-, el que usan las aplicaciones de
línea de comandos y los televisores, el usuario inicia sesión en el navegador,
en la página de Keycloak, y la aplicación nunca ve la contraseña: es el que usa
el paso a paso. Con el *grant type* `password`, la aplicación recibe el usuario
y la contraseña y se los pasa a Keycloak: es más corto, y lo usan los scripts,
pero no se usaría en una aplicación real.

**Cómo funciona:**

![Inicio de sesión con el flujo de dispositivo y una solicitud con el
token](assets/17-federated-identity.svg)

El token dura 15 minutos. Keycloak no interviene en cada decisión: `Orders`
decide con los claims que trae el token.

**Código:**

![Clases de la identidad en Orders](assets/17-federated-identity-clases.svg)

`Orders` no conoce a Keycloak ni valida tokens: lee los encabezados que arma el
gateway. Esa confianza depende del [Gatekeeper](#gatekeeper): si alguien llega
a `Orders` sin pasar por el gateway, puede mandar esos encabezados con
cualquier valor.

**Cómo verlo:**
[demos/17-federated-identity.sh](demos/17-federated-identity.sh) crea una
`Order` para el `User` 2 por la entrada HTTP, sin identificarse. Después bob
inicia sesión y, por la entrada HTTPS, crea una `Order` sin indicar el `User`:
es de bob; si indica el `User` 1, `Orders` responde 403. bob consulta sus
`Orders` y no las de alice, y alice, al revés; carol consulta las de los dos,
pero no puede crear. Sin token, o con el `user_id` del token cambiado por el de
alice, el gateway responde 401, porque la firma ya no corresponde a los claims.
Como en las demás demos, las `Orders` se crean solo para el `User` 2: las del
`User` 1 son la base de las demos de disponibilidad. En Grafana, el panel
"Eventos de Keycloak por segundo" muestra los inicios de sesión.

> [!TIP]
>
> Consulta [Securing applications with OpenID
> Connect](https://www.keycloak.org/securing-apps/oidc-layers) para tener más
> información sobre los flujos que ofrece Keycloak, y [RFC 8628, OAuth 2.0
> Device Authorization Grant](https://datatracker.ietf.org/doc/html/rfc8628)
> sobre el flujo de dispositivo.

#### Gateway Offloading

**Problema:** TLS, la validación de tokens y la compresión son funciones
especializadas, que necesitan configuración y certificados. Si cada servicio
las implementa, cada uno lo hace a su manera, en su lenguaje, y actualizar un
certificado o una política obliga a cambiar todos los servicios.

**Implementación:** el gateway se hace cargo de esas funciones en la entrada
HTTPS, en [ApiGateway/nginxconf/nginx.conf](src/ApiGateway/nginxconf/nginx.conf):

* **TLS:** el gateway termina TLS con su certificado; los servicios siguen
  hablando HTTP dentro de la red de Docker, sin certificados. El certificado es
  autofirmado y se genera al construir la imagen, en
  [ApiGateway/Dockerfile](src/ApiGateway/Dockerfile), para no guardar una clave
  privada en el repositorio; en producción lo emitiría una autoridad de
  certificación.
* **Validación de tokens:** antes de reenviar cada solicitud, `auth_request`
  hace una subsolicitud a `/userinfo` de Keycloak con el token. Si Keycloak
  responde 200, la solicitud sigue; si no, el gateway responde 401 y la
  solicitud no llega al servicio. Después, `identity.js`, en
  [ApiGateway/nginxconf/identity.js](src/ApiGateway/nginxconf/identity.js), lee
  los claims del token, ya validado, y el gateway se los pasa al servicio en los
  encabezados `X-User-Name`, `X-User-Id` y `X-User-Roles`.
* **Compresión:** el gateway comprime con gzip las respuestas de más de 1 KB,
  si el cliente lo acepta.
* **Throttling por cliente:** el de la [demo de Throttling](#throttling), que
  el gateway ya aplicaba en la entrada HTTP.

El costo es que el gateway le pregunta a Keycloak por cada solicitud: si
Keycloak no responde, nadie entra. El [sidecar](#sidecar) muestra la
alternativa: verificar la firma del token con las claves públicas de Keycloak,
sin preguntarle cada vez, a cambio de no enterarse si se cerró la sesión.

**Cómo funciona:**

![Funciones que el gateway les quita a los
servicios](assets/18-gateway-offloading.svg)

**Código:** la validación del token, en
[ApiGateway/nginxconf/nginx.conf](src/ApiGateway/nginxconf/nginx.conf):

```nginx
auth_request /_auth;

location = /_auth {
    internal;
    proxy_pass http://$keycloak/realms/andis/protocol/openid-connect/userinfo;
    proxy_pass_request_body off;
    proxy_set_header Content-Length "";
    proxy_set_header Authorization $http_authorization;
}

js_set $user_id identity.userId;
proxy_set_header X-User-Id $user_id;
```

**Cómo verlo:**
[demos/18-gateway-offloading.sh](demos/18-gateway-offloading.sh) muestra que
`Orders` no habla TLS y que el gateway sí, con su certificado. Sin token, el
gateway responde 401, y su *log* muestra que la solicitud no llegó a ningún
servicio; con token, la reenvía. La lista de `Orders` llega unas 15 veces más
chica por HTTPS que por HTTP, comprimida por el gateway. En Grafana, el panel
"Eventos de Keycloak por segundo" muestra una validación de token,
`user_info_request`, por cada solicitud HTTPS.

> [!TIP]
>
> Consulta [Authentication Based on Subrequest
> Result](https://docs.nginx.com/nginx/admin-guide/security-controls/configuring-subrequest-authentication/)
> para tener más información sobre `auth_request`, y la [documentación de
> njs](https://nginx.org/en/docs/njs/) sobre el JavaScript de nginx que usa
> `identity.js`.

#### Gatekeeper

**Problema:** si los servicios reciben las solicitudes de los clientes
directamente, cada uno tiene que defenderse de todo lo que le pueda llegar:
rutas de administración, métodos inesperados, cuerpos enormes o mal formados,
encabezados falsos. Una falla en cualquiera de esas defensas deja expuesto al
servicio y a sus datos.

**Implementación:** en la entrada HTTPS, el gateway es el intermediario entre
los clientes y los servicios, y valida y sanea cada solicitud antes de
reenviarla, en [ApiGateway/nginxconf/nginx.conf](src/ApiGateway/nginxconf/nginx.conf):

* **Rutas:** publica solo las de la aplicación. Las de administración
  -`/chaos`, `/patterns`, `/cache`, `/orders/projections/rebuild`- y cualquier
  otra responden 404 sin llegar a los servicios.
* **Ids:** el de un `Product` tiene 24 caracteres hexadecimales, y el de una
  `Order` o un `User` es un número; si no, 404.
* **Métodos:** cada ruta acepta solo los que usa la aplicación; un `DELETE`
  responde 403.
* **Cuerpo:** los `POST` y `PUT` tienen que ser JSON -415 si no- y medir hasta
  64 KB -413 si no-.
* **Encabezados de identidad:** el gateway arma `X-User-Name`, `X-User-Id` y
  `X-User-Roles` con los claims del token y reemplaza los que haya mandado el
  cliente. Un cliente no se puede hacer pasar por otro.

El gateway no tiene credenciales de las bases ni acceso a los datos: si alguien
lo compromete, no obtiene más que lo que ya podía pedir a través de él.

Para que sirva, los servicios no tienen que poder alcanzarse sin pasar por el
gateway. [docker-compose.yaml](docker-compose.yaml) publica en el host el
puerto de cada servicio y de cada base, para las demos anteriores; por ahí,
cualquiera llega a la administración o manda encabezados de identidad falsos.
[docker-compose.gatekeeper.yaml](docker-compose.gatekeeper.yaml) deja de
publicarlos: los servicios siguen hablando entre sí por la red de Docker, pero
desde afuera la única entrada es el gateway. Se aplica junto con el otro
archivo, y Docker Compose vuelve a crear solo los contenedores que cambian:

```bash
docker compose -f docker-compose.yaml -f docker-compose.gatekeeper.yaml up -d --wait
```

**Cómo funciona:**

![Controles del gateway antes de reenviar una
solicitud](assets/19-gatekeeper.svg)

**Código:** las rutas de `Orders` y el saneamiento de los encabezados, en
[ApiGateway/nginxconf/nginx.conf](src/ApiGateway/nginxconf/nginx.conf):

```nginx
proxy_set_header X-User-Id    $user_id;
proxy_set_header X-User-Name  $user_name;
proxy_set_header X-User-Roles $user_roles;

client_max_body_size 64k;

location ~ ^/orders/(\d+(/status|/events)?|user/\d+/details(/view)?)?$ {
    limit_except GET POST { deny all; }
    if ($body_not_json) {
        return 415 '{"detail": "Gatekeeper: el cuerpo tiene que ser JSON"}\n';
    }
    proxy_pass http://$orders_service;
}

location / {
    return 404 '{"detail": "Gatekeeper: ruta no publicada"}\n';
}
```

**Cómo verlo:** [demos/19-gatekeeper.sh](demos/19-gatekeeper.sh) compara las
dos entradas. Por HTTP, las rutas de administración responden 200, y los
métodos, los cuerpos y los ids inválidos llegan a `Orders`, que los rechaza
después de recibirlos. Por HTTPS, el gateway los rechaza sin llegar a los
servicios. alice no puede hacerse pasar por bob con encabezados falsos por
HTTPS, pero sí por el puerto 8000, sin token. Al aplicar
`docker-compose.gatekeeper.yaml`, el puerto 8000 deja de responder, y la
entrada HTTPS sigue funcionando. Al terminar, el script vuelve a publicar los
puertos. En Grafana, el panel "Solicitudes por segundo, por servicio y código
HTTP" muestra los rechazos de `Orders` por HTTP; los de HTTPS no aparecen,
porque no llegaron a `Orders`.

> [!TIP]
>
> Consulta [Module ngx_http_core_module](https://nginx.org/en/docs/http/ngx_http_core_module.html)
> para tener más información sobre `location`, `limit_except` y
> `client_max_body_size`, y [Merge Compose
> files](https://docs.docker.com/reference/compose-file/merge/) sobre cómo
> `docker-compose.gatekeeper.yaml` quita los puertos con `!reset`.

#### Sidecar

**Problema:** agregarle a un servicio funciones transversales -autenticación,
autorización, registro de accesos- obliga a cambiar su código, en su lenguaje,
y a repetirlo en cada servicio; y un servicio de terceros o legado tal vez no
se pueda cambiar. Además, el gateway protege solo lo que entra por el gateway:
dentro de la red, cualquier contenedor llama a cualquier servicio.

**Implementación:** un proxy Envoy, `products-sidecar`, corre junto a
`products-service`, en su mismo espacio de red -`network_mode:
service:products-service` en [docker-compose.yaml](docker-compose.yaml)-. Para
Envoy, `Products` es `127.0.0.1:8000`; para los demás contenedores, el sidecar
es `products-service:9000`. Envoy es otra tecnología, escrita en C++, y se
configura con [ProductsSidecar/envoy.yaml](src/ProductsSidecar/envoy.yaml):

* **Autenticación:** el filtro `jwt_authn` verifica la firma del token con las
  claves públicas de Keycloak, que pide una vez cada 5 minutos, además del
  emisor y el vencimiento. Un token inválido recibe 401; sin token, la
  solicitud sigue como anónima.
* **Autorización:** el filtro `rbac` deja leer a cualquiera -`GET`- y exige el
  rol `admin` para todo lo demás. Si no, 403.
* **Registro de accesos:** una línea `[SIDECAR]` por solicitud, con el código,
  el usuario del token y el motivo del rechazo.

`Products` no cambió: `ProductsController` no verifica roles. La entrada HTTPS
del gateway le envía `/products/` al sidecar, y la HTTP, directamente a
`Products`, para comparar. En producción, `Products` escucharía solo en
`127.0.0.1` y el sidecar sería su única entrada; acá sigue escuchando en el
puerto 8000 para las demos anteriores.

Una *service mesh*, como Istio o Linkerd, pone un sidecar como este junto a
cada servicio, y agrega TLS mutuo entre los sidecars; acá hay uno solo, sin TLS
mutuo.

**Cómo funciona:**

![El sidecar de Products y quién lo llama](assets/20-sidecar.svg)

**Código:** las políticas de autorización, en
[ProductsSidecar/envoy.yaml](src/ProductsSidecar/envoy.yaml):

```yaml
policies:
  lecturas:
    permissions:
      - header: { name: ":method", string_match: { exact: GET } }
    principals:
      - any: true
  administracion:
    permissions:
      - any: true
    principals:
      - metadata:
          filter: envoy.filters.http.jwt_authn
          path: [{ key: jwt_payload }, { key: realm_access }, { key: roles }]
          value: { list_match: { one_of: { string_match: { exact: admin } } } }
```

**Cómo verlo:** [demos/20-sidecar.sh](demos/20-sidecar.sh) cambia el nombre de
un `Product` por HTTP, sin identificarse. Por HTTPS, alice lo lee pero no lo
puede cambiar, y carol sí. Desde adentro de la red, `products-service:8000`
acepta cualquier cambio, y el sidecar, `products-service:9000`, lo rechaza: 403
sin token, y 401 con un token al que alice se agregó el rol `admin` sin poder
volver a firmarlo. El registro del sidecar muestra cada decisión. En Grafana,
el panel "Solicitudes en el sidecar de Products, por resultado" muestra las
permitidas y las rechazadas.

> [!TIP]
>
> Consulta [JWT Authentication](https://www.envoyproxy.io/docs/envoy/latest/configuration/http/http_filters/jwt_authn_filter)
> y [Role Based Access Control
> -RBAC- Filter](https://www.envoyproxy.io/docs/envoy/latest/configuration/http/http_filters/rbac_filter)
> para tener más información sobre los dos filtros de Envoy que usa el sidecar.

#### Valet Key

**Problema:** si los archivos de los clientes -imágenes, documentos- pasan por
la aplicación, la aplicación gasta red, memoria y tiempo en mover bytes, y el
gateway tiene que aceptar cuerpos grandes. Si en cambio el cliente accede
directamente al almacenamiento con las credenciales de la aplicación, puede
hacer cualquier cosa con todos los datos.

**Implementación:** las imágenes de los `Products` se guardan en Azurite, el
emulador de Azure Storage. `Products` tiene la clave de la cuenta, pero no la
entrega: le da al cliente una URL firmada -una SAS, *Shared Access
Signature*- que permite una sola operación, sobre la imagen de un solo
`Product`, durante 2 minutos como máximo. Con esa URL, el cliente sube o baja
la imagen directamente de Azurite, y los bytes no pasan por el gateway ni por
`Products`. En AWS, el equivalente de una SAS es una URL prefirmada de Amazon
S3 -*presigned URL*-, que también permite una operación sobre un objeto
durante un tiempo acotado:

* `POST /products/{id}/image/upload-url` entrega una URL para crear o reemplazar
  la imagen. No sirve para leerla, ni para la imagen de otro `Product`, ni
  después de vencer. El cliente puede pedir que venza antes, con `?seconds=N`.
* `GET /products/{id}/image` redirige, con 302, a una URL para leer la imagen.
* `PUT /products/{id}/image` es la forma sin Valet Key, para comparar:
  `Products` recibe la imagen y la sube él mismo, con su clave. Por la entrada
  HTTP, el gateway acepta hasta 20 MB en esa ruta; por la HTTPS, el
  [Gatekeeper](#gatekeeper) no deja pasar más de 64 KB.

Lo hace la clase `ProductImages`, en
[ProductsService/ProductImages.cs](src/ProductsService/ProductImages.cs): el
método `GetUploadKeyAsync` firma la URL de escritura, `GetDownloadKeyAsync` la
de lectura, y `UploadAsync` sube la imagen sin Valet Key. Los usan los métodos
`GetImageUploadUrl`, `GetImage` y `UploadImage` de la clase
`ProductsController`, en
[ProductsService/Controllers/ProductsController.cs](src/ProductsService/Controllers/ProductsController.cs).
La firma se calcula con la clave de la cuenta, sin llamar a Azurite, y no
incluye el host: `Products` llega a Azurite como `azurite:10000`, y la URL que
entrega dice `localhost:10000`, la dirección que usa el cliente. Como en
cualquier cambio del catálogo, el sidecar exige el rol `admin` para pedir una
URL de escritura.

**Cómo funciona:**

![Subida y bajada de una imagen con Valet Key](assets/21-valet-key.svg)

**Código:**

![Clases de las imágenes de Products](assets/21-valet-key-clases.svg)

**Cómo verlo:** [demos/21-valet-key.sh](demos/21-valet-key.sh) sube una imagen
de 5 MB a través de `Products`, por HTTP; por HTTPS, el gateway la rechaza con
413. Después carol pide una URL firmada y sube la imagen directamente a
Azurite. Con esa URL no se puede leer la imagen, ni escribir la de otro
`Product`, y sin la firma no se puede nada. Una URL que vence a los 5 segundos
funciona enseguida y no después. Para bajar la imagen, `Products` redirige a
una URL de lectura. En Grafana, el panel "Bytes de imágenes que recibe
Products" muestra los 5 MB de la subida sin Valet Key, y el panel "Claves de
imágenes que entrega Products", las URL firmadas.

> [!TIP]
>
> Consulta [Grant limited access to Azure Storage resources using shared access
> signatures](https://learn.microsoft.com/en-us/azure/storage/common/storage-sas-overview)
> para tener más información sobre las SAS, y [Create a service SAS for a blob
> with .NET](https://learn.microsoft.com/en-us/azure/storage/blobs/sas-service-create-dotnet)
> sobre `GenerateSasUri`, con el que `ProductImages` firma las URL.

## Demos de facilidad de modificación

Escenario: operar para varias regiones o clientes, centralizar la configuración,
integrarse con un sistema legado y migrar funcionalidad de un monolito.

Qué se muestra:

* Una copia completa de la aplicación por región o cliente, con enrutamiento
  dirigido.
* Configuración central con recarga dinámica.
* Un puente de mensajería entre la aplicación y un sistema legado simulado.
* Migración progresiva de rutas desde un monolito.

Patrones cubiertos:

* **Deployment Stamps**: dos copias de la aplicación; el gateway elige una según
  un encabezado de región o cliente.
* **External Configuration Store**: URLs, límites y *feature flags* en un
  almacén central que se recarga sin redesplegar.
* **Messaging Bridge**: traduce los eventos de `Orders` al formato de un ERP
  legado simulado, y viceversa.
* **Strangler Fig**: un monolito que atiende `Users`, `Products` y `Orders`; el
  gateway desvía ruta por ruta hacia los microservicios hasta reemplazarlo.

Resultado observable: despliegues independientes por región, cambios de
configuración sin redespliegue y convivencia ordenada con sistemas existentes.

## Demos de facilidad de despliegue

Escenario: publicar una nueva versión de un servicio, por ejemplo `Products`,
con riesgo controlado y medir su impacto.

Qué se muestra:

* Dos entornos vivos y conmutación inmediata.
* Reemplazo de instancias por lotes.
* Una fracción del tráfico derivada a la versión nueva.
* Experimentos por segmento de usuarios.

Patrones y estrategias cubiertos:

* **Arquitectura de microservicios**: el estilo común sobre el que se apoyan
  todas las demos.
* **Blue/Green Deployment**: dos versiones de `Products` detrás del gateway, con
  cambio y *rollback* inmediatos.
* **Rolling Upgrade**: varias réplicas que se reemplazan por lotes, verificando
  su salud.
* **Canary Testing**: entre 1 % y 10 % del tráfico a la versión nueva, por
  ejemplo con `split_clients` de nginx.
* **A/B Testing**: segmentos por usuario, con cookie o encabezado, y comparación
  de métricas.

Resultado observable: despliegues frecuentes con impacto acotado y decisiones
basadas en métricas.

## Cobertura

| Categoría | Patrones | Demo |
| --------- | -------- | ---- |
| Disponibilidad | Bulkhead, Circuit Breaker, Competing Consumers, Event Sourcing, Health Endpoint Monitoring, Rate Limiting, Retry, Throttling, Transactional Outbox | 1 |
| Rendimiento | Cache-Aside, Queue-Based Load Leveling, Competing Consumers, Publisher/Subscriber, Asynchronous Request-Reply, CQRS, Materialized View | 2 |
| Seguridad | Gatekeeper, Federated Identity, Gateway Offloading, Sidecar, Valet Key | 3 |
| Modificabilidad y despliegue | Deployment Stamps, External Configuration Store, Messaging Bridge, Strangler Fig | 4 |
| Modificabilidad y despliegue: estrategias de lanzamiento | Arquitectura de microservicios, Blue/Green, Rolling Upgrade, Canary Testing, A/B Testing | 5 |
