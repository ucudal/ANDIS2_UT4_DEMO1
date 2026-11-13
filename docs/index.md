# Demo de patrones de arquitectura en una arquitectura de microservicios

Esta demo es la aplicación base del tema de patrones de arquitectura, una
pequeña aplicación de comercio electrónico, con los patrones de disponibilidad,
rendimiento, seguridad, facilidad de modificación y eficiencia energética, y las
estrategias de despliegue, implementados.

También es una demo de arquitectura de microservicios. La descripción de la
arquitectura esta en [ARCHITECTURE.md](../ARCHITECTURE.md).

El panorama de la demo y la explicación de cada patrón está en
[PATTERNS.md](../PATTERNS.md).

## Descripción

La aplicación tiene tres servicios de dominio, cada uno con su propia base de
datos, detrás de un API Gateway. Las `Orders` se procesan en segundo plano por
varias réplicas de un *worker* que consumen una cola de
[RabbitMQ](https://www.rabbitmq.com/). Los servicios publican eventos en
RabbitMQ, y los proyectores los usan para mantener una vista materializada del
detalle de las `Orders`. `Products` usa [Redis](https://redis.io/) como caché.

El gateway tiene dos entradas:
[HTTP](https://datatracker.ietf.org/doc/html/rfc9110), sin los patrones de
seguridad, y HTTPS, con ellos. Por la entrada HTTPS, los usuarios se identifican
con un token de [Keycloak](https://www.keycloak.org/), el proveedor de
identidad; el gateway valida cada solicitud antes de reenviarla; `Products`
tiene un *sidecar* que exige el rol `admin` para cambiar el catálogo; y las
imágenes de los `Products` se suben y se bajan directamente de
[Azurite](https://github.com/Azure/Azurite), el emulador de [Azure
Storage](https://azure.microsoft.com/products/storage/), con URL firmadas.

| Componente | Responsabilidad | Tecnología | Puerto en el host |
| ---------- | --------------- | ---------- | ----------------- |
| `api-gateway` | Punto de entrada único; enruta `/users/`, `/products/` y `/orders/` a cada servicio; aplica Throttling por cliente a `POST /orders/`. En la entrada HTTPS, además, termina [TLS](https://datatracker.ietf.org/doc/html/rfc8446), valida los tokens y las solicitudes, y comprime las respuestas | [nginx](https://nginx.org/) 1.25 | 80 HTTP, 443 HTTPS, 8090 fachada, 8091 despliegue |
| `users-service` | Alta y consulta de [`Users`](src/UsersService/Models/Users.cs) por [SOAP](https://www.w3.org/TR/soap/), con el contrato publicado como [WSDL](https://www.w3.org/TR/wsdl/) | [ASP.NET Core](https://dotnet.microsoft.com/apps/aspnet) 10 + [SoapCore](https://github.com/DigDes/SoapCore) + [MongoDB Driver](https://www.mongodb.com/docs/drivers/csharp/) | 8002 |
| `users-service-v2` | La versión `v2` de `Users`, con un experimento de A/B Testing; solo la usan las demos de facilidad de despliegue | La misma imagen que `users-service`, con otra versión configurada | — |
| `products-service` | Catálogo de [`Products`](src/ProductsService/Models/Product.cs), con caché en Redis; reserva y libera el stock de cada `Order` | ASP.NET Core 10 + [EF Core](https://learn.microsoft.com/ef/core/) + [StackExchange.Redis](https://stackexchange.github.io/StackExchange.Redis/) | 8001 |
| `orders-service` | Alta y consulta de [`Orders`](src/OrdersService/Models/Order.cs); arma el detalle de las `Orders` de un `User` consultando a `Users` y `Products` por HTTP, o lo lee de la vista materializada; publica los eventos de las `Orders` a través del *outbox* | ASP.NET Core 10 + EF Core | 8000 |
| `orders-worker` ×3 | Consume la cola y confirma, rechaza o cancela cada `Order`; es la misma imagen que `orders-service` con el *worker* habilitado | ASP.NET Core 10 + [RabbitMQ.Client](https://www.rabbitmq.com/client-libraries/dotnet) | — |
| `orders-projector` ×3 | Mantiene la vista materializada del detalle de las `Orders` a partir de los eventos; es la misma imagen que `orders-service` con el proyector habilitado | ASP.NET Core 10 + RabbitMQ.Client | — |
| `rabbitmq` | Exchange `andis.events` y colas `orders.process`, `orders.details-view` y `products.stock` | RabbitMQ 4.4 | 5672, 15672 consola |
| `redis` | Caché de `Products` | Redis 8 | 6379 |
| `products-sidecar` | Autenticación, autorización y registro de accesos de `Products`, sin cambiar su código; comparte el espacio de red de `products-service` | [Envoy](https://www.envoyproxy.io/) 1.40 | — |
| `keycloak` | Proveedor de identidad: usuarios, contraseñas, roles y tokens | Keycloak 26.8 | 8080 |
| `azurite` | Imágenes de los `Products` | Azurite 3.37, emulador de Azure Storage | 10000 |
| `unleash` | Servicio de *feature flags*: qué variante del experimento de A/B Testing ve cada `User`; solo en el perfil `feature-flags` | [Unleash](https://www.getunleash.io/) 8.2 | 4242 consola |
| `unleash-db` | Datos de Unleash; solo en el perfil `feature-flags` | PostgreSQL 18 | — |
| `consul` | Almacén central de configuración | [Consul](https://developer.hashicorp.com/consul) 1.21 | 8500 consola |
| `legacy-broker` | Broker del ERP legado | [ActiveMQ](https://activemq.apache.org/) Classic 6.1 | 8161 consola |
| `legacy-erp` | ERP legado simulado: registros de ancho fijo en ActiveMQ | [.NET](https://dotnet.microsoft.com/) 10 + [Apache NMS](https://activemq.apache.org/components/nms/) | — |
| `messaging-bridge` | Puente entre RabbitMQ y ActiveMQ | .NET 10 + RabbitMQ.Client + Apache NMS | — |
| `monolith` | Monolito que se reemplaza en la demo de Strangler Fig | ASP.NET Core 10 + SoapCore + EF Core + [SQLite](https://www.sqlite.org/) | — |
| `purchases-service` | [`Purchases`](src/PurchasesService/Purchase.cs): las compras de cada `Product`, con su propio modelo; su capa anticorrupción traduce desde `Orders` y `Users` | ASP.NET Core 10 | — |
| `bff-web` | Backend de la web: las `Orders` de un `User` con precios y total | ASP.NET Core 10 | — |
| `bff-mobile` | Backend de la app móvil: el `Product` y el estado de cada `Order` | ASP.NET Core 10 | — |
| `users-db` | Datos de `Users` | [MongoDB](https://www.mongodb.com/) 9 | 27017 |
| `products-db` | Datos de `Products` | [PostgreSQL](https://www.postgresql.org/) 18 | 5432 |
| `orders-db` | `Orders`, sus eventos, el *outbox* y la vista materializada | [MySQL](https://www.mysql.com/) 26 | 3306 |

Endpoints disponibles a través del gateway, en `http://localhost`. Por la
entrada HTTPS, `https://localhost`, son los mismos, salvo los de administración
-`/chaos`, `/patterns`, `/cache` y `/orders/projections/rebuild`-, y necesitan
un token de Keycloak: ver [Demos de seguridad](../PATTERNS.md#demos-de-seguridad)
en PATTERNS.md.

| Método | Ruta | Descripción |
| ------ | ---- | ----------- |
| `GET` | `/users/UserService.svc?wsdl` | Contrato SOAP de `Users` |
| `POST` | `/users/UserService.svc` | Operaciones SOAP de `Users`, según el encabezado `SOAPAction`: `ListUsers` lista los `Users`, `GetUser` obtiene un `User`, `CreateUser` crea uno y `UpdateUser` lo cambia y publica `user.updated`; ver [UsersService.http](https://github.com/ucudal/ANDIS2_UT4_DEMO1/blob/main/src/UsersService/UsersService.http) |
| `GET` | `/products/` | Lista los `Products` |
| `GET` | `/products/{id}` | Obtiene un `Product` |
| `POST` | `/products/` | Crea un `Product` |
| `PUT` | `/products/{id}` | Cambia un `Product`, invalida la caché y publica `product.updated` |
| `PUT` | `/products/{id}/image` | Sube la imagen de un `Product` a través de `Products`, sin Valet Key |
| `POST` | `/products/{id}/image/upload-url` | Entrega una URL firmada para subir la imagen de un `Product` directamente a Azurite: Valet Key |
| `GET` | `/products/{id}/image` | Redirige a una URL firmada para bajar la imagen de un `Product` |
| `PUT`, `DELETE` | `/products/cache` | Prende o apaga la caché y simula una base lenta, para las pruebas: ver [Qué agrega a las demos de disponibilidad](../PATTERNS.md#qué-agrega-a-las-demos-de-disponibilidad) en PATTERNS.md |
| `GET` | `/orders/` | Lista las `Orders` con su estado |
| `GET` | `/orders/{id}` | Obtiene una `Order` |
| `POST` | `/orders/` | Crea una `Order` en estado `Pending` y la encola para procesarla; responde `202 Accepted` con `Location` |
| `GET` | `/orders/{id}/status` | Estado de una `Order`: `200` mientras está `Pending`, `303` a la `Order` cuando se procesó |
| `GET` | `/orders/user/{userId}/details` | `Orders` de un `User` con el nombre del `User` y de cada `Product`, pidiéndolos a `Users` y a `Products` |
| `GET` | `/orders/user/{userId}/details/view` | Lo mismo, leído de la vista materializada |
| `GET` | `/orders/{id}/events` | Eventos de una `Order` y el estado que resulta de reproducirlos |
| `POST` | `/orders/projections/rebuild` | Reconstruye la tabla de `Orders` a partir de los eventos |
| `GET` | `/health/users`, `/health/products`, `/health/orders` | Salud de cada servicio |
| `PUT`, `DELETE` | `/users/chaos`, `/products/chaos`, `/orders/chaos` | Inyección de fallas para las pruebas: ver [Qué agrega a la aplicación base](../PATTERNS.md#qué-agrega-a-la-aplicación-base) en PATTERNS.md |
| `GET`, `PUT`, `DELETE` | `/orders/patterns` | Prende y apaga el Retry, el Circuit Breaker, el Bulkhead y el Throttling de `Orders`, para que cada prueba muestre un patrón por vez |

`orders-service` llama a `users-service` y a `products-service` al armar el
detalle de las `Orders` de un `User`, y `orders-worker` los llama al procesar
cada `Order`. Esas llamadas son las que protegen los patrones de resiliencia.
`orders-projector` también los llama, una vez por `Order`, para copiar los
nombres en la vista.

## Diagrama de despliegue

[![Diagrama de despliegue: los contenedores de Docker Compose, sus artefactos y
sus conexiones](assets/deployment.svg)](https://github.com/ucudal/ANDIS2_UT4_DEMO1/blob/main/assets/deployment.svg)

Para verlo en tamaño completo y hacer zoom, abrir
[deployment.svg](https://github.com/ucudal/ANDIS2_UT4_DEMO1/blob/main/assets/deployment.svg) o el diagrama dibujado [desde su
fuente](https://www.plantuml.com/plantuml/proxy?fmt=svg&src=https://raw.githubusercontent.com/ucudal/ANDIS2_UT4_DEMO1/main/assets/deployment.puml),
[deployment.puml](https://github.com/ucudal/ANDIS2_UT4_DEMO1/blob/main/assets/deployment.puml).

Cada nodo `«container»` es un contenedor definido en
[docker-compose.yaml](https://github.com/ucudal/ANDIS2_UT4_DEMO1/blob/main/docker-compose.yaml). Los servicios escuchan en el puerto
8000 dentro de la red de [Docker](https://www.docker.com/); en el host se publican en 8000, 8001 y 8002,
para probarlos sin pasar por el gateway. Las réplicas de `orders-worker` y
`orders-projector` no se publican en el host. `products-sidecar` no tiene red
propia: comparte la de `products-service`, y los demás contenedores lo
encuentran como `products-service:9000`.

## Patrones implementados

| Patrón | Dónde |
| ------ | ----- |
| Retry | `AddRetry` en el método `AddResilience` de la clase `Resilience`, en [Resilience.cs](src/OrdersService/Resilience.cs), para los `HttpClient` hacia `Users` y `Products` |
| Circuit Breaker | `AddCircuitBreaker` en el método `AddResilience` de la clase `Resilience`, en [Resilience.cs](src/OrdersService/Resilience.cs); la degradación está en el método `GetOrdersForUser` de la clase `OrdersController`, en [OrdersController.cs](src/OrdersService/Controllers/OrdersController.cs) |
| Bulkhead | `AddRateLimiter` con un `ConcurrencyLimiter` por dependencia, en el método `AddResilience` de la clase `Resilience`, en [Resilience.cs](src/OrdersService/Resilience.cs) |
| Throttling | `AddRateLimiter`, política `throttling`, en [Program.cs](src/OrdersService/Program.cs), aplicada en el método `GetOrdersForUser` de la clase `OrdersController`, en [OrdersController.cs](src/OrdersService/Controllers/OrdersController.cs); por cliente, en [nginx.conf](https://github.com/ucudal/ANDIS2_UT4_DEMO1/blob/main/src/ApiGateway/nginxconf/nginx.conf): [`limit_req_zone`](https://nginx.org/en/docs/http/ngx_http_limit_req_module.html#limit_req_zone) y [`limit_req`](https://nginx.org/en/docs/http/ngx_http_limit_req_module.html#limit_req) para `POST /orders/` |
| Rate Limiting | Del lado del cliente: la demo [05 de Rate Limiting](https://github.com/ucudal/ANDIS2_UT4_DEMO1/blob/main/demos/05-rate-limiting.sh) y [rate-limiting.js](https://github.com/ucudal/ANDIS2_UT4_DEMO1/blob/main/k6/rate-limiting.js) crean `Orders` sin superar el límite del gateway |
| Health Endpoint Monitoring | `AddHealthChecks` y `MapHealthChecks` en el `Program.cs` de cada servicio; [`healthcheck`](https://docs.docker.com/reference/compose-file/services/#healthcheck) en [docker-compose.yaml](https://github.com/ucudal/ANDIS2_UT4_DEMO1/blob/main/docker-compose.yaml); rutas `/health/*` en `nginx.conf` |
| Competing Consumers | Los métodos `ExecuteAsync` y `ChangeStatusAsync` de la clase `OrderProcessingWorker`, en [OrderProcessingWorker.cs](src/OrdersService/Messaging/OrderProcessingWorker.cs), consumen la cola `orders.process`; servicio `orders-worker` con 3 réplicas en [docker-compose.yaml](https://github.com/ucudal/ANDIS2_UT4_DEMO1/blob/main/docker-compose.yaml) |
| Event Sourcing | La clase `OrderEvent`, en [OrderEvent.cs](src/OrdersService/Models/OrderEvent.cs), y el método `Replay` de la clase `OrderProjection`, en [OrderProjection.cs](src/OrdersService/Models/OrderProjection.cs); tabla `order_events` en [mysqlinit.sql](https://github.com/ucudal/ANDIS2_UT4_DEMO1/blob/main/src/OrdersService/mysqlinit/mysqlinit.sql) |
| Transactional Outbox | La clase `OutboxMessage`, en [OutboxMessage.cs](src/Common/OutboxMessage.cs), escrita en el método `CreateOrder` de la clase `OrdersController`, en [OrdersController.cs](src/OrdersService/Controllers/OrdersController.cs), en el método `ChangeStatusAsync` del *worker* y en la clase `StockReservations` de `Products`; la publica el método `PublishPendingAsync` de la clase `OutboxRelay`, en [OutboxRelay.cs](src/Common/OutboxRelay.cs) |
| Saga | Los métodos `ReserveAsync` y `ReleaseAsync` de la clase `StockReservations`, en [StockReservations.cs](src/ProductsService/StockReservations.cs); los métodos `ConfirmAsync`, `RejectAsync` y `CancelAsync` de la clase `OrderProcessingWorker`, en [OrderProcessingWorker.cs](src/OrdersService/Messaging/OrderProcessingWorker.cs); el rechazo del ERP en [Erp.cs](src/LegacyErp/Erp.cs), traducido por el método `ToEvent` de la clase `ErpFormat`, en [ErpFormat.cs](src/MessagingBridge/ErpFormat.cs) |
| Cache-Aside | Los métodos `GetOrLoadAsync` e `InvalidateAsync` de la clase `ProductCache`, en [ProductCache.cs](src/ProductsService/ProductCache.cs), usados en los métodos `GetProduct` y `UpdateProduct` de la clase `ProductsController`, en [ProductsController.cs](src/ProductsService/Controllers/ProductsController.cs) |
| Queue-Based Load Leveling | `POST /orders/` responde sin procesar la `Order`; la cola `orders.process` absorbe los picos |
| Publisher/Subscriber | Los métodos `PublishEventAsync` y `DeclareSubscriberQueueAsync` de la clase `RabbitMq`, en [RabbitMq.cs](src/Common/RabbitMq.cs), y los contratos de la clase `Events` y sus registros, en [Events.cs](src/Common/Events.cs); publican `Orders`, `Users` y `Products`; se suscriben `orders-worker`, `orders-projector` y `products-service` |
| Asynchronous Request-Reply | Los métodos `CreateOrder` y `GetOrderStatus` de la clase `OrdersController`, en [OrdersController.cs](src/OrdersService/Controllers/OrdersController.cs) |
| CQRS | Comandos en el método `CreateOrder` de la clase `OrdersController`, en [OrdersController.cs](src/OrdersService/Controllers/OrdersController.cs), y en el método `ChangeStatusAsync` de la clase `OrderProcessingWorker`, en [OrderProcessingWorker.cs](src/OrdersService/Messaging/OrderProcessingWorker.cs); consultas en el método `GetOrdersForUser` de la clase `OrdersQueriesController`, en [OrdersQueriesController.cs](src/OrdersService/Controllers/OrdersQueriesController.cs) |
| Materialized View | Tabla `order_details`, mantenida por el método `ApplyAsync` de la clase `OrderDetailsProjector`, en [OrderDetailsProjector.cs](src/OrdersService/Messaging/OrderDetailsProjector.cs), en `orders-projector` |
| Federated Identity | [Keycloak](https://www.keycloak.org/), con el realm de [andis-realm.json](https://github.com/ucudal/ANDIS2_UT4_DEMO1/blob/main/src/Keycloak/andis-realm.json); `Orders` decide con los claims del token en el registro `CallerIdentity`, en [CallerIdentity.cs](src/OrdersService/CallerIdentity.cs) |
| Gateway Offloading | La entrada HTTPS de [nginx.conf](https://github.com/ucudal/ANDIS2_UT4_DEMO1/blob/main/src/ApiGateway/nginxconf/nginx.conf): [TLS](https://datatracker.ietf.org/doc/html/rfc8446), [`auth_request`](https://nginx.org/en/docs/http/ngx_http_auth_request_module.html#auth_request) a Keycloak, los claims con [identity.js](https://github.com/ucudal/ANDIS2_UT4_DEMO1/blob/main/src/ApiGateway/nginxconf/identity.js) y [gzip](https://nginx.org/en/docs/http/ngx_http_gzip_module.html) |
| Gatekeeper | La entrada HTTPS de [nginx.conf](https://github.com/ucudal/ANDIS2_UT4_DEMO1/blob/main/src/ApiGateway/nginxconf/nginx.conf): rutas, métodos, tipo y tamaño del cuerpo, y encabezados de identidad; [docker-compose.gatekeeper.yaml](https://github.com/ucudal/ANDIS2_UT4_DEMO1/blob/main/docker-compose.gatekeeper.yaml) deja de publicar los puertos de los servicios |
| Sidecar | `products-sidecar` en [docker-compose.yaml](https://github.com/ucudal/ANDIS2_UT4_DEMO1/blob/main/docker-compose.yaml), configurado en [envoy.yaml](https://github.com/ucudal/ANDIS2_UT4_DEMO1/blob/main/src/ProductsSidecar/envoy.yaml) |
| Anti-corruption Layer | Las clases `OrdersTranslator`, `UsersTranslator` y `OrdersPurchaseSource`, en [AntiCorruption](https://github.com/ucudal/ANDIS2_UT4_DEMO1/tree/main/src/PurchasesService/AntiCorruption); el modelo de `Purchases` está en [Purchase.cs](src/PurchasesService/Purchase.cs) |
| External Configuration Store | Las clases `ConsulConfigurationProvider` y `ConsulConfigurationPoller`, en [ExternalConfiguration.cs](src/Common/ExternalConfiguration.cs) |
| Messaging Bridge | Las clases `OrdersToErpBridge` y `ErpToOrdersBridge`, en [MessagingBridge](https://github.com/ucudal/ANDIS2_UT4_DEMO1/tree/main/src/MessagingBridge); la traducción está en la clase `ErpFormat` |
| Strangler Fig | La fachada del puerto 8090 en [nginx.conf](https://github.com/ucudal/ANDIS2_UT4_DEMO1/blob/main/src/ApiGateway/nginxconf/nginx.conf), con la tabla de rutas en [strangler-routes.conf](https://github.com/ucudal/ANDIS2_UT4_DEMO1/blob/main/src/ApiGateway/nginxconf/strangler-routes.conf), y el monolito en [Program.cs](src/Monolith/Program.cs) |
| Backends for Frontends | `bff-web`, en su [Program.cs](src/BffWeb/Program.cs), y `bff-mobile`, en su [Program.cs](src/BffMobile/Program.cs), publicados por el gateway en `/web/` y `/mobile/` |
| Blue/Green Deployment | `users-service` y `users-service-v2` en [docker-compose.yaml](https://github.com/ucudal/ANDIS2_UT4_DEMO1/blob/main/docker-compose.yaml); la entrada de despliegue, en el puerto 8091 de [nginx.conf](https://github.com/ucudal/ANDIS2_UT4_DEMO1/blob/main/src/ApiGateway/nginxconf/nginx.conf), con el destino en [deployment-routes.conf](https://github.com/ucudal/ANDIS2_UT4_DEMO1/blob/main/src/ApiGateway/nginxconf/deployment-routes.conf); con [Kubernetes](https://kubernetes.io/), el `Rollout` de [Argo Rollouts](https://argoproj.github.io/rollouts/) en [rollout.yaml](https://github.com/ucudal/ANDIS2_UT4_DEMO1/blob/main/k8s/blue-green/rollout.yaml) |
| Rolling Upgrade | El alias `users-pool` de `users-service-v2` y las funciones `pool_start_v1` y `pool_replace` de [common.sh](https://github.com/ucudal/ANDIS2_UT4_DEMO1/blob/main/demos/common.sh); [`proxy_next_upstream`](https://nginx.org/en/docs/http/ngx_http_proxy_module.html#proxy_next_upstream) en la entrada de despliegue; con Kubernetes, el [Deployment](https://kubernetes.io/docs/concepts/workloads/controllers/deployment/) de [deployment.yaml](https://github.com/ucudal/ANDIS2_UT4_DEMO1/blob/main/k8s/rolling-upgrade/deployment.yaml) y el `Rollout` con análisis de [rollout.yaml](https://github.com/ucudal/ANDIS2_UT4_DEMO1/blob/main/k8s/rolling-upgrade/rollout.yaml) |
| Canary Testing | [`split_clients`](https://nginx.org/en/docs/http/ngx_http_split_clients_module.html#split_clients) en [deployment-routes.conf](https://github.com/ucudal/ANDIS2_UT4_DEMO1/blob/main/src/ApiGateway/nginxconf/deployment-routes.conf), escrito por la función `deploy_canary` de [common.sh](https://github.com/ucudal/ANDIS2_UT4_DEMO1/blob/main/demos/common.sh); el encabezado `X-Version`, en [Release.cs](src/UsersService/Release.cs); con Kubernetes, el `Rollout` de [rollout.yaml](https://github.com/ucudal/ANDIS2_UT4_DEMO1/blob/main/k8s/canary/rollout.yaml), que cambia los pesos de una ruta de [Gateway API](https://gateway-api.sigs.k8s.io/) y decide con el análisis de [analysis-error-rate.yaml](https://github.com/ucudal/ANDIS2_UT4_DEMO1/blob/main/k8s/base/analysis-error-rate.yaml) |
| A/B Testing | La clase `Experiment`, en [Experiment.cs](src/UsersService/Experiment.cs), usada en el método `GetUser` de la clase `UserService`, en [UserService.cs](src/UsersService/Soap/UserService.cs); le pregunta la variante a [OpenFeature](https://openfeature.dev/), con el proveedor de [Unleash](https://www.getunleash.io/) registrado en [Program.cs](src/UsersService/Program.cs); el *flag* lo crean las funciones `experiment_start` y `experiment_split` de [common.sh](https://github.com/ucudal/ANDIS2_UT4_DEMO1/blob/main/demos/common.sh) |
| Valet Key | Los métodos `GetUploadKeyAsync` y `GetDownloadKeyAsync` de la clase `ProductImages`, en [ProductImages.cs](src/ProductsService/ProductImages.cs), usados en los métodos `GetImageUploadUrl` y `GetImage` de la clase `ProductsController`, en [ProductsController.cs](src/ProductsService/Controllers/ProductsController.cs) |
| Power Monitor | Las reglas de `energy.rules.yml`, en [prometheus.yaml](https://github.com/ucudal/ANDIS2_UT4_DEMO1/blob/main/k8s/base/prometheus.yaml), con la CPU de [cAdvisor](https://github.com/google/cadvisor) y de [kube-state-metrics](https://github.com/kubernetes/kube-state-metrics); el panel "Potencia estimada del worker, por pod" de Grafana |
| Use Serverless Cloud Services y Scale Logical Components Independently | El [ScaledObject](https://keda.sh/docs/latest/reference/scaledobject-spec/) de [KEDA](https://keda.sh/) en [scaledobject-queue.yaml](https://github.com/ucudal/ANDIS2_UT4_DEMO1/blob/main/k8s/energy/scaledobject-queue.yaml), que escala el Deployment de [worker.yaml](https://github.com/ucudal/ANDIS2_UT4_DEMO1/blob/main/k8s/energy/worker.yaml) de 0 a 6 réplicas según la cola `orders.process` |
| Queue Non-Urgent Processing Requests | El ScaledObject de [scaledobject-window.yaml](https://github.com/ucudal/ANDIS2_UT4_DEMO1/blob/main/k8s/energy/scaledobject-window.yaml), con un *trigger* metrics-api: el *worker* solo procesa la cola mientras la ventana está abierta, según la clave `andis/Energy/WindowOpen` de Consul, que publica el método `GetWindow` de la clase `EnergyController`, en [EnergyController.cs](src/OrdersService/Controllers/EnergyController.cs) |

Qué problema resuelve cada patrón, cómo se implementó y qué muestra cada prueba
está en las secciones [Demos de disponibilidad](../PATTERNS.md#demos-de-disponibilidad),
[Demos de rendimiento y procesamiento asíncrono](../PATTERNS.md#demos-de-rendimiento-y-procesamiento-asíncrono),
[Demos de seguridad](../PATTERNS.md#demos-de-seguridad),
[Demos de facilidad de modificación](../PATTERNS.md#demos-de-facilidad-de-modificación),
[Demos de facilidad de despliegue](../PATTERNS.md#demos-de-facilidad-de-despliegue) y
[Demos de eficiencia energética](../PATTERNS.md#demos-de-eficiencia-energética)
de PATTERNS.md.
