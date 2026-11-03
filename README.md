<!-- markdownlint-disable-next-line MD033 MD041 -->
<img alt="UCU" src="https://www.ucu.edu.uy/plantillas/images/logo_ucu.svg"
width="150"/>

# Universidad Católica del Uruguay

## Análisis y diseño de aplicaciones II

# Demo de patrones de arquitectura en una arquitectura de microservicios

Esta demo es la aplicación base de la UT4, una pequeña aplicación de comercio
electrónico, con los patrones de disponibilidad y rendimiento implementados.

También es la demo de la [arquitectura de microservicios](ARCHITECTURE.md) en la
UT5.

El panorama de la demo y la explicación de cada patrón está en
[PATTERNS.md](PATTERNS.md).`

## Descripción

La aplicación tiene tres servicios de dominio, cada uno con su propia base de
datos, detrás de un API Gateway. Las `Orders` se procesan en segundo plano por
varias réplicas de un *worker* que consumen una cola de RabbitMQ. Los servicios
publican eventos en RabbitMQ, y un proyector los usa para mantener una vista
materializada del detalle de las `Orders`. `Products` usa Redis como caché.

| Componente | Responsabilidad | Tecnología | Puerto en el host |
| ---------- | --------------- | ---------- | ----------------- |
| `api-gateway` | Punto de entrada único; enruta `/users/`, `/products/` y `/orders/` a cada servicio; aplica Throttling por cliente a `POST /orders/` | nginx 1.25 | 80 |
| `users-service` | Alta y consulta de [`Users`](UsersService/Models/Users.cs) por SOAP, con el contrato publicado como WSDL | ASP.NET Core 10 + SoapCore + EF Core | 8002 |
| `products-service` | Catálogo de [`Products`](ProductsService/Models/Product.cs), con caché en Redis | ASP.NET Core 10 + MongoDB Driver + StackExchange.Redis | 8001 |
| `orders-service` | Alta y consulta de [`Orders`](OrdersService/Models/Order.cs); arma el detalle de las `Orders` de un `User` consultando a `Users` y `Products` por HTTP, o lo lee de la vista materializada; publica los eventos de las `Orders` a través del *outbox* | ASP.NET Core 10 + EF Core | 8000 |
| `orders-worker` ×3 | Consume la cola y confirma o rechaza cada `Order`; es la misma imagen que `orders-service` con el *worker* habilitado | ASP.NET Core 10 + RabbitMQ.Client | — |
| `orders-projector` | Mantiene la vista materializada del detalle de las `Orders` a partir de los eventos; es la misma imagen que `orders-service` con el proyector habilitado | ASP.NET Core 10 + RabbitMQ.Client | — |
| `rabbitmq` | Exchange `andis.events` y colas `orders.process` y `orders.details-view` | RabbitMQ 4.4 | 5672, 15672 consola |
| `redis` | Caché de `Products` | Redis 8 | 6379 |
| `users-db` | Datos de `Users` | PostgreSQL 18 | 5432 |
| `products-db` | Datos de `Products` | MongoDB 9 | 27017 |
| `orders-db` | `Orders`, sus eventos, el *outbox* y la vista materializada | MySQL 26 | 3306 |

Endpoints disponibles a través del gateway, en `http://localhost`:

| Método | Ruta | Descripción |
| ------ | ---- | ----------- |
| `GET` | `/users/UserService.svc?wsdl` | Contrato SOAP de `Users` |
| `POST` | `/users/UserService.svc` | Operaciones SOAP de `Users`, según el encabezado `SOAPAction`: `ListUsers` lista los `Users`, `GetUser` obtiene un [`User`](UsersService/Models/Users.cs), `CreateUser` crea uno y `UpdateUser` lo cambia y publica `user.updated`; ver [UsersService/UsersService.http](UsersService/UsersService.http) |
| `GET` | `/products/` | Lista los `Products` |
| `GET` | `/products/{id}` | Obtiene un [`Product`](ProductsService/Models/Product.cs) |
| `POST` | `/products/` | Crea un `Product` |
| `PUT` | `/products/{id}` | Cambia un `Product`, invalida la caché y publica `product.updated` |
| `PUT`, `DELETE` | `/products/cache` | Prende o apaga la caché y simula una base lenta, para las pruebas: ver [PATTERNS.md](PATTERNS.md#qué-agrega-a-la-demo-1) |
| `GET` | `/orders/` | Lista las `Orders` con su estado |
| `GET` | `/orders/{id}` | Obtiene una [`Order`](OrdersService/Models/Order.cs) |
| `POST` | `/orders/` | Crea una `Order` en estado `Pending` y la encola para procesarla; responde `202 Accepted` con `Location` |
| `GET` | `/orders/{id}/status` | Estado de una `Order`: `200` mientras está `Pending`, `303` a la `Order` cuando se procesó |
| `GET` | `/orders/user/{userId}/details` | `Orders` de un `User` con el nombre del `User` y de cada `Product`, pidiéndolos a `Users` y a `Products` |
| `GET` | `/orders/user/{userId}/details/view` | Lo mismo, leído de la vista materializada |
| `GET` | `/orders/{id}/events` | Eventos de una `Order` y el estado que resulta de reproducirlos |
| `POST` | `/orders/projections/rebuild` | Reconstruye la tabla de `Orders` a partir de los eventos |
| `GET` | `/health/users`, `/health/products`, `/health/orders` | Salud de cada servicio |
| `PUT`, `DELETE` | `/users/chaos`, `/products/chaos`, `/orders/chaos` | Inyección de fallas para las pruebas: ver [PATTERNS.md](PATTERNS.md#qué-agrega-a-la-aplicación-base) |
| `GET`, `PUT`, `DELETE` | `/orders/patterns` | Prende y apaga el Retry, el Circuit Breaker, el Bulkhead y el Throttling de `Orders`, para que cada prueba muestre un patrón por vez |

`orders-service` llama a `users-service` y a `products-service` al armar el
detalle de las `Orders` de un `User`, y `orders-worker` los llama al procesar
cada `Order`. Esas llamadas son las que protegen los patrones de resiliencia.
`orders-projector` también los llama, una vez por `Order`, para copiar los
nombres en la vista.

### Requisitos

| Herramienta | Para qué | macOS | Windows |
| ----------- | -------- | ----- | ------- |
| Docker | Levantar la aplicación | Docker Desktop[^docker] | Docker Desktop[^docker] |
| `bash` y `curl` | Los comandos de [commands.azcli](commands.azcli) y los scripts de [demos/](demos/) | Incluidos | Git Bash[^gitbash] o WSL[^wsl]; PowerShell y `cmd` no sirven, porque los comandos usan sintaxis de `bash` |
| `jq`[^jq] | Formatear las respuestas JSON y convertirlas en tablas con la función `table` de `commands.azcli` | Incluido | En WSL, `sudo apt install jq`; en Git Bash, `winget install jqlang.jq` y volver a abrir la terminal |
| `column` | Alinear las columnas de esas tablas | Incluido | En WSL, incluido en la mayoría de las distribuciones; si falta, `sudo apt install bsdextrautils`; ver la nota sobre Git Bash |
| `xmllint` | Indentar las respuestas SOAP de `Users` en `commands.azcli` | Incluido | En WSL, `sudo apt install libxml2-utils`; en Git Bash no viene incluido: usar WSL o ver el XML sin indentar |
| Visual Studio Code[^vscode] con la extensión Azure CLI Tools[^azcli] | Opcional: abrir [commands.azcli](commands.azcli) con resaltado de sintaxis y ejecutar cada comando en la terminal | Instalar Visual Studio Code y, dentro de él, la extensión | Igual que en macOS |
| Extensión Database Client[^dbclient] de Visual Studio Code | Opcional: ver las tablas, colecciones y claves de MySQL, PostgreSQL, MongoDB y Redis; los datos para conectarse están en [Conectarse a las bases de datos](#conectarse-a-las-bases-de-datos) | Instalar la extensión dentro de Visual Studio Code | Igual que en macOS |

[^docker]: Docker Desktop: <https://www.docker.com/products/docker-desktop/>
[^gitbash]: Git Bash, incluido en Git para Windows: <https://git-scm.com/downloads/win>
[^wsl]: WSL: <https://learn.microsoft.com/windows/wsl/install>
[^jq]: `jq`: <https://jqlang.org/download/>
[^vscode]: Visual Studio Code: <https://code.visualstudio.com/download>
[^azcli]: Azure CLI Tools para Visual Studio Code: <https://marketplace.visualstudio.com/items?itemName=ms-vscode.azurecli>
[^dbclient]: Database Client para Visual Studio Code: <https://marketplace.visualstudio.com/items?itemName=cweijan.vscode-database-client2>

`jq` y `column` solo hacen falta para ver las respuestas como tabla. Los
scripts de `demos/` también los usan, pero si falta alguno muestran el JSON tal
cual; las excepciones son `03-bulkhead.sh`, `04-throttling.sh`,
`05-rate-limiting.sh` y los demos del `09` en adelante, que necesitan `jq`.

En Windows conviene usar WSL, porque se comporta igual que Linux y macOS. Si Git Bash
no tiene `column`, la función `table` falla con `column: command not found`.
En ese caso hay dos opciones:

- Quitar `| column -t -s $'\t'` de la función: se ven los mismos datos, con las
  columnas separadas por tabuladores pero sin alinear.
- Usar `jq .` en lugar de `table`, que muestra el JSON formateado y con colores.

### Cómo ejecutarla

Los comandos están en [commands.azcli](commands.azcli). En resumen:

```bash
docker compose up -d --build --wait
curl http://localhost/orders/user/1/details
```

`--wait` espera a que todos los contenedores estén sanos según su `healthcheck`.

Las bases de datos se crean con datos de ejemplo la primera vez que se levantan
los contenedores, con los scripts de `*/mysqlinit`, `*/mongoinit` y `*/pginit`.
Para volver a empezar desde cero hay que borrar las carpetas `mysqldata`,
`mongodbdata` y `pgdata`; los comandos están al final de `commands.azcli`.

### Conectarse a las bases de datos

Con los contenedores levantados, cada base se puede abrir desde la extensión
Database Client o desde cualquier otro cliente, con estos datos:

| Servicio | Motor | Host | Puerto | Base | Usuario | Contraseña |
| -------- | ----- | ---- | ------ | ---- | ------- | ---------- |
| `Orders` | MySQL | `localhost` | `3306` | `ordersdb` | `ordersusr` | `Pa55w0rd` |
| `Users` | PostgreSQL | `localhost` | `5432` | `usersdb` | `usersusr` | `Pa55w0rd` |
| `Products` | MongoDB | `localhost` | `27017` | `productsdb` | `productsusr` | `Pa55w0rd` |
| Caché de `Products` | Redis | `localhost` | `6379` | — | — | — |

En MongoDB el usuario `productsusr` se autentica contra la base `productsdb`,
así que en el campo de base de autenticación -`authSource`- va `productsdb` y
no `admin`. La cadena de conexión completa es
`mongodb://productsusr:Pa55w0rd@localhost:27017/productsdb?authSource=productsdb`.
Para ver todas las bases de MongoDB está el usuario `root`, con la misma
contraseña y `admin` como base de autenticación.

Redis no tiene usuario ni contraseña.

## Diagrama de despliegue

```mermaid
---
config:
  class:
    hideEmptyMembersBox: true
  theme: base
  themeVariables:
    fontSize: 14px
    background: transparent
    mainBkg: transparent
    secondaryColor: transparent
    tertiaryColor: transparent
    clusterBkg: transparent
    clusterBorder: '#006EAF'
    edgeLabelBackground: transparent
    primaryTextColor: '#006EAF'
    primaryBorderColor: '#006EAF'
    nodeBorder: '#006EAF'
    textColor: '#006EAF'
    titleColor: '#006EAF'
    lineColor: '#006EAF'
    useGradient: false
    dropShadow: false
---
flowchart LR
    client["«device»<br/>Cliente<br/>(curl, navegador, scripts de demos/)"]

    subgraph host["«device» Host con Docker"]
        direction LR

        subgraph net["«execution environment» Red de Docker Compose"]
            direction LR

            gw["«container» api-gateway<br/>nginx 1.25<br/>──────────<br/>«artifact» nginx.conf"]

            subgraph users["Users"]
                direction TB
                us["«container» users-service<br/>ASP.NET Core 10<br/>──────────<br/>«artifact» UsersService.dll"]
                udb[("«container» users-db<br/>PostgreSQL 18<br/>──────────<br/>«artifact» pginit.sql")]
            end

            subgraph products["Products"]
                direction TB
                ps["«container» products-service<br/>ASP.NET Core 10<br/>──────────<br/>«artifact» ProductsService.dll"]
                pdb[("«container» products-db<br/>MongoDB 9<br/>──────────<br/>«artifact» mongoinit.js")]
                rd[("«container» redis<br/>Redis 8")]
            end

            subgraph orders["Orders"]
                direction TB
                os["«container» orders-service<br/>ASP.NET Core 10<br/>──────────<br/>«artifact» OrdersService.dll"]
                ow["«container» orders-worker ×3<br/>ASP.NET Core 10<br/>──────────<br/>«artifact» OrdersService.dll"]
                op["«container» orders-projector<br/>ASP.NET Core 10<br/>──────────<br/>«artifact» OrdersService.dll"]
                mq[["«container» rabbitmq<br/>RabbitMQ 4.4<br/>──────────<br/>exchange andis.events"]]
                odb[("«container» orders-db<br/>MySQL 26<br/>──────────<br/>«artifact» mysqlinit.sql")]
            end
        end
    end

    client -- "HTTP :80" --> gw
    gw -- "HTTP :8000 /users/" --> us
    gw -- "HTTP :8000 /products/" --> ps
    gw -- "HTTP :8000 /orders/" --> os
    os -- "HTTP :8000" --> us
    os -- "HTTP :8000" --> ps
    os -- "AMQP :5672 publica" --> mq
    us -- "AMQP :5672 publica" --> mq
    ps -- "AMQP :5672 publica" --> mq
    mq -- "AMQP :5672 consume" --> ow
    mq -- "AMQP :5672 consume" --> op
    ow -- "HTTP :8000" --> us
    ow -- "HTTP :8000" --> ps
    op -- "HTTP :8000" --> us
    op -- "HTTP :8000" --> ps
    us -- "TCP :5432" --> udb
    ps -- "TCP :27017" --> pdb
    ps -- "TCP :6379" --> rd
    os -- "TCP :3306" --> odb
    ow -- "TCP :3306" --> odb
    op -- "TCP :3306" --> odb
```

Cada nodo `«container»` es un contenedor definido en
[docker-compose.yaml](docker-compose.yaml). Los servicios escuchan en el puerto
8000 dentro de la red de Docker; en el host se publican en 8000, 8001 y 8002,
para probarlos sin pasar por el gateway. Las réplicas de `orders-worker` y
`orders-projector` no se publican en el host.

## Patrones implementados

| Patrón | Dónde |
| ------ | ----- |
| Retry | `AddRetry` en el método `AddResilience` de la clase `Resilience`, en [OrdersService/Resilience.cs](OrdersService/Resilience.cs), para los `HttpClient` hacia `Users` y `Products` |
| Circuit Breaker | `AddCircuitBreaker` en el método `AddResilience` de la clase `Resilience`, en [OrdersService/Resilience.cs](OrdersService/Resilience.cs); la degradación está en el método `GetOrdersForUser` de la clase `OrdersController`, en [OrdersService/Controllers/OrdersController.cs](OrdersService/Controllers/OrdersController.cs) |
| Bulkhead | `AddRateLimiter` con un `ConcurrencyLimiter` por dependencia, en el método `AddResilience` de la clase `Resilience`, en [OrdersService/Resilience.cs](OrdersService/Resilience.cs) |
| Throttling | `AddRateLimiter`, política `throttling`, en [OrdersService/Program.cs](OrdersService/Program.cs), aplicada en el método `GetOrdersForUser` de la clase `OrdersController`, en [OrdersService/Controllers/OrdersController.cs](OrdersService/Controllers/OrdersController.cs); por cliente, en [ApiGateway/nginxconf/nginx.conf](ApiGateway/nginxconf/nginx.conf): `limit_req_zone` y `limit_req` para `POST /orders/` |
| Rate Limiting | Del lado del cliente: [demos/05-rate-limiting.sh](demos/05-rate-limiting.sh) y [k6/rate-limiting.js](k6/rate-limiting.js) crean `Orders` sin superar el límite del gateway |
| Health Endpoint Monitoring | `AddHealthChecks` y `MapHealthChecks` en el `Program.cs` de cada servicio; `healthcheck` en [docker-compose.yaml](docker-compose.yaml); rutas `/health/*` en `nginx.conf` |
| Competing Consumers | Los métodos `ExecuteAsync` y `ProcessAsync` de la clase `OrderProcessingWorker`, en [OrdersService/Messaging/OrderProcessingWorker.cs](OrdersService/Messaging/OrderProcessingWorker.cs), consumen la cola `orders.process`; servicio `orders-worker` con 3 réplicas en [docker-compose.yaml](docker-compose.yaml) |
| Event Sourcing | La clase `OrderEvent`, en [OrdersService/Models/OrderEvent.cs](OrdersService/Models/OrderEvent.cs), y el método `Replay` de la clase `OrderProjection`, en [OrdersService/Models/OrderProjection.cs](OrdersService/Models/OrderProjection.cs); tabla `order_events` en [OrdersService/mysqlinit/mysqlinit.sql](OrdersService/mysqlinit/mysqlinit.sql) |
| Transactional Outbox | La clase `OutboxMessage`, en [OrdersService/Models/OutboxMessage.cs](OrdersService/Models/OutboxMessage.cs), escrita en el método `CreateOrder` de la clase `OrdersController`, en [OrdersService/Controllers/OrdersController.cs](OrdersService/Controllers/OrdersController.cs), y en el método `ProcessAsync` del *worker*; la publica el método `PublishPendingAsync` de la clase `OutboxRelay`, en [OrdersService/Messaging/OutboxRelay.cs](OrdersService/Messaging/OutboxRelay.cs) |
| Cache-Aside | Los métodos `GetOrLoadAsync` e `InvalidateAsync` de la clase `ProductCache`, en [ProductsService/ProductCache.cs](ProductsService/ProductCache.cs), usados en los métodos `GetProduct` y `UpdateProduct` de la clase `ProductsController`, en [ProductsService/Controllers/ProductsController.cs](ProductsService/Controllers/ProductsController.cs) |
| Queue-Based Load Leveling | `POST /orders/` responde sin procesar la `Order`; la cola `orders.process` absorbe los picos |
| Publisher/Subscriber | Los métodos `PublishEventAsync` y `DeclareSubscriberQueueAsync` de la clase `RabbitMq`, en [Common/RabbitMq.cs](Common/RabbitMq.cs), y los contratos de la clase `Events` y sus registros, en [Common/Events.cs](Common/Events.cs); publican `Orders`, `Users` y `Products`; se suscriben `orders-worker` y `orders-projector` |
| Asynchronous Request-Reply | Los métodos `CreateOrder` y `GetOrderStatus` de la clase `OrdersController`, en [OrdersService/Controllers/OrdersController.cs](OrdersService/Controllers/OrdersController.cs) |
| CQRS | Comandos en el método `CreateOrder` de la clase `OrdersController`, en [OrdersService/Controllers/OrdersController.cs](OrdersService/Controllers/OrdersController.cs), y en el método `ProcessAsync` de la clase `OrderProcessingWorker`, en [OrdersService/Messaging/OrderProcessingWorker.cs](OrdersService/Messaging/OrderProcessingWorker.cs); consultas en el método `GetOrdersForUser` de la clase `OrdersQueriesController`, en [OrdersService/Controllers/OrdersQueriesController.cs](OrdersService/Controllers/OrdersQueriesController.cs) |
| Materialized View | Tabla `order_details`, mantenida por el método `ApplyAsync` de la clase `OrderDetailsProjector`, en [OrdersService/Messaging/OrderDetailsProjector.cs](OrdersService/Messaging/OrderDetailsProjector.cs), en `orders-projector` |

Qué problema resuelve cada patrón, cómo se implementó y qué muestra cada prueba
está en las secciones [Demos de disponibilidad](PATTERNS.md#demos-de-disponibilidad)
y [Demos de rendimiento y procesamiento asíncrono](PATTERNS.md#demos-de-rendimiento-y-procesamiento-asíncrono)
de PATTERNS.md.

## Cómo probar los patrones

Cada patrón tiene un script en [demos/](./demos/) que provoca la situación que
lo activa, como un servicio que falla, que está lento o que recibe demasiadas
solicitudes, y muestra el resultado.

Las instrucciones para ejecutar estos scripts y la explicación de lo que sucede
en cada uno está en [commands.azcli](commands.azcli).

Mientras corren las pruebas, también se puede mirar:

- La salud de los servicios en vivo, en otra terminal: `./demos/monitor-health.sh`.
- Los eventos de los patrones en los logs de Docker:
  `docker compose logs -f orders-service orders-worker orders-projector products-service`.
- Las colas, sus consumidores y los enlaces del exchange `andis.events` en la
  consola de RabbitMQ: <http://localhost:15672>, usuario y contraseña `guest`.

## Observabilidad y pruebas de carga

Los scripts de [demos/](./demos/) muestran cada patrón paso a paso, con pocas
solicitudes; la evidencia en los logs de Docker se ve con los comandos de
[commands.azcli](commands.azcli). Para ver cómo se comportan los patrones
bajo carga y a lo largo del tiempo, la aplicación tiene además un perfil
opcional de observabilidad y escenarios de carga con [k6](https://k6.io).

| Componente | Responsabilidad | Tecnología | Puerto en el host |
| ---------- | --------------- | ---------- | ----------------- |
| `prometheus` | Consulta cada 5 s el endpoint `/metrics` de los servicios, de cada réplica de `orders-worker` y de RabbitMQ; recibe las métricas de k6 | Prometheus 3.15 | 9090 |
| `grafana` | Dashboard con todo lo anterior, ya configurado y sin login | Grafana 13.2 | 3000 |
| `k6` | Genera la carga de cada escenario; no queda corriendo | k6 2.3 | — |

```bash
# Levanta la aplicación junto con Prometheus y Grafana
docker compose --profile observability up -d --build --wait

# Ejecuta un escenario de carga
docker compose run --rm k6 run /scripts/bulkhead.js
```

El dashboard está en <http://localhost:3000>. Sin el perfil
`observability` la aplicación funciona igual y k6 también, solo que los
resultados se ven únicamente en la terminal.

### Métricas

Los servicios exponen sus métricas en `GET /metrics`, con OpenTelemetry y el
exporter de Prometheus, configurado en el método `AddObservability` de la clase
`ObservabilityExtensions`, en [Common/Observability.cs](Common/Observability.cs). Casi todas las métricas ya
las publican las bibliotecas que usa la aplicación; no hubo que instrumentar el
código a mano:

| Métrica | Quién la publica | Qué muestra |
| ------- | ---------------- | ----------- |
| `http_server_request_duration_seconds` | ASP.NET Core | Solicitudes recibidas, latencia y código HTTP, por ruta |
| `aspnetcore_rate_limiting_*` | ASP.NET Core | Throttling: consultas en curso, aceptadas y rechazadas |
| `resilience_polly_strategy_events_total` | Polly | Retry, Circuit Breaker, Bulkhead y timeouts: cada evento, por dependencia |
| `resilience_polly_pipeline_duration_milliseconds` | Polly | Duración de cada llamada a `Users` y `Products`, con los reintentos incluidos |
| `rabbitmq_detailed_queue_*` | RabbitMQ | Mensajes esperando y en proceso en la cola, y consumidores conectados |
| `orders_processed_total` | La clase `OrderProcessingWorker`, en [OrdersService/Messaging/OrderProcessingWorker.cs](OrdersService/Messaging/OrderProcessingWorker.cs) | `Orders` procesadas por cada réplica, con su resultado |
| `outbox_pending`, `outbox_published_total` | La clase `OutboxRelay`, en [OrdersService/Messaging/OutboxRelay.cs](OrdersService/Messaging/OutboxRelay.cs) | Mensajes del *outbox* que esperan ser publicados, y los publicados |
| `products_cache_total` | La clase `ProductCache`, en [ProductsService/ProductCache.cs](ProductsService/ProductCache.cs) | Lecturas de la caché de `Products`: `hit`, `miss`, `error` o `disabled` |
| `chaos_failure_rate`, `chaos_delay_seconds` | El método `UseChaos` de la clase `ChaosExtensions`, en [Common/Chaos.cs](Common/Chaos.cs) | Las fallas inyectadas, para ver en qué momento empezaron y terminaron |
| `orders_patterns_enabled` | El método `MapPatterns` de la clase `PatternExtensions`, en [OrdersService/Patterns.cs](OrdersService/Patterns.cs) | Qué patrones de resiliencia están prendidos en `Orders`: 1 prendido, 0 apagado |
| `service_health` | La clase `HealthMetricsPublisher`, en [Common/HealthMetrics.cs](Common/HealthMetrics.cs) | Salud de cada servicio y de cada uno de sus chequeos, la misma que responde `/health`: 0 Unhealthy, 1 Degraded, 2 Healthy |
| `orders_circuit_state` | El método `MapPatterns` de la clase `PatternExtensions`, en [OrdersService/Patterns.cs](OrdersService/Patterns.cs) | Estado del circuito de cada dependencia de `Orders`: -1 con el Circuit Breaker apagado, 0 cerrado, 1 semiabierto, 2 abierto |
| `k6_*` | k6 | La carga generada y lo que ve el cliente: solicitudes por segundo por código HTTP, latencia por endpoint |

> [!TIP]
>
> Consulta [.NET Observability with OpenTelemetry](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/observability-with-otel)
> para tener más información sobre cómo `AddObservability` configura las
> métricas, y [Creating Metrics](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/metrics-instrumentation)
> sobre los contadores y los *gauges* propios de la aplicación, como
> `service_health` y `orders_patterns_enabled`.

### Escenarios de carga

Los escenarios están en [k6/](k6/). Cada uno inyecta las fallas que necesita y
las quita al terminar. Al principio de cada archivo se explica qué mirar en el
dashboard.

| Escenario | Qué hace | Qué hay que observar en Grafana |
| --------- | -------- | ------------------------------- |
| `circuit-breaker.js` | 5 consultas de detalle por segundo durante 80 s; `Products` falla siempre entre los 15 s y los 50 s | La latencia sube por los reintentos y vuelve a bajar cuando el circuito se abre; las respuestas siguen siendo 200, degradadas; el circuito se cierra después de la recuperación |
| `bulkhead.js` | `Products` demora 1,5 s; 6 consultas de detalle por segundo durante 45 s | Rechazos del bulkhead solo en `Products`; `Users` sin rechazos |
| `throttling.js` | `Orders` demora 1 s; los usuarios virtuales suben de 0 a 40 | Las consultas en curso se estabilizan en 20 y el excedente recibe 503 |
| `rate-limiting.js` | 5 `POST /orders/` por segundo durante 30 s, sin Rate Limiting; después, 1 por segundo durante 30 s, con Rate Limiting; 5 `GET /orders/` por segundo todo el tiempo | En la primera fase pasa un POST por segundo y el resto recibe 429; en la segunda no hay 429; los GET siempre responden 200 |
| `competing-consumers.js` | Crea 60 `Orders` de golpe | La cola crece y se vacía a unas 1,5 `Orders` por segundo, repartidas entre las 3 réplicas |
| `load.js` | Carga base para las demos de rendimiento: lecturas mezcladas a una tasa configurable; con `-e DETAILS=view`, el detalle se lee de la vista | Throughput y latencia por servicio; k6 termina con error si no se cumple el objetivo de p95 menor a 500 ms con menos de 1 % de errores; comparando las dos corridas, la latencia de `details` contra la de `details view` |
| `cache-aside.js` | 50 lecturas de `Products` por segundo, con una base que demora 100 ms: 30 s con la caché apagada y 30 s prendida | La latencia de `product` baja de unos 100 ms a unos 3 ms; las lecturas de la caché pasan de `disabled` a casi todas `hit` |
| `load-leveling.js` | 10 `POST /orders/` por segundo durante 10 s, directamente en `orders-service` | La latencia de `create order` se mantiene baja; la cola `orders.process` crece durante el pico y se vacía a ritmo constante |

`load.js` acepta la tasa y la duración como parámetros:

```bash
docker compose run --rm k6 run -e RATE=50 -e DURATION=2m /scripts/load.js
```

Para que la escala de tiempo del dashboard sea legible, conviene esperar unos
segundos entre un escenario y el siguiente.
