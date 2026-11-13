<!-- markdownlint-disable-next-line MD033 MD041 -->
<img alt="UCU" src="https://www.ucu.edu.uy/plantillas/images/logo_ucu.svg"
width="150"/>

# Universidad Católica del Uruguay

## Análisis y diseño de aplicaciones II

# Demo de patrones de arquitectura en una arquitectura de microservicios

[![Deploy to
Pages](https://github.com/ucudal/ANDIS2_UT4_DEMO1/actions/workflows/pages.yaml/badge.svg)](https://github.com/ucudal/ANDIS2_UT4_DEMO1/actions/workflows/pages.yaml)

> [!IMPORTANT]
> Comienza consultando 📖 [esta
> documentación](https://ucudal.github.io/ANDIS2_UT4_DEMO1), que te ayudará a
> entender los patrones navegando por el código. Las clases de esta demo están
> documentadas para explicar qué patrón implementan.

Esta demo es la aplicación base del tema de patrones de arquitectura, una
pequeña aplicación de comercio electrónico, con los patrones de disponibilidad,
rendimiento, seguridad, facilidad de modificación y eficiencia energética, y las
estrategias de despliegue, implementados.

También es una demo de arquitectura de microservicios. La descripción de la
arquitectura esta en [ARCHITECTURE.md](ARCHITECTURE.md).

El panorama de la demo y la explicación de cada patrón está en
[PATTERNS.md](PATTERNS.md).

## Requisitos

| Herramienta | Para qué | macOS | Windows |
| ----------- | -------- | ----- | ------- |
| [Docker](https://www.docker.com/) | Levantar la aplicación | Docker Desktop[^docker] | Docker Desktop[^docker] |
| `bash` y `curl` | Los comandos de los archivos de [commands](commands/) y los scripts de [demos](demos/) | Incluidos | Git Bash[^gitbash] o WSL[^wsl]; PowerShell y `cmd` no sirven, porque los comandos usan sintaxis de `bash` |
| `jq`[^jq] | Formatear las respuestas [JSON](https://www.json.org/) y convertirlas en tablas con la función `table` de los archivos de `commands/` | Incluido | En WSL, `sudo apt install jq`; en Git Bash, `winget install jqlang.jq` y volver a abrir la terminal |
| [`column`](https://man7.org/linux/man-pages/man1/column.1.html) | Alinear las columnas de esas tablas | Incluido | En WSL, incluido en la mayoría de las distribuciones; si falta, `sudo apt install bsdextrautils`; ver la nota sobre Git Bash |
| [`xmllint`](https://gnome.pages.gitlab.gnome.org/libxml2/xmllint.html) | Indentar las respuestas [SOAP](https://www.w3.org/TR/soap/) de `Users` en `commands/01-api-gateway.azcli` y los errores de [Azurite](https://github.com/Azure/Azurite) en `commands/04-security.azcli` | Incluido | En WSL, `sudo apt install libxml2-utils`; en Git Bash no viene incluido: usar WSL o ver el [XML](https://www.w3.org/XML/) sin indentar |
| Visual Studio Code[^vscode] con la extensión Azure CLI Tools[^azcli] | Opcional: abrir los archivos de [commands](commands/) con resaltado de sintaxis y ejecutar cada comando en la terminal | Instalar Visual Studio Code y, dentro de él, la extensión | Igual que en macOS |
| kind[^kind], kubectl[^kubectl] y el plugin de Argo Rollouts para kubectl[^argorollouts] | Solo para las demos con [Kubernetes](https://kubernetes.io/): las de despliegue, de la 32 a la 34, y las de eficiencia energética, de la 35 a la 37. Crear el clúster y manejar los despliegues | `brew install kind kubectl argoproj/tap/kubectl-argo-rollouts` | En WSL, con los binarios para Linux de cada sitio |
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

Las demos de eficiencia energética, de la 35 a la 37, necesitan a la vez Docker
Compose y el clúster de Kubernetes: Docker necesita al menos 12 GB de memoria,
en Docker Desktop, en Settings, Resources. Para las demás alcanza con 8 GB.

`jq` y `column` solo hacen falta para ver las respuestas como tabla. Los scripts
de `demos/` también los usan, pero si falta alguno muestran el JSON tal cual;
las excepciones son `03-bulkhead.sh`, `04-throttling.sh`, `05-rate-limiting.sh`
y los demos del `09` en adelante, que necesitan `jq`.

En Windows conviene usar WSL, porque se comporta igual que Linux y macOS. Si Git
Bash no tiene `column`, la función `table` falla con `column: command not
found`. En ese caso hay dos opciones:

* Quitar `| column -t -s $'\t'` de la función: se ven los mismos datos, con las
  columnas separadas por tabuladores pero sin alinear.
* Usar `jq .` en lugar de `table`, que muestra el JSON formateado y con colores.

## Cómo ejecutarla

Los comandos están en la carpeta [commands](commands/), en siete archivos:

* [01-api-gateway.azcli](commands/01-api-gateway.azcli): probar los servicios a
  través del API Gateway.
* [02-availability.azcli](commands/02-availability.azcli): demos de los
  patrones de disponibilidad, de la 01 a la 10.
* [03-performance.azcli](commands/03-performance.azcli): demos de los
  patrones de rendimiento, de la 11 a la 17.
* [04-security.azcli](commands/04-security.azcli): demos de los patrones de
  seguridad, de la 18 a la 22.
* [05-modifiability.azcli](commands/05-modifiability.azcli): demos de los
  patrones de facilidad de modificación, de la 23 a la 27.
* [06-deployability.azcli](commands/06-deployability.azcli): demos de las
  estrategias de despliegue, de la 28 a la 34. Las demos 32 a 34 corren en un
  clúster de [Kubernetes](https://kubernetes.io/), que se crea con
  `./demos/k8s-up.sh`, y no usan Docker Compose: la entrada de los clientes al
  clúster está en el puerto 8092, y su Prometheus, en el 30090. Qué es cada
  concepto de Kubernetes, y su análogo en Docker Compose, está en [Conceptos de
  Kubernetes](PATTERNS.md#conceptos-de-kubernetes), en PATTERNS.md.
* [07-energy-efficiency.azcli](commands/07-energy-efficiency.azcli): demos de
  los patrones de eficiencia energética, de la 35 a la 37. El *worker* de
  `Orders` corre en el mismo clúster, y usa los servicios de Docker Compose.

Cada archivo empieza levantando todos los servicios y termina con el *tear
down*, así que se puede usar por separado. En resumen:

```bash
docker compose up -d --build --wait
curl http://localhost/orders/user/665f1c2ab4e8d9a0c1f2e301/details
```

El comando [`docker compose
up`](https://docs.docker.com/reference/cli/docker/compose/up/) con `--wait`
espera a que todos los contenedores estén sanos según su
[`healthcheck`](https://docs.docker.com/reference/compose-file/services/#healthcheck).
Qué es cada concepto de Docker que usan las demos -imagen, contenedor,
servicio, réplicas, red, volumen, perfil- está en [Contenedores y Docker
Compose](PATTERNS.md#contenedores-y-docker-compose), en PATTERNS.md.

Las bases de datos se crean con datos de ejemplo la primera vez que se levantan
los contenedores, con los scripts de `src/*/mysqlinit`, `src/*/mongoinit` y
`src/*/pginit`. Para volver a empezar desde cero hay que borrar las carpetas
`mysqldata`, `mongodbdata` y `pgdata`; los comandos están al final de cada
archivo de [`commands`](commands/). Lo mismo hace falta si cambia uno de esos
scripts, como `mysqlinit.sql`: con la carpeta ya creada, no se vuelven a
ejecutar.

## Conectarse a las bases de datos

Con los contenedores levantados, cada base se puede abrir desde la extensión
Database Client o desde cualquier otro cliente, con estos datos:

| Servicio | Motor | Host | Puerto | Base | Usuario | Contraseña |
| -------- | ----- | ---- | ------ | ---- | ------- | ---------- |
| `Orders` | [MySQL](https://www.mysql.com/) | `localhost` | `3306` | `ordersdb` | `ordersusr` | `Pa55w0rd` |
| `Users` | [MongoDB](https://www.mongodb.com/) | `localhost` | `27017` | `usersdb` | `usersusr` | `Pa55w0rd` |
| `Products` | [PostgreSQL](https://www.postgresql.org/) | `localhost` | `5432` | `productsdb` | `productsusr` | `Pa55w0rd` |
| Caché de `Products` | [Redis](https://redis.io/) | `localhost` | `6379` | — | — | — |

En MongoDB el usuario `usersusr` se autentica contra la base `usersdb`, así
que en el campo de base de autenticación -`authSource`- va `usersdb` y no
`admin`. La cadena de conexión completa es
`mongodb://usersusr:Pa55w0rd@localhost:27017/usersdb?authSource=usersdb`.
Para ver todas las bases de MongoDB está el usuario `root`, con la misma
contraseña y `admin` como base de autenticación.

Redis no tiene usuario ni contraseña.

La consola de administración de [Keycloak](https://www.keycloak.org/) está en <http://localhost:8080>, con
el usuario `admin` y la contraseña `Pa55w0rd`. Los usuarios de la aplicación
-alice, bob y carol, con la misma contraseña- están en el realm `andis`.

## Cómo probar los patrones

Cada patrón tiene un script en [demos](demos/) que provoca la situación que
lo activa, como un servicio que falla, que está lento o que recibe demasiadas
solicitudes, y muestra el resultado.

Las instrucciones para ejecutar estos scripts y la explicación de lo que sucede
en cada uno está en [02-availability.azcli](commands/02-availability.azcli),
[03-performance.azcli](commands/03-performance.azcli),
[04-security.azcli](commands/04-security.azcli),
[05-modifiability.azcli](commands/05-modifiability.azcli),
[06-deployability.azcli](commands/06-deployability.azcli) y
[07-energy-efficiency.azcli](commands/07-energy-efficiency.azcli).

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
* Con [Kubernetes](https://kubernetes.io/), cada versión de `Users` y su papel en el despliegue, en vivo:
  `kubectl argo rollouts get rollout users -n andis --watch`, de [Argo
  Rollouts](https://argoproj.github.io/rollouts/).
* Las réplicas del *worker* de `Orders` en el clúster, que cambia
  [KEDA](https://keda.sh/), en vivo: `kubectl get deployment orders-worker -n andis --watch`.
* La potencia estimada de cada pod, en el Prometheus del clúster:
  <http://localhost:30090>, con la consulta `pod:power:watts`.

### Tests de las demos

Los scripts muestran lo que pasa, pero no fallan si un patrón deja de
funcionar. Para eso están los tests de integración de
[DemoTests](tests/DemoTests/), con [NUnit](https://nunit.org/): al menos
uno por demo, que hace lo mismo que su script y verifica el resultado. Usan las
mismas funciones de [common.sh](demos/common.sh) para detener
contenedores, cambiar las rutas del gateway o escalar réplicas.

Necesitan el [SDK de .NET](https://dotnet.microsoft.com/download) 10 y la
aplicación levantada; los tests no levantan los contenedores:

```bash
docker compose up -d --build --wait
dotnet test tests/DemoTests --filter "TestCategory!=Gatekeeper&TestCategory!=Kubernetes"
```

Los tests corren de a uno, porque comparten el estado de la aplicación. Cada uno deja los servicios como estaban: sin fallas simuladas,
con todos los patrones prendidos y con los nombres de ejemplo de los `Users` y
los `Products`. Las `Orders` que crean son del `User` 2, así que las del `User` 1
no cambian. Algunos tienen una categoría, para elegir cuáles correr:

| Categoría | Qué hacen | Qué necesitan |
| --------- | --------- | ------------- |
| Sin categoría | Cambian datos, pero no los contenedores | `docker compose up` |
| `Disruptive` | Detienen, escalan o recrean contenedores | `docker compose up` |
| `FeatureFlags` | Levantan [Unleash](https://www.getunleash.io/), con el perfil `feature-flags` | `docker compose up` |
| `Gatekeeper` | Recrean los servicios con [docker-compose.gatekeeper.yaml](docker-compose.gatekeeper.yaml) y los vuelven a la normalidad; tarda varios minutos | `docker compose up` |
| `Kubernetes` | Las demos 32 a 37; si no está el clúster, se ignoran. Las de la 35 a la 37 tardan unos 2 minutos | `./demos/k8s-up.sh`; las de la 35 a la 37, también `docker compose up` |

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
`ObservabilityExtensions`, en [Observability.cs](src/Common/Observability.cs). Casi todas las métricas ya
las publican las bibliotecas que usa la aplicación; no hubo que instrumentar el
código a mano:

| Métrica | Quién la publica | Qué muestra |
| ------- | ---------------- | ----------- |
| `http_server_request_duration_seconds` | [ASP.NET Core](https://dotnet.microsoft.com/apps/aspnet) | Solicitudes recibidas, latencia y código [HTTP](https://datatracker.ietf.org/doc/html/rfc9110), por ruta |
| `aspnetcore_rate_limiting_*` | ASP.NET Core | Throttling: consultas en curso, aceptadas y rechazadas |
| `resilience_polly_strategy_events_total` | [Polly](https://www.pollydocs.org/) | Retry, Circuit Breaker, Bulkhead y timeouts: cada evento, por dependencia |
| `resilience_polly_pipeline_duration_milliseconds` | Polly | Duración de cada llamada a `Users` y `Products`, con los reintentos incluidos |
| `rabbitmq_detailed_queue_*` | [RabbitMQ](https://www.rabbitmq.com/) | Mensajes esperando y en proceso en la cola, y consumidores conectados |
| `orders_processed_total` | La clase `OrderProcessingWorker`, en [OrderProcessingWorker.cs](src/OrdersService/Messaging/OrderProcessingWorker.cs) | `Orders` procesadas por cada réplica, con su resultado |
| `outbox_pending`, `outbox_published_total` | La clase `OutboxRelay`, en [OutboxRelay.cs](src/Common/OutboxRelay.cs) | Mensajes del *outbox* que esperan ser publicados, y los publicados, en `orders-service` y en `products-service` |
| `products_cache_total` | La clase `ProductCache`, en [ProductCache.cs](src/ProductsService/ProductCache.cs) | Lecturas de la caché de `Products`: `hit`, `miss`, `error` o `disabled` |
| `products_stock`, `products_stock_reservations_total` | La clase `StockMetrics`, en [StockMetrics.cs](src/ProductsService/StockMetrics.cs) | Unidades disponibles de cada `Product`, y las reservas de la Saga por resultado: `Reserved`, `Rejected` o `Released` |
| `chaos_failure_rate`, `chaos_delay_seconds` | El método `UseChaos` de la clase `ChaosExtensions`, en [Chaos.cs](src/Common/Chaos.cs) | Las fallas inyectadas, para ver en qué momento empezaron y terminaron |
| `orders_patterns_enabled` | El método `MapPatterns` de la clase `PatternExtensions`, en [Patterns.cs](src/OrdersService/Patterns.cs) | Qué patrones de resiliencia están prendidos en `Orders`: 1 prendido, 0 apagado |
| `service_health` | La clase `HealthMetricsPublisher`, en [HealthMetrics.cs](src/Common/HealthMetrics.cs) | Salud de cada servicio y de cada uno de sus chequeos, la misma que responde `/health`: 0 Unhealthy, 1 Degraded, 2 Healthy |
| `orders_circuit_state` | El método `MapPatterns` de la clase `PatternExtensions`, en [Patterns.cs](src/OrdersService/Patterns.cs) | Estado del circuito de cada dependencia de `Orders`: -1 con el Circuit Breaker apagado, 0 cerrado, 1 semiabierto, 2 abierto |
| `products_images_received_bytes_total`, `products_images_valet_keys_total` | La clase `ProductImages`, en [ProductImages.cs](src/ProductsService/ProductImages.cs) | Bytes de imágenes que recibió `Products`, sin Valet Key, y URL firmadas que entregó, por permiso |
| `envoy_http_rbac_*`, `envoy_http_jwt_authn_*` | El *sidecar* de `Products` | Solicitudes permitidas y rechazadas por la autorización, y tokens aceptados y rechazados |
| `users_experiment_total` | La clase `Experiment`, en [Experiment.cs](src/UsersService/Experiment.cs) | Respuestas de la versión `v2` de `Users` por variante del experimento y por segmento |
| `config_value` | La clase `ConsulConfigurationPoller`, en [ExternalConfiguration.cs](src/Common/ExternalConfiguration.cs) | Valor vigente de cada clave numérica del almacén, en cada proceso |
| `keycloak_user_events_total` | [Keycloak](https://www.keycloak.org/) | Inicios de sesión y validaciones de tokens, con su error si lo hubo |
| `k6_*` | [k6](https://k6.io/) | La carga generada y lo que ve el cliente: solicitudes por segundo por código HTTP, latencia por endpoint |
| `k6_strangler_requests_total`, `k6_strangler_details_duration_*` | Los escenarios de k6 | Solicitudes de la fachada por destino, según `X-Served-By`, y latencia del detalle |
| `k6_acl_duration_*`, `k6_bff_requests_total`, `k6_config_requests_total` | Los escenarios de k6 | Latencia de `Orders` y de `Purchases`, respuestas de cada BFF, y respuestas de `Orders` por operación y resultado |
| `k6_deploy_requests_total` | Los escenarios de k6 | Respuestas de la entrada de despliegue por versión de `Users`, según `X-Version`, y por código HTTP |

El Prometheus del clúster de [Kubernetes](https://kubernetes.io/), en <http://localhost:30090>, tiene
además las métricas de las demos de eficiencia energética:

| Métrica | Quién la publica | Qué muestra |
| ------- | ---------------- | ----------- |
| `container_cpu_usage_seconds_total` | [cAdvisor](https://github.com/google/cadvisor), en el kubelet | Segundos de CPU que usó cada contenedor |
| `kube_pod_container_resource_requests` | [kube-state-metrics](https://github.com/kubernetes/kube-state-metrics) | CPU y memoria que reserva cada contenedor |
| `kube_deployment_status_replicas_ready` | kube-state-metrics | Réplicas listas de cada [Deployment](https://kubernetes.io/docs/concepts/workloads/controllers/deployment/) |
| `pod:cpu_used:vcpu`, `pod:cpu_reserved:vcpu`, `pod:power:watts` | Las reglas de `energy.rules.yml`, en [prometheus.yaml](k8s/base/prometheus.yaml) | vCPU que usa y que reserva cada pod, y su potencia estimada con el modelo de [Cloud Carbon Footprint](https://www.cloudcarbonfootprint.org/docs/methodology/) |

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
| `bulkhead.js` | `Products` demora 1.5 segundos; 6 consultas de detalle por segundo durante 45 segundos | Rechazos del bulkhead solo en `Products`; `Users` sin rechazos |
| `throttling.js` | `Orders` demora 1 segundo; los usuarios virtuales suben de 0 a 40 | Las consultas en curso se estabilizan en 20 y el excedente recibe 503 |
| `rate-limiting.js` | 5 `POST /orders/` por segundo durante 30 segundos, sin Rate Limiting; después, 1 por segundo durante 30 segundos, con Rate Limiting; 5 `GET /orders/` por segundo todo el tiempo | En la primera fase pasa un POST por segundo y el resto recibe 429; en la segunda no hay 429; los GET siempre responden 200 |
| `competing-consumers.js` | Crea 60 `Orders` de golpe | La cola crece y se vacía a unas 1.5 `Orders` por segundo, repartidas entre las 3 réplicas |
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
| `energy.js` | Tres picos de 30 `POST /orders/`, cada 4 minutos, directamente en `orders-service`; la corrida dura 10 minutos | Las réplicas del *worker* del clúster y su potencia estimada; la energía de toda la corrida, para comparar las configuraciones |

`blue-green.js`, `rolling-upgrade.js` y `canary.js` también sirven para las
demos 32 a 34, con [Kubernetes](https://kubernetes.io/): el script los apunta al
clúster, y el panel "Errores de cada versión, según Users" muestra lo que mide
el análisis de [Argo Rollouts](https://argoproj.github.io/rollouts/).

Los nueve de facilidad de modificación y de despliegue, de `strangler-fig.js`
a `ab-testing.js`, necesitan que alguien haga el cambio mientras corren. Los de
facilidad de modificación se lanzan con
[load-modifiability.sh](demos/load-modifiability.sh), y los de facilidad de
despliegue, con [load-deployment.sh](demos/load-deployment.sh). `energy.js`
necesita el *worker* del clúster en una de tres configuraciones, y se lanza con
[load-energy.sh](demos/load-energy.sh); hay que esperar los 10 minutos de la
corrida para comparar la energía. Los tres scripts marcan cada cambio en el
dashboard:

```bash
./demos/load-modifiability.sh strangler   # también: acl, config, bridge, bff
./demos/load-deployment.sh canary         # también: bluegreen, rolling, ab
./demos/load-deployment.sh k8s-canary     # también: k8s-bluegreen, k8s-rolling
./demos/load-energy.sh queue              # también: fixed, window
```

`load.js` acepta la tasa y la duración como parámetros:

```bash
docker compose run --rm k6 run -e RATE=50 -e DURATION=2m /scripts/load.js
```

Para que la escala de tiempo del dashboard sea legible, conviene esperar unos
segundos entre un escenario y el siguiente.
