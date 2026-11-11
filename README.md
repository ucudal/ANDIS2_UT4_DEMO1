<!-- markdownlint-disable-next-line MD033 MD041 -->
<img alt="UCU" src="https://www.ucu.edu.uy/plantillas/images/logo_ucu.svg"
width="150"/>

# Universidad Católica del Uruguay

## Análisis y diseño de aplicaciones II

# Demo de patrones de arquitectura en una arquitectura de microservicios

Esta demo es la aplicación base de la UT4, una pequeña aplicación de comercio
electrónico, con los patrones de disponibilidad, rendimiento, seguridad y
facilidad de modificación, y las estrategias de despliegue, implementados.

También es la demo de la [arquitectura de microservicios](ARCHITECTURE.md) en la
UT5.

El panorama de la demo y la explicación de cada patrón está en
[PATTERNS.md](PATTERNS.md).

## Descripción

La aplicación tiene tres servicios de dominio, cada uno con su propia base de
datos, detrás de un API Gateway. Las `Orders` se procesan en segundo plano por
varias réplicas de un *worker* que consumen una cola de [RabbitMQ](https://www.rabbitmq.com/). Los servicios
publican eventos en RabbitMQ, y los proyectores los usan para mantener una vista
materializada del detalle de las `Orders`. `Products` usa [Redis](https://redis.io/) como caché.

El gateway tiene dos entradas: [HTTP](https://datatracker.ietf.org/doc/html/rfc9110), sin los patrones de seguridad, y HTTPS,
con ellos. Por la entrada HTTPS, los usuarios se identifican con un token de
[Keycloak](https://www.keycloak.org/), el proveedor de identidad; el gateway valida cada solicitud antes de
reenviarla; `Products` tiene un *sidecar* que exige el rol `admin` para
cambiar el catálogo; y las imágenes de los `Products` se suben y se bajan
directamente de [Azurite](https://github.com/Azure/Azurite), el emulador de [Azure Storage](https://azure.microsoft.com/products/storage/), con URL firmadas.

| Componente | Responsabilidad | Tecnología | Puerto en el host |
| ---------- | --------------- | ---------- | ----------------- |
| `api-gateway` | Punto de entrada único; enruta `/users/`, `/products/` y `/orders/` a cada servicio; aplica Throttling por cliente a `POST /orders/`. En la entrada HTTPS, además, termina [TLS](https://datatracker.ietf.org/doc/html/rfc8446), valida los tokens y las solicitudes, y comprime las respuestas | [nginx](https://nginx.org/) 1.25 | 80 HTTP, 443 HTTPS, 8090 fachada, 8091 despliegue |
| `users-service` | Alta y consulta de [`Users`](src/UsersService/Models/Users.cs) por [SOAP](https://www.w3.org/TR/soap/), con el contrato publicado como [WSDL](https://www.w3.org/TR/wsdl/) | [ASP.NET Core](https://dotnet.microsoft.com/apps/aspnet) 10 + [SoapCore](https://github.com/DigDes/SoapCore) + [EF Core](https://learn.microsoft.com/ef/core/) | 8002 |
| `users-service-v2` | La versión `v2` de `Users`, con un experimento de A/B Testing; solo la usan las demos de facilidad de despliegue | La misma imagen que `users-service`, con otra versión configurada | — |
| `products-service` | Catálogo de [`Products`](src/ProductsService/Models/Product.cs), con caché en Redis | ASP.NET Core 10 + [MongoDB Driver](https://www.mongodb.com/docs/drivers/csharp/) + [StackExchange.Redis](https://stackexchange.github.io/StackExchange.Redis/) | 8001 |
| `orders-service` | Alta y consulta de [`Orders`](src/OrdersService/Models/Order.cs); arma el detalle de las `Orders` de un `User` consultando a `Users` y `Products` por HTTP, o lo lee de la vista materializada; publica los eventos de las `Orders` a través del *outbox* | ASP.NET Core 10 + EF Core | 8000 |
| `orders-worker` ×3 | Consume la cola y confirma o rechaza cada `Order`; es la misma imagen que `orders-service` con el *worker* habilitado | ASP.NET Core 10 + [RabbitMQ.Client](https://www.rabbitmq.com/client-libraries/dotnet) | — |
| `orders-projector` ×3 | Mantiene la vista materializada del detalle de las `Orders` a partir de los eventos; es la misma imagen que `orders-service` con el proyector habilitado | ASP.NET Core 10 + RabbitMQ.Client | — |
| `rabbitmq` | Exchange `andis.events` y colas `orders.process` y `orders.details-view` | RabbitMQ 4.4 | 5672, 15672 consola |
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
| `users-db` | Datos de `Users` | [PostgreSQL](https://www.postgresql.org/) 18 | 5432 |
| `products-db` | Datos de `Products` | [MongoDB](https://www.mongodb.com/) 9 | 27017 |
| `orders-db` | `Orders`, sus eventos, el *outbox* y la vista materializada | [MySQL](https://www.mysql.com/) 26 | 3306 |

Endpoints disponibles a través del gateway, en `http://localhost`. Por la
entrada HTTPS, `https://localhost`, son los mismos, salvo los de
administración -`/chaos`, `/patterns`, `/cache` y `/orders/projections/rebuild`-,
y necesitan un token de Keycloak: ver [Demos de
seguridad](PATTERNS.md#demos-de-seguridad) en PATTERNS.md.

| Método | Ruta | Descripción |
| ------ | ---- | ----------- |
| `GET` | `/users/UserService.svc?wsdl` | Contrato SOAP de `Users` |
| `POST` | `/users/UserService.svc` | Operaciones SOAP de `Users`, según el encabezado `SOAPAction`: `ListUsers` lista los `Users`, `GetUser` obtiene un `User`, `CreateUser` crea uno y `UpdateUser` lo cambia y publica `user.updated`; ver [UsersService/UsersService.http](src/UsersService/UsersService.http) |
| `GET` | `/products/` | Lista los `Products` |
| `GET` | `/products/{id}` | Obtiene un `Product` |
| `POST` | `/products/` | Crea un `Product` |
| `PUT` | `/products/{id}` | Cambia un `Product`, invalida la caché y publica `product.updated` |
| `PUT` | `/products/{id}/image` | Sube la imagen de un `Product` a través de `Products`, sin Valet Key |
| `POST` | `/products/{id}/image/upload-url` | Entrega una URL firmada para subir la imagen de un `Product` directamente a Azurite: Valet Key |
| `GET` | `/products/{id}/image` | Redirige a una URL firmada para bajar la imagen de un `Product` |
| `PUT`, `DELETE` | `/products/cache` | Prende o apaga la caché y simula una base lenta, para las pruebas: ver [Qué agrega a las demos de disponibilidad](PATTERNS.md#qué-agrega-a-las-demos-de-disponibilidad) en PATTERNS.md |
| `GET` | `/orders/` | Lista las `Orders` con su estado |
| `GET` | `/orders/{id}` | Obtiene una `Order` |
| `POST` | `/orders/` | Crea una `Order` en estado `Pending` y la encola para procesarla; responde `202 Accepted` con `Location` |
| `GET` | `/orders/{id}/status` | Estado de una `Order`: `200` mientras está `Pending`, `303` a la `Order` cuando se procesó |
| `GET` | `/orders/user/{userId}/details` | `Orders` de un `User` con el nombre del `User` y de cada `Product`, pidiéndolos a `Users` y a `Products` |
| `GET` | `/orders/user/{userId}/details/view` | Lo mismo, leído de la vista materializada |
| `GET` | `/orders/{id}/events` | Eventos de una `Order` y el estado que resulta de reproducirlos |
| `POST` | `/orders/projections/rebuild` | Reconstruye la tabla de `Orders` a partir de los eventos |
| `GET` | `/health/users`, `/health/products`, `/health/orders` | Salud de cada servicio |
| `PUT`, `DELETE` | `/users/chaos`, `/products/chaos`, `/orders/chaos` | Inyección de fallas para las pruebas: ver [Qué agrega a la aplicación base](PATTERNS.md#qué-agrega-a-la-aplicación-base) en PATTERNS.md |
| `GET`, `PUT`, `DELETE` | `/orders/patterns` | Prende y apaga el Retry, el Circuit Breaker, el Bulkhead y el Throttling de `Orders`, para que cada prueba muestre un patrón por vez |

`orders-service` llama a `users-service` y a `products-service` al armar el
detalle de las `Orders` de un `User`, y `orders-worker` los llama al procesar
cada `Order`. Esas llamadas son las que protegen los patrones de resiliencia.
`orders-projector` también los llama, una vez por `Order`, para copiar los
nombres en la vista.

### Requisitos

| Herramienta | Para qué | macOS | Windows |
| ----------- | -------- | ----- | ------- |
| [Docker](https://www.docker.com/) | Levantar la aplicación | Docker Desktop[^docker] | Docker Desktop[^docker] |
| `bash` y `curl` | Los comandos de los archivos de [commands](commands/) y los scripts de [demos](demos/) | Incluidos | Git Bash[^gitbash] o WSL[^wsl]; PowerShell y `cmd` no sirven, porque los comandos usan sintaxis de `bash` |
| `jq`[^jq] | Formatear las respuestas [JSON](https://www.json.org/) y convertirlas en tablas con la función `table` de los archivos de `commands/` | Incluido | En WSL, `sudo apt install jq`; en Git Bash, `winget install jqlang.jq` y volver a abrir la terminal |
| [`column`](https://man7.org/linux/man-pages/man1/column.1.html) | Alinear las columnas de esas tablas | Incluido | En WSL, incluido en la mayoría de las distribuciones; si falta, `sudo apt install bsdextrautils`; ver la nota sobre Git Bash |
| [`xmllint`](https://gnome.pages.gitlab.gnome.org/libxml2/xmllint.html) | Indentar las respuestas [SOAP](https://www.w3.org/TR/soap/) de `Users` en `commands/01-api-gateway.azcli` y los errores de [Azurite](https://github.com/Azure/Azurite) en `commands/04-security.azcli` | Incluido | En WSL, `sudo apt install libxml2-utils`; en Git Bash no viene incluido: usar WSL o ver el [XML](https://www.w3.org/XML/) sin indentar |
| Visual Studio Code[^vscode] con la extensión Azure CLI Tools[^azcli] | Opcional: abrir los archivos de [commands](commands/) con resaltado de sintaxis y ejecutar cada comando en la terminal | Instalar Visual Studio Code y, dentro de él, la extensión | Igual que en macOS |
| kind[^kind], kubectl[^kubectl] y el plugin de Argo Rollouts para kubectl[^argorollouts] | Solo para las demos de despliegue con Kubernetes, de la 31 a la 33: crear el clúster y manejar los despliegues | `brew install kind kubectl argoproj/tap/kubectl-argo-rollouts` | En WSL, con los binarios para Linux de cada sitio |
| Extensión Database Client[^dbclient] de Visual Studio Code | Opcional: ver las tablas, colecciones y claves de [MySQL](https://www.mysql.com/), [PostgreSQL](https://www.postgresql.org/), [MongoDB](https://www.mongodb.com/) y [Redis](https://redis.io/); los datos para conectarse están en [Conectarse a las bases de datos](#conectarse-a-las-bases-de-datos) | Instalar la extensión dentro de Visual Studio Code | Igual que en macOS |

[^docker]: Docker Desktop: <https://www.docker.com/products/docker-desktop/>
[^gitbash]: Git Bash, incluido en Git para Windows: <https://git-scm.com/downloads/win>
[^wsl]: WSL: <https://learn.microsoft.com/windows/wsl/install>
[^jq]: `jq`: <https://jqlang.org/download/>
[^vscode]: Visual Studio Code: <https://code.visualstudio.com/download>
[^azcli]: Azure CLI Tools para Visual Studio Code: <https://marketplace.visualstudio.com/items?itemName=ms-vscode.azurecli>
[^kind]: kind: <https://kind.sigs.k8s.io/docs/user/quick-start/#installation>
[^kubectl]: kubectl: <https://kubernetes.io/docs/tasks/tools/>
[^argorollouts]: Plugin de Argo Rollouts para kubectl: <https://argoproj.github.io/argo-rollouts/installation/#kubectl-plugin-installation>
[^dbclient]: Database Client para Visual Studio Code: <https://marketplace.visualstudio.com/items?itemName=cweijan.vscode-database-client2>

`jq` y `column` solo hacen falta para ver las respuestas como tabla. Los
scripts de `demos/` también los usan, pero si falta alguno muestran el JSON tal
cual; las excepciones son `03-bulkhead.sh`, `04-throttling.sh`,
`05-rate-limiting.sh` y los demos del `09` en adelante, que necesitan `jq`.

En Windows conviene usar WSL, porque se comporta igual que Linux y macOS. Si Git Bash
no tiene `column`, la función `table` falla con `column: command not found`.
En ese caso hay dos opciones:

* Quitar `| column -t -s $'\t'` de la función: se ven los mismos datos, con las
  columnas separadas por tabuladores pero sin alinear.
* Usar `jq .` en lugar de `table`, que muestra el JSON formateado y con colores.

### Cómo ejecutarla

Los comandos están en la carpeta [commands](commands/), en seis archivos:

* [01-api-gateway.azcli](commands/01-api-gateway.azcli): probar los servicios a
  través del API Gateway.
* [02-availability.azcli](commands/02-availability.azcli): demos de los
  patrones de disponibilidad, de la 01 a la 09.
* [03-performance.azcli](commands/03-performance.azcli): demos de los
  patrones de rendimiento, de la 10 a la 16.
* [04-security.azcli](commands/04-security.azcli): demos de los patrones de
  seguridad, de la 17 a la 21.
* [05-modifiability.azcli](commands/05-modifiability.azcli): demos de los
  patrones de facilidad de modificación, de la 22 a la 26.
* [06-deployability.azcli](commands/06-deployability.azcli): demos de las
  estrategias de despliegue, de la 27 a la 33. Las demos 31 a 33 corren en un
  clúster de [Kubernetes](https://kubernetes.io/), que se crea con
  `./demos/k8s-up.sh`, y no usan Docker Compose: la entrada de los clientes al
  clúster está en el puerto 8092, y su Prometheus, en el 30090.

Cada archivo empieza levantando todos los servicios y termina con el *tear
down*, así que se puede usar por separado. En resumen:

```bash
docker compose up -d --build --wait
curl http://localhost/orders/user/1/details
```

El comando [`docker compose
up`](https://docs.docker.com/reference/cli/docker/compose/up/) con `--wait`
espera a que todos los contenedores estén sanos según su
[`healthcheck`](https://docs.docker.com/reference/compose-file/services/#healthcheck).

Las bases de datos se crean con datos de ejemplo la primera vez que se levantan
los contenedores, con los scripts de `src/*/mysqlinit`, `src/*/mongoinit` y
`src/*/pginit`. Para volver a empezar desde cero hay que borrar las carpetas
`mysqldata`, `mongodbdata` y `pgdata`; los comandos están al final de cada
archivo de [`commands`](commands/). Lo mismo hace falta si cambia uno de esos
scripts, como `mysqlinit.sql`: con la carpeta ya creada, no se vuelven a
ejecutar.

### Conectarse a las bases de datos

Con los contenedores levantados, cada base se puede abrir desde la extensión
Database Client o desde cualquier otro cliente, con estos datos:

| Servicio | Motor | Host | Puerto | Base | Usuario | Contraseña |
| -------- | ----- | ---- | ------ | ---- | ------- | ---------- |
| `Orders` | [MySQL](https://www.mysql.com/) | `localhost` | `3306` | `ordersdb` | `ordersusr` | `Pa55w0rd` |
| `Users` | [PostgreSQL](https://www.postgresql.org/) | `localhost` | `5432` | `usersdb` | `usersusr` | `Pa55w0rd` |
| `Products` | [MongoDB](https://www.mongodb.com/) | `localhost` | `27017` | `productsdb` | `productsusr` | `Pa55w0rd` |
| Caché de `Products` | [Redis](https://redis.io/) | `localhost` | `6379` | — | — | — |

En MongoDB el usuario `productsusr` se autentica contra la base `productsdb`,
así que en el campo de base de autenticación -`authSource`- va `productsdb` y
no `admin`. La cadena de conexión completa es
`mongodb://productsusr:Pa55w0rd@localhost:27017/productsdb?authSource=productsdb`.
Para ver todas las bases de MongoDB está el usuario `root`, con la misma
contraseña y `admin` como base de autenticación.

Redis no tiene usuario ni contraseña.

La consola de administración de [Keycloak](https://www.keycloak.org/) está en <http://localhost:8080>, con
el usuario `admin` y la contraseña `Pa55w0rd`. Los usuarios de la aplicación
-alice, bob y carol, con la misma contraseña- están en el realm `andis`.

## Diagrama de despliegue

[![Diagrama de despliegue: los contenedores de Docker Compose, sus artefactos y
sus conexiones](assets/deployment.svg)](assets/deployment.svg)

Para verlo en tamaño completo y hacer zoom, abrir
[assets/deployment.svg](assets/deployment.svg) o el diagrama dibujado [desde su
fuente](https://www.plantuml.com/plantuml/proxy?fmt=svg&src=https://raw.githubusercontent.com/ucudal/ANDIS2_UT4_DEMO1/main/assets/deployment.puml),
[assets/deployment.puml](assets/deployment.puml).

Cada nodo `«container»` es un contenedor definido en
[docker-compose.yaml](docker-compose.yaml). Los servicios escuchan en el puerto
8000 dentro de la red de [Docker](https://www.docker.com/); en el host se publican en 8000, 8001 y 8002,
para probarlos sin pasar por el gateway. Las réplicas de `orders-worker` y
`orders-projector` no se publican en el host. `products-sidecar` no tiene red
propia: comparte la de `products-service`, y los demás contenedores lo
encuentran como `products-service:9000`.

## Patrones implementados

| Patrón | Dónde |
| ------ | ----- |
| Retry | `AddRetry` en el método `AddResilience` de la clase `Resilience`, en [OrdersService/Resilience.cs](src/OrdersService/Resilience.cs), para los `HttpClient` hacia `Users` y `Products` |
| Circuit Breaker | `AddCircuitBreaker` en el método `AddResilience` de la clase `Resilience`, en [OrdersService/Resilience.cs](src/OrdersService/Resilience.cs); la degradación está en el método `GetOrdersForUser` de la clase `OrdersController`, en [OrdersService/Controllers/OrdersController.cs](src/OrdersService/Controllers/OrdersController.cs) |
| Bulkhead | `AddRateLimiter` con un `ConcurrencyLimiter` por dependencia, en el método `AddResilience` de la clase `Resilience`, en [OrdersService/Resilience.cs](src/OrdersService/Resilience.cs) |
| Throttling | `AddRateLimiter`, política `throttling`, en [OrdersService/Program.cs](src/OrdersService/Program.cs), aplicada en el método `GetOrdersForUser` de la clase `OrdersController`, en [OrdersService/Controllers/OrdersController.cs](src/OrdersService/Controllers/OrdersController.cs); por cliente, en [ApiGateway/nginxconf/nginx.conf](src/ApiGateway/nginxconf/nginx.conf): [`limit_req_zone`](https://nginx.org/en/docs/http/ngx_http_limit_req_module.html#limit_req_zone) y [`limit_req`](https://nginx.org/en/docs/http/ngx_http_limit_req_module.html#limit_req) para `POST /orders/` |
| Rate Limiting | Del lado del cliente: la demo [05 de Rate Limiting](demos/05-rate-limiting.sh) y [k6/rate-limiting.js](k6/rate-limiting.js) crean `Orders` sin superar el límite del gateway |
| Health Endpoint Monitoring | `AddHealthChecks` y `MapHealthChecks` en el `Program.cs` de cada servicio; [`healthcheck`](https://docs.docker.com/reference/compose-file/services/#healthcheck) en [docker-compose.yaml](docker-compose.yaml); rutas `/health/*` en `nginx.conf` |
| Competing Consumers | Los métodos `ExecuteAsync` y `ProcessAsync` de la clase `OrderProcessingWorker`, en [OrdersService/Messaging/OrderProcessingWorker.cs](src/OrdersService/Messaging/OrderProcessingWorker.cs), consumen la cola `orders.process`; servicio `orders-worker` con 3 réplicas en [docker-compose.yaml](docker-compose.yaml) |
| Event Sourcing | La clase `OrderEvent`, en [OrdersService/Models/OrderEvent.cs](src/OrdersService/Models/OrderEvent.cs), y el método `Replay` de la clase `OrderProjection`, en [OrdersService/Models/OrderProjection.cs](src/OrdersService/Models/OrderProjection.cs); tabla `order_events` en [OrdersService/mysqlinit/mysqlinit.sql](src/OrdersService/mysqlinit/mysqlinit.sql) |
| Transactional Outbox | La clase `OutboxMessage`, en [OrdersService/Models/OutboxMessage.cs](src/OrdersService/Models/OutboxMessage.cs), escrita en el método `CreateOrder` de la clase `OrdersController`, en [OrdersService/Controllers/OrdersController.cs](src/OrdersService/Controllers/OrdersController.cs), y en el método `ProcessAsync` del *worker*; la publica el método `PublishPendingAsync` de la clase `OutboxRelay`, en [OrdersService/Messaging/OutboxRelay.cs](src/OrdersService/Messaging/OutboxRelay.cs) |
| Cache-Aside | Los métodos `GetOrLoadAsync` e `InvalidateAsync` de la clase `ProductCache`, en [ProductsService/ProductCache.cs](src/ProductsService/ProductCache.cs), usados en los métodos `GetProduct` y `UpdateProduct` de la clase `ProductsController`, en [ProductsService/Controllers/ProductsController.cs](src/ProductsService/Controllers/ProductsController.cs) |
| Queue-Based Load Leveling | `POST /orders/` responde sin procesar la `Order`; la cola `orders.process` absorbe los picos |
| Publisher/Subscriber | Los métodos `PublishEventAsync` y `DeclareSubscriberQueueAsync` de la clase `RabbitMq`, en [Common/RabbitMq.cs](src/Common/RabbitMq.cs), y los contratos de la clase `Events` y sus registros, en [Common/Events.cs](src/Common/Events.cs); publican `Orders`, `Users` y `Products`; se suscriben `orders-worker` y `orders-projector` |
| Asynchronous Request-Reply | Los métodos `CreateOrder` y `GetOrderStatus` de la clase `OrdersController`, en [OrdersService/Controllers/OrdersController.cs](src/OrdersService/Controllers/OrdersController.cs) |
| CQRS | Comandos en el método `CreateOrder` de la clase `OrdersController`, en [OrdersService/Controllers/OrdersController.cs](src/OrdersService/Controllers/OrdersController.cs), y en el método `ProcessAsync` de la clase `OrderProcessingWorker`, en [OrdersService/Messaging/OrderProcessingWorker.cs](src/OrdersService/Messaging/OrderProcessingWorker.cs); consultas en el método `GetOrdersForUser` de la clase `OrdersQueriesController`, en [OrdersService/Controllers/OrdersQueriesController.cs](src/OrdersService/Controllers/OrdersQueriesController.cs) |
| Materialized View | Tabla `order_details`, mantenida por el método `ApplyAsync` de la clase `OrderDetailsProjector`, en [OrdersService/Messaging/OrderDetailsProjector.cs](src/OrdersService/Messaging/OrderDetailsProjector.cs), en `orders-projector` |
| Federated Identity | [Keycloak](https://www.keycloak.org/), con el realm de [Keycloak/andis-realm.json](src/Keycloak/andis-realm.json); `Orders` decide con los claims del token en el registro `CallerIdentity`, en [OrdersService/CallerIdentity.cs](src/OrdersService/CallerIdentity.cs) |
| Gateway Offloading | La entrada HTTPS de [ApiGateway/nginxconf/nginx.conf](src/ApiGateway/nginxconf/nginx.conf): [TLS](https://datatracker.ietf.org/doc/html/rfc8446), [`auth_request`](https://nginx.org/en/docs/http/ngx_http_auth_request_module.html#auth_request) a Keycloak, los claims con [ApiGateway/nginxconf/identity.js](src/ApiGateway/nginxconf/identity.js) y [gzip](https://nginx.org/en/docs/http/ngx_http_gzip_module.html) |
| Gatekeeper | La entrada HTTPS de [ApiGateway/nginxconf/nginx.conf](src/ApiGateway/nginxconf/nginx.conf): rutas, métodos, tipo y tamaño del cuerpo, y encabezados de identidad; [docker-compose.gatekeeper.yaml](docker-compose.gatekeeper.yaml) deja de publicar los puertos de los servicios |
| Sidecar | `products-sidecar` en [docker-compose.yaml](docker-compose.yaml), configurado en [ProductsSidecar/envoy.yaml](src/ProductsSidecar/envoy.yaml) |
| Anti-corruption Layer | Las clases `OrdersTranslator`, `UsersTranslator` y `OrdersPurchaseSource`, en [PurchasesService/AntiCorruption](src/PurchasesService/AntiCorruption/); el modelo de `Purchases` está en [PurchasesService/Purchase.cs](src/PurchasesService/Purchase.cs) |
| External Configuration Store | Las clases `ConsulConfigurationProvider` y `ConsulConfigurationPoller`, en [Common/ExternalConfiguration.cs](src/Common/ExternalConfiguration.cs) |
| Messaging Bridge | Las clases `OrdersToErpBridge` y `ErpToOrdersBridge`, en [MessagingBridge](src/MessagingBridge/); la traducción está en la clase `ErpFormat` |
| Strangler Fig | La fachada del puerto 8090 en [ApiGateway/nginxconf/nginx.conf](src/ApiGateway/nginxconf/nginx.conf), con la tabla de rutas en [strangler-routes.conf](src/ApiGateway/nginxconf/strangler-routes.conf), y el monolito en [Monolith/Program.cs](src/Monolith/Program.cs) |
| Backends for Frontends | `bff-web` y `bff-mobile`, en [BffWeb/Program.cs](src/BffWeb/Program.cs) y [BffMobile/Program.cs](src/BffMobile/Program.cs), publicados por el gateway en `/web/` y `/mobile/` |
| Blue/Green Deployment | `users-service` y `users-service-v2` en [docker-compose.yaml](docker-compose.yaml); la entrada de despliegue, en el puerto 8091 de [ApiGateway/nginxconf/nginx.conf](src/ApiGateway/nginxconf/nginx.conf), con el destino en [deployment-routes.conf](src/ApiGateway/nginxconf/deployment-routes.conf); con [Kubernetes](https://kubernetes.io/), el `Rollout` de [Argo Rollouts](https://argoproj.github.io/rollouts/) en [k8s/blue-green/rollout.yaml](k8s/blue-green/rollout.yaml) |
| Rolling Upgrade | El alias `users-pool` de `users-service-v2` y las funciones `pool_start_v1` y `pool_replace` de [demos/common.sh](demos/common.sh); [`proxy_next_upstream`](https://nginx.org/en/docs/http/ngx_http_proxy_module.html#proxy_next_upstream) en la entrada de despliegue; con Kubernetes, el Deployment de [k8s/rolling-upgrade/deployment.yaml](k8s/rolling-upgrade/deployment.yaml) y el `Rollout` con análisis de [k8s/rolling-upgrade/rollout.yaml](k8s/rolling-upgrade/rollout.yaml) |
| Canary Testing | [`split_clients`](https://nginx.org/en/docs/http/ngx_http_split_clients_module.html#split_clients) en [deployment-routes.conf](src/ApiGateway/nginxconf/deployment-routes.conf), escrito por la función `deploy_canary` de [demos/common.sh](demos/common.sh); el encabezado `X-Version`, en [UsersService/Release.cs](src/UsersService/Release.cs); con Kubernetes, el `Rollout` de [k8s/canary/rollout.yaml](k8s/canary/rollout.yaml), que cambia los pesos de una ruta de [Gateway API](https://gateway-api.sigs.k8s.io/) y decide con el análisis de [k8s/base/analysis-error-rate.yaml](k8s/base/analysis-error-rate.yaml) |
| A/B Testing | La clase `Experiment`, en [UsersService/Experiment.cs](src/UsersService/Experiment.cs), usada en el método `GetUser` de la clase `UserService`, en [UsersService/Soap/UserService.cs](src/UsersService/Soap/UserService.cs); le pregunta la variante a [OpenFeature](https://openfeature.dev/), con el proveedor de [Unleash](https://www.getunleash.io/) registrado en [UsersService/Program.cs](src/UsersService/Program.cs); el *flag* lo crean las funciones `experiment_start` y `experiment_split` de [demos/common.sh](demos/common.sh) |
| Valet Key | Los métodos `GetUploadKeyAsync` y `GetDownloadKeyAsync` de la clase `ProductImages`, en [ProductsService/ProductImages.cs](src/ProductsService/ProductImages.cs), usados en los métodos `GetImageUploadUrl` y `GetImage` de la clase `ProductsController`, en [ProductsService/Controllers/ProductsController.cs](src/ProductsService/Controllers/ProductsController.cs) |

Qué problema resuelve cada patrón, cómo se implementó y qué muestra cada prueba
está en las secciones [Demos de disponibilidad](PATTERNS.md#demos-de-disponibilidad),
[Demos de rendimiento y procesamiento asíncrono](PATTERNS.md#demos-de-rendimiento-y-procesamiento-asíncrono),
[Demos de seguridad](PATTERNS.md#demos-de-seguridad),
[Demos de facilidad de modificación](PATTERNS.md#demos-de-facilidad-de-modificación) y
[Demos de facilidad de despliegue](PATTERNS.md#demos-de-facilidad-de-despliegue)
de PATTERNS.md.

## Cómo probar los patrones

Cada patrón tiene un script en [demos](demos/) que provoca la situación que
lo activa, como un servicio que falla, que está lento o que recibe demasiadas
solicitudes, y muestra el resultado.

Las instrucciones para ejecutar estos scripts y la explicación de lo que sucede
en cada uno está en [02-availability.azcli](commands/02-availability.azcli),
[03-performance.azcli](commands/03-performance.azcli),
[04-security.azcli](commands/04-security.azcli),
[05-modifiability.azcli](commands/05-modifiability.azcli) y
[06-deployability.azcli](commands/06-deployability.azcli).

Mientras corren las pruebas, también se puede mirar:

* La salud de los servicios en vivo, en otra terminal: `./demos/monitor-health.sh`.
* Los eventos de los patrones en los logs de [Docker](https://www.docker.com/):
  `docker compose logs -f orders-service orders-worker orders-projector products-service`.
* Las colas, sus consumidores y los enlaces del exchange `andis.events` en la
  consola de [RabbitMQ](https://www.rabbitmq.com/): <http://localhost:15672>, usuario y contraseña `guest`.
* El tamaño de cada cambio: `./demos/measure-change-size.sh`, explicado en
  [Tamaño del cambio](PATTERNS.md#tamaño-del-cambio), en PATTERNS.md.
* La configuración central en la consola de [Consul](https://developer.hashicorp.com/consul): <http://localhost:8500>.
* Las colas del ERP en la consola de [ActiveMQ](https://activemq.apache.org/): <http://localhost:8161/admin/>,
  usuario y contraseña `admin`.
* Qué instancia y qué versión de `Users` atendió cada solicitud de la entrada de
  despliegue: `docker compose logs -f api-gateway | grep DEPLOY`.
* El *flag* del experimento de A/B Testing en la consola de
  [Unleash](https://www.getunleash.io/): <http://localhost:4242>, usuario `admin`
  y contraseña `unleash4all`.
* Con Kubernetes, cada versión de `Users` y su papel en el despliegue, en vivo:
  `kubectl argo rollouts get rollout users -n andis --watch`, de [Argo
  Rollouts](https://argoproj.github.io/rollouts/).

### Tests de las demos

Los scripts muestran lo que pasa, pero no fallan si un patrón deja de
funcionar. Para eso están los tests de integración de
[tests/DemoTests](tests/DemoTests/), con [NUnit](https://nunit.org/): al menos
uno por demo, que hace lo mismo que su script y verifica el resultado. Usan las
mismas funciones de [demos/common.sh](demos/common.sh) para detener
contenedores, cambiar las rutas del gateway o escalar réplicas.

Necesitan el [SDK de .NET](https://dotnet.microsoft.com/download) 10 y la
aplicación levantada; los tests no levantan los contenedores:

```bash
docker compose up -d --build --wait
dotnet test tests/DemoTests --filter "TestCategory!=Gatekeeper&TestCategory!=Kubernetes"
```

Los tests corren de a uno, en el orden de las demos, porque comparten el estado
de la aplicación. Cada uno deja los servicios como estaban: sin fallas simuladas,
con todos los patrones prendidos y con los nombres de ejemplo de los `Users` y
los `Products`. Las `Orders` que crean son del `User` 2, así que las del `User` 1
no cambian. Algunos tienen una categoría, para elegir cuáles correr:

| Categoría | Qué hacen | Qué necesitan |
| --------- | --------- | ------------- |
| Sin categoría | Cambian datos, pero no los contenedores | `docker compose up` |
| `Disruptive` | Detienen, escalan o recrean contenedores | `docker compose up` |
| `FeatureFlags` | Levantan [Unleash](https://www.getunleash.io/), con el perfil `feature-flags` | `docker compose up` |
| `Gatekeeper` | Recrean los servicios con [docker-compose.gatekeeper.yaml](docker-compose.gatekeeper.yaml) y los vuelven a la normalidad; tarda varios minutos | `docker compose up` |
| `Kubernetes` | Las demos 31 a 33; si no está el clúster, se ignoran | `./demos/k8s-up.sh` |

Para correr solo los tests de una demo, por ejemplo la de Retry:
`dotnet test tests/DemoTests --filter "FullyQualifiedName~Demo01"`.

## Observabilidad y pruebas de carga

Los scripts de [demos](demos/) muestran cada patrón paso a paso, con pocas
solicitudes; la evidencia en los logs de [Docker](https://www.docker.com/) se ve con los comandos de
los archivos de [commands](commands/). Para ver cómo se comportan los patrones
bajo carga y a lo largo del tiempo, la aplicación tiene además un [perfil](https://docs.docker.com/compose/how-tos/profiles/)
opcional de observabilidad y escenarios de carga con [k6](https://k6.io).

| Componente | Responsabilidad | Tecnología | Puerto en el host |
| ---------- | --------------- | ---------- | ----------------- |
| `prometheus` | Consulta cada 5 segundos el endpoint `/metrics` de los servicios, de cada réplica de `orders-worker` y de `users-service-v2`, de [RabbitMQ](https://www.rabbitmq.com/), del *sidecar* de `Products` y de [Keycloak](https://www.keycloak.org/); recibe las métricas de k6 | [Prometheus](https://prometheus.io/) 3.15 | 9090 |
| `grafana` | Dashboard con todo lo anterior, ya configurado y sin login | [Grafana](https://grafana.com/oss/grafana/) 13.2 | 3000 |
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

Los servicios exponen sus métricas en `GET /metrics`, con [OpenTelemetry](https://opentelemetry.io/) y el
exporter de [Prometheus](https://prometheus.io/), configurado en el método `AddObservability` de la clase
`ObservabilityExtensions`, en [Common/Observability.cs](src/Common/Observability.cs). Casi todas las métricas ya
las publican las bibliotecas que usa la aplicación; no hubo que instrumentar el
código a mano:

| Métrica | Quién la publica | Qué muestra |
| ------- | ---------------- | ----------- |
| `http_server_request_duration_seconds` | [ASP.NET Core](https://dotnet.microsoft.com/apps/aspnet) | Solicitudes recibidas, latencia y código [HTTP](https://datatracker.ietf.org/doc/html/rfc9110), por ruta |
| `aspnetcore_rate_limiting_*` | ASP.NET Core | Throttling: consultas en curso, aceptadas y rechazadas |
| `resilience_polly_strategy_events_total` | [Polly](https://www.pollydocs.org/) | Retry, Circuit Breaker, Bulkhead y timeouts: cada evento, por dependencia |
| `resilience_polly_pipeline_duration_milliseconds` | Polly | Duración de cada llamada a `Users` y `Products`, con los reintentos incluidos |
| `rabbitmq_detailed_queue_*` | [RabbitMQ](https://www.rabbitmq.com/) | Mensajes esperando y en proceso en la cola, y consumidores conectados |
| `orders_processed_total` | La clase `OrderProcessingWorker`, en [OrdersService/Messaging/OrderProcessingWorker.cs](src/OrdersService/Messaging/OrderProcessingWorker.cs) | `Orders` procesadas por cada réplica, con su resultado |
| `outbox_pending`, `outbox_published_total` | La clase `OutboxRelay`, en [OrdersService/Messaging/OutboxRelay.cs](src/OrdersService/Messaging/OutboxRelay.cs) | Mensajes del *outbox* que esperan ser publicados, y los publicados |
| `products_cache_total` | La clase `ProductCache`, en [ProductsService/ProductCache.cs](src/ProductsService/ProductCache.cs) | Lecturas de la caché de `Products`: `hit`, `miss`, `error` o `disabled` |
| `chaos_failure_rate`, `chaos_delay_seconds` | El método `UseChaos` de la clase `ChaosExtensions`, en [Common/Chaos.cs](src/Common/Chaos.cs) | Las fallas inyectadas, para ver en qué momento empezaron y terminaron |
| `orders_patterns_enabled` | El método `MapPatterns` de la clase `PatternExtensions`, en [OrdersService/Patterns.cs](src/OrdersService/Patterns.cs) | Qué patrones de resiliencia están prendidos en `Orders`: 1 prendido, 0 apagado |
| `service_health` | La clase `HealthMetricsPublisher`, en [Common/HealthMetrics.cs](src/Common/HealthMetrics.cs) | Salud de cada servicio y de cada uno de sus chequeos, la misma que responde `/health`: 0 Unhealthy, 1 Degraded, 2 Healthy |
| `orders_circuit_state` | El método `MapPatterns` de la clase `PatternExtensions`, en [OrdersService/Patterns.cs](src/OrdersService/Patterns.cs) | Estado del circuito de cada dependencia de `Orders`: -1 con el Circuit Breaker apagado, 0 cerrado, 1 semiabierto, 2 abierto |
| `products_images_received_bytes_total`, `products_images_valet_keys_total` | La clase `ProductImages`, en [ProductsService/ProductImages.cs](src/ProductsService/ProductImages.cs) | Bytes de imágenes que recibió `Products`, sin Valet Key, y URL firmadas que entregó, por permiso |
| `envoy_http_rbac_*`, `envoy_http_jwt_authn_*` | El *sidecar* de `Products` | Solicitudes permitidas y rechazadas por la autorización, y tokens aceptados y rechazados |
| `users_experiment_total` | La clase `Experiment`, en [UsersService/Experiment.cs](src/UsersService/Experiment.cs) | Respuestas de la versión `v2` de `Users` por variante del experimento y por segmento |
| `config_value` | La clase `ConsulConfigurationPoller`, en [Common/ExternalConfiguration.cs](src/Common/ExternalConfiguration.cs) | Valor vigente de cada clave numérica del almacén, en cada proceso |
| `keycloak_user_events_total` | [Keycloak](https://www.keycloak.org/) | Inicios de sesión y validaciones de tokens, con su error si lo hubo |
| `k6_*` | [k6](https://k6.io/) | La carga generada y lo que ve el cliente: solicitudes por segundo por código HTTP, latencia por endpoint |
| `k6_strangler_requests_total`, `k6_strangler_details_duration_*` | Los escenarios de k6 | Solicitudes de la fachada por destino, según `X-Served-By`, y latencia del detalle |
| `k6_acl_duration_*`, `k6_bff_requests_total`, `k6_config_requests_total` | Los escenarios de k6 | Latencia de `Orders` y de `Purchases`, respuestas de cada BFF, y respuestas de `Orders` por operación y resultado |
| `k6_deploy_requests_total` | Los escenarios de k6 | Respuestas de la entrada de despliegue por versión de `Users`, según `X-Version`, y por código HTTP |

> [!TIP]
>
> Consulta [.NET Observability with OpenTelemetry](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/observability-with-otel)
> para tener más información sobre cómo `AddObservability` configura las
> métricas, y [Creating Metrics](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/metrics-instrumentation)
> sobre los contadores y los *gauges* propios de la aplicación, como
> `service_health` y `orders_patterns_enabled`.

### Escenarios de carga

Los escenarios están en [k6](k6/). Cada uno inyecta las fallas que necesita y
las quita al terminar. Al principio de cada archivo se explica qué mirar en el
dashboard.

| Escenario | Qué hace | Qué hay que observar en [Grafana](https://grafana.com/oss/grafana/) |
| --------- | -------- | ------------------------------- |
| `circuit-breaker.js` | 5 consultas de detalle por segundo durante 80 segundos; `Products` falla siempre entre los 15 segundos y los 50 segundos | La latencia sube por los reintentos y vuelve a bajar cuando el circuito se abre; las respuestas siguen siendo 200, degradadas; el circuito se cierra después de la recuperación |
| `bulkhead.js` | `Products` demora 1,5 segundos; 6 consultas de detalle por segundo durante 45 segundos | Rechazos del bulkhead solo en `Products`; `Users` sin rechazos |
| `throttling.js` | `Orders` demora 1 segundo; los usuarios virtuales suben de 0 a 40 | Las consultas en curso se estabilizan en 20 y el excedente recibe 503 |
| `rate-limiting.js` | 5 `POST /orders/` por segundo durante 30 segundos, sin Rate Limiting; después, 1 por segundo durante 30 segundos, con Rate Limiting; 5 `GET /orders/` por segundo todo el tiempo | En la primera fase pasa un POST por segundo y el resto recibe 429; en la segunda no hay 429; los GET siempre responden 200 |
| `competing-consumers.js` | Crea 60 `Orders` de golpe | La cola crece y se vacía a unas 1,5 `Orders` por segundo, repartidas entre las 3 réplicas |
| `load.js` | Carga base para las demos de rendimiento: lecturas mezcladas a una tasa configurable; con `-e DETAILS=view`, el detalle se lee de la vista | Throughput y latencia por servicio; [k6](https://k6.io/) termina con error si no se cumple el objetivo de p95 menor a 500 ms con menos de 1% de errores; comparando las dos corridas, la latencia de `details` contra la de `details view` |
| `cache-aside.js` | 50 lecturas de `Products` por segundo, con una base que demora 100 ms: 30 segundos con la caché apagada y 30 segundos prendida | La latencia de `product` baja de unos 100 ms a unos 3 ms; las lecturas de la caché pasan de `disabled` a casi todas `hit` |
| `load-leveling.js` | 10 `POST /orders/` por segundo durante 10 segundos, directamente en `orders-service` | La latencia de `create order` se mantiene baja; la cola `orders.process` crece durante el pico y se vacía a ritmo constante |

| `strangler-fig.js` | 15 lecturas por segundo a la fachada, mientras se migran las rutas | El tráfico pasa del monolito a los servicios, sin errores; la latencia del detalle sube |
| `external-configuration.js` | 2 `POST /orders/` y 20 lecturas por segundo, mientras se cambia un límite por variable de entorno y en el almacén | Solo la variable de entorno deja solicitudes sin respuesta |
| `messaging-bridge.js` | 2 `Orders` por segundo, mientras se detiene [ActiveMQ](https://activemq.apache.org/) | La cola `bridge.erp` crece y se vacía; las `Orders` se siguen confirmando |
| `anti-corruption-layer.js` | 10 lecturas por segundo, mitad a `Orders` y mitad a `Purchases`, mientras se demora `Users` | `Purchases` tarda más que `Orders`; con `Users` demorado, solo sube `Purchases` |
| `backends-for-frontends.js` | 20 lecturas por segundo, mitad por cada BFF, mientras se redespliega `bff-mobile` | La web sigue respondiendo `200 OK`; la app puede recibir algún error |
| `blue-green.js` | 20 lecturas de `Users` por segundo a la entrada de despliegue, mientras se conmuta a la versión `v2` y se vuelve atrás | El tráfico pasa de una versión a la otra de una vez, sin errores |
| `rolling-upgrade.js` | Lo mismo, mientras se reemplazan tres instancias de la versión `v1` en tres lotes | La versión `v2` sube en escalones, sin errores |
| `canary.js` | Lo mismo, mientras la versión `v2` recibe el 10% y el 50%, falla y se vuelve atrás | La versión `v2` sigue al porcentaje; los errores son solo suyos |
| `ab-testing.js` | 20 lecturas por segundo de los dos `Users` a la versión `v2`; la mitad de las de bob, con `X-Segment: beta` | Cada `User` recibe siempre su variante, salvo con el segmento |

`blue-green.js`, `rolling-upgrade.js` y `canary.js` también sirven para las
demos 31 a 33, con [Kubernetes](https://kubernetes.io/): el script los apunta al
clúster, y el panel "Errores de cada versión, según Users" muestra lo que mide
el análisis de Argo Rollouts.

Los últimos nueve necesitan que alguien haga el cambio mientras corren. Los de
facilidad de modificación se lanzan con
[demos/load-modifiability.sh](demos/load-modifiability.sh), y los de facilidad de
despliegue, con [demos/load-deployment.sh](demos/load-deployment.sh). Los dos
marcan cada cambio en el dashboard:

```bash
./demos/load-modifiability.sh strangler   # también: acl, config, bridge, bff
./demos/load-deployment.sh canary         # también: bluegreen, rolling, ab
./demos/load-deployment.sh k8s-canary     # también: k8s-bluegreen, k8s-rolling
```

`load.js` acepta la tasa y la duración como parámetros:

```bash
docker compose run --rm k6 run -e RATE=50 -e DURATION=2m /scripts/load.js
```

Para que la escala de tiempo del dashboard sea legible, conviene esperar unos
segundos entre un escenario y el siguiente.
