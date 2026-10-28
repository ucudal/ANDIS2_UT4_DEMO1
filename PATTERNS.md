# Demos de la UT4: patrones de arquitectura sobre una misma aplicación

## Aplicación base

Todas las demos parten de la misma aplicación de microservicios, que es la de
este repositorio:

| Servicio | Responsabilidad | Base de datos |
| -------- | --------------- | ------------- |
| `users-service` | Alta y consulta de [`Users`](UsersService/Models/Users.cs) | PostgreSQL |
| `products-service` | Catálogo de [`Products`](ProductsService/Models/Product.cs) | MongoDB |
| `orders-service` | Alta y consulta de [`Orders`](OrdersService/Models/Order.cs); arma el detalle de las `Orders` de un `User` consultando a `Users` y `Products` por HTTP | MySQL |
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

## Demo 1: Resiliencia y disponibilidad

**Estado: implementada en este repositorio.**

Escenario: [`Users`](UsersService) o [`Products`](ProductsService) fallan o
responden lento de forma intermitente. [`Orders`](OrdersService) debe seguir
funcionando y el resto del sistema no debe caerse.

Qué se muestra:

- Aislamiento de recursos para que la lentitud de un servicio no agote los
  recursos que usan los demás.
- Degradación controlada del detalle de `Orders` cuando `Products` o `Users` no
  responden.
- Protección contra el exceso de solicitudes, tanto en el borde como en el
  servicio.
- Monitoreo activo de la salud de cada servicio.
- Procesamiento de `Orders` en segundo plano, repartido entre varias réplicas.
- Historial inmutable de eventos de cada `Order`.

Resultado observable: si `Products` se cae, el circuito se abre, `Orders` deja
de esperar *timeouts* y el detalle devuelve las `Orders` con "no disponible
-circuito abierto-" en lugar de un error. `Users` y `Products` siguen atendiendo
sus propias rutas.

### Qué agrega a la aplicación base

| Componente | Para qué |
| ---------- | -------- |
| `rabbitmq` | Cola `orders.process` con las `Orders` pendientes de procesar. Consola en <http://localhost:15672> -usr: `guest` pwd: `guest`-. |
| `orders-worker` -3 réplicas- | La misma imagen que `orders-service`, con el *worker* habilitado -`Worker__Enabled=true`-. Consume la cola y confirma o rechaza cada `Order`. |
| Estado de la [`Order`](OrdersService/Models/Order.cs) | `POST /orders/` guarda la `Order` como `Pending`. Un *worker* verifica que el `User` y el `Product` existan y la pasa a `Confirmed` o `Rejected`. |
| Inyección de fallas | `PUT /users/chaos`, `PUT /products/chaos` y `PUT /orders/chaos` hacen que el servicio falle o demore a propósito, sin reiniciarlo. Así se provocan las situaciones que activan cada patrón. Está en [Common/Chaos.cs](Common/Chaos.cs). |
| Observabilidad y carga -opcional- | Perfil `observability` de Docker Compose con Prometheus y un dashboard de Grafana ya configurado, y escenarios de carga con k6 en [k6/](k6). Muestra los patrones bajo carga y a lo largo del tiempo. Cómo usarlos está en el [README](README.md#observabilidad-y-pruebas-de-carga). |

Ejemplo de inyección de fallas:

```bash
# Products falla en el 50 % de las solicitudes y demora 1 segundo en todas
curl -X PUT http://localhost/products/chaos -H "Content-Type: application/json" \
  -d '{"failureRate": 0.5, "delayMs": 1000}'

# Vuelve a la normalidad
curl -X DELETE http://localhost/products/chaos
```

### Patrones

Cada patrón tiene un script en [demos/](demos) que provoca la situación y
muestra el resultado. Cómo ejecutarlos está en el
[README](README.md#cómo-probar-los-patrones). Los eventos de cada patrón también
aparecen en los *logs* de Docker de `orders-service`, con una etiqueta: `[RETRY]`,
`[CIRCUIT BREAKER]`, `[BULKHEAD]` y `[THROTTLING]`. Los patrones que dependen de
la carga -Circuit Breaker, Bulkhead, Throttling, Rate Limiting y Competing
Consumers- tienen además un escenario de k6 para verlos en el dashboard.

#### Retry

**Problema:** una falla transitoria -un error puntual de red, un servicio que se
reinicia- hace fallar una operación que habría funcionado un momento después.

**Implementación:** los `HttpClient` de `Orders` hacia `Users` y `Products`
reintentan hasta 3 veces con retroceso exponencial -0,5 s, 1 s, 2 s, con
*jitter*-. Solo se reintentan los errores transitorios: 5xx, 408, 429, errores
de red y *timeouts*; un 404 no se reintenta. Está en
[OrdersService/Resilience.cs](OrdersService/Resilience.cs), con
`Microsoft.Extensions.Http.Resilience`.

**Cómo verlo:** el detalle de las `Orders` de un `User` lo arma `Orders`, que le
pide a `Users` el nombre del cliente y a `Products` el nombre del producto de
cada `Order`. [demos/01-retry.sh](demos/01-retry.sh) hace que `Products` falle
en el 20 % de las solicitudes y consulta el detalle 8 veces. Casi todas las
respuestas traen el nombre del producto, porque los reintentos ocultan las
fallas. Las que tardan más son las que necesitaron reintentos. Los *logs* de
Docker de `orders-service` muestran cada `[RETRY]` con su demora y su causa; el comando
para verlos está en [commands.azcli](commands.azcli).

#### Circuit Breaker

**Problema:** si una dependencia está caída, seguir llamándola -y reintentando-
desperdicia recursos, demora las respuestas y la sobrecarga justo cuando intenta
recuperarse.

**Implementación:** un circuito por dependency, en el mismo pipeline que el
Retry. Se abre si fallan al menos la mitad de las llamadas en 10 segundos, con
un mínimo de 5 llamadas. Abierto, las llamadas fallan de inmediato durante 15
segundos; después pasa a semiabierto, deja pasar una llamada de prueba y, si
funciona, se cierra. El detalle de `Orders` no devuelve error: muestra `"no
disponible [circuito abierto]"` en lugar del nombre
-[OrdersController.cs](OrdersService/Controllers/OrdersController.cs),
`GetOrdersForUser`-.

**Cómo verlo:** [demos/02-circuit-breaker.sh](demos/02-circuit-breaker.sh) hace
que `Products` falle en el 100 % de las solicitudes. La primera consulta tarda
unos 3 segundos -reintenta y abre el circuito-; las siguientes responden en
milisegundos con "circuito abierto". Cuando `Products` se recupera, el circuito
sigue abierto hasta que pasan los 15 segundos. Los *logs* de Docker muestran las
transiciones ABIERTO → SEMIABIERTO → CERRADO.

#### Bulkhead

**Problema:** si una dependencia se pone lenta, las llamadas pendientes se
acumulan y consumen los recursos -conexiones, memoria, hilos- que necesitan las
demás funciones.

**Implementación:** un límite de concurrencia por dependencia, en
[OrdersService/Resilience.cs](OrdersService/Resilience.cs). Las llamadas que
exceden el límite se rechazan de inmediato. Como cada dependencia tiene su
propio compartimento, la lentitud de `Products` no afecta las llamadas a
`Users`. Cada compartimento se dimensiona según su dependencia, en
[OrdersService/Program.cs](OrdersService/Program.cs): `Products` tiene 5
llamadas en curso y 5 en espera; `Users`, que responde rápido pero se llama en
cada consulta, tiene 20 en curso y 5 en espera.

**Cómo verlo:** [demos/03-bulkhead.sh](demos/03-bulkhead.sh) hace que `Products`
demore 1,5 segundos y lanza 15 consultas simultáneas. 10 entran al
compartimento de `Products` y traen los nombres de los productos; las otras 5 se
rechazan de inmediato con `"no disponible (bulkhead lleno)"`. El nombre del
`User` aparece en todas las respuestas.

#### Throttling

**Problema:** un pico de carga puede saturar un servicio y degradarlo para todos
sus clientes.

**Implementación:** `Orders` atiende como máximo 20 consultas de detalle a la
vez. Las que exceden el límite reciben `503 Service Unavailable` con
`Retry-After: 2`, en lugar de encolarse. Está en
[OrdersService/Program.cs](OrdersService/Program.cs) -`AddRateLimiter`, política
`throttling`- y se aplica con `[EnableRateLimiting("throttling")]` en el
controlador.

El gateway también aplica Throttling, pero por cliente: cada IP puede crear como
máximo 1 `Order` por segundo, con una ráfaga de hasta 5. Lo que excede el límite
recibe `429 Too Many Requests` sin llegar a `orders-service`; las consultas no
tienen límite. Está en
[ApiGateway/nginxconf/nginx.conf](ApiGateway/nginxconf/nginx.conf)
-`limit_req_zone` y `limit_req`-. Es el límite que respeta el cliente de la
demo de Rate Limiting.

**Cómo verlo:** [demos/04-throttling.sh](demos/04-throttling.sh) hace que
`Orders` demore 2 segundos en cada consulta y lanza 40 simultáneas: 20 responden
200 y 20 se rechazan al instante con 503. Algunas de las respuestas 200 traen
un producto "no disponible", porque las 20 consultas atendidas llaman a
`Products` a la vez y llenan su compartimento: el Throttling y el Bulkhead
actúan juntos. El Throttling por cliente del gateway se ve en la demo de Rate
Limiting.

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

- `users-service`: conexión a PostgreSQL.
- `products-service`: *ping* a MongoDB.
- `orders-service`: conexión a MySQL y a RabbitMQ.

`Orders` **no** chequea a `Users` ni a `Products`: si se cae una dependency,
`Orders` sigue funcionando degradado y no debe figurar como caído. El gateway
publica los tres en `/health/users`, `/health/products` y `/health/orders`.
Docker los consulta cada 5 segundos -`healthcheck` en
[docker-compose.yaml](docker-compose.yaml)- y los usa para no levantar un
servicio hasta que sus dependencias estén sanas -`depends_on: condition:
service_healthy`-.

**Cómo verlo:** [demos/06-health.sh](demos/06-health.sh) detiene la base de
datos de `Products`. `/health/products` pasa a 503 con `"MongoDB no responde"`,
`Orders` sigue sano, y a los pocos segundos Docker marca `products-service` como
`unhealthy`. Al levantar la base, todo vuelve a la normalidad. Para verlo en
vivo, corre [demos/monitor-salud.sh](demos/monitor-salud.sh) en otra terminal.

> [!TIP]
>
> Consulta [Health checks in ASP.NET
> Core](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/health-checks?view=aspnetcore-10.0)
> para tener más información sobre cómo está implementado el chequeo de salud.

#### Competing Consumers

**Problema:** un solo proceso que atiende una cola es un cuello de botella y un
punto único de falla.

**Implementación:** `POST /orders/` publica el id de la `Order` en la cola
`orders.process` de RabbitMQ. Tres réplicas de `orders-worker` consumen la misma
cola; RabbitMQ entrega cada mensaje a una sola réplica. Cada réplica toma un
mensaje a la vez -`prefetch = 1`- y lo confirma -*ack*- recién cuando termina;
si se cae antes, RabbitMQ entrega el mensaje a otra. Si `Users` o `Products` no
están disponibles, el *worker* devuelve el mensaje a la cola y lo reintenta 5
segundos después. Está en
[OrdersService/Messaging/OrderProcessingWorker.cs](OrdersService/Messaging/OrderProcessingWorker.cs).

**Cómo verlo:**
[demos/07-competing-consumers.sh](demos/07-competing-consumers.sh) crea 9
`Orders`. Cada *worker* tarda 2 segundos por `Order`, y las 9 se procesan en
unos 6 segundos, 3 por réplica. Después crea 9 más y detiene una réplica
mientras procesa: la `Order` que tenía la termina otra réplica. Para ver la cola
y sus consumidores, abre la consola de RabbitMQ.

#### Event Sourcing

**Problema:** si solo se guarda el estado actual, se pierde la historia de cómo
se llegó a él, y una corrupción de ese estado no tiene vuelta atrás.

**Implementación:** cada cambio de una `Order` se guarda como un evento
inmutable en la tabla `order_events`: `OrderCreated`, `OrderConfirmed` u
`OrderRejected`. La tabla `orders` es una **proyección**: el estado actual,
calculado a partir de los eventos. Ver
[OrderEvent.cs](OrdersService/Models/OrderEvent.cs) y
[OrderProjection.cs](OrdersService/Models/OrderProjection.cs).

- `GET /orders/{id}/events` muestra los eventos de una `Order` y el estado que
  resulta de reproducirlos.
- `POST /orders/projections/rebuild` borra la tabla `orders` y la reconstruye a
  partir de los eventos.

**Cómo verlo:** [demos/08-event-sourcing.sh](demos/08-event-sourcing.sh) crea
una `Order`, muestra sus eventos, borra la tabla `orders` directamente en MySQL
y la reconstruye. Las `Orders` vuelven con su estado.

## Demo 2: Rendimiento y procesamiento asíncrono

Escenario: un pico de tráfico en una campaña. La aplicación debe mantener
latencias bajas y absorber la carga.

Qué se muestra:

- Lecturas rápidas con caché y vistas materializadas.
- Creación de `Orders` encolada, con consumidores en competencia.
- Eventos pub/sub para desacoplar los servicios.

Patrones cubiertos:

- **Cache-Aside**: `Products`, con Redis.
- **Materialized View**: vista desnormalizada del detalle de `Orders`, con
  nombre de `User` y de `Product`, que reemplaza las llamadas HTTP en cada
  consulta.
- **CQRS**: comandos -crear `Order`- separados de las consultas -detalle de
  `Orders` de un `User`-.
- **Queue-Based Load Leveling**: `POST /orders` encola en lugar de escribir
  directamente.
- **Competing Consumers**: varios *workers* consumen la cola de `Orders`.
- **Publisher/Subscriber**: eventos `OrderCreated`, `UserUpdated` y
  `ProductUpdated`; la vista materializada se suscribe a ellos para mantenerse
  al día.
- **Asynchronous Request-Reply**: `POST /orders` responde `202 Accepted` con un
  `Location` para consultar el estado de la `Order`.

Resultado observable: latencias de lectura estables durante el pico, *backlog*
absorbido por la cola y throughput que crece al agregar *workers*. El pico se
genera con [k6/load.js](k6/load.js) y se compara antes y después de cada patrón
en el dashboard de Grafana.

## Demo 3: Seguridad de extremo a extremo

Escenario: los usuarios inician sesión con un proveedor de identidad externo. El
gateway aplica los controles antes de que el tráfico llegue a los servicios.

Qué se muestra:

- Inicio de sesión con un proveedor externo y propagación de tokens.
- TLS, validación de tokens y limitación de tráfico en el gateway.
- *Sidecars* que aplican mTLS y políticas junto a cada servicio.
- Acceso temporal y acotado a almacenamiento desde el cliente.

Patrones cubiertos:

- **Federated Identity**: OIDC con un IdP, por ejemplo Keycloak.
- **Gatekeeper**: el gateway valida las solicitudes antes de reenviarlas a los
  servicios.
- **Gateway Offloading**: terminación TLS, validación de JWT y limitación de
  tráfico en el gateway.
- **Sidecar**: proxy junto a cada servicio para mTLS, autorización y telemetría.
- **Valet Key**: URL firmada para que el cliente suba la imagen de un `Product`
  directamente al almacenamiento -MinIO o Azurite- sin pasar por `Products` ni
  recibir credenciales.

Resultado observable: los servicios no implementan autenticación, las políticas
son consistentes en el borde y cada acceso a datos tiene el mínimo privilegio
necesario.

## Demo 4: Modificabilidad, integración y despliegue a escala

Escenario: operar para varias regiones o clientes, centralizar la configuración,
integrarse con un sistema legado y migrar funcionalidad de un monolito.

Qué se muestra:

- Una copia completa de la aplicación por región o cliente, con enrutamiento
  dirigido.
- Configuración central con recarga dinámica.
- Un puente de mensajería entre la aplicación y un sistema legado simulado.
- Migración progresiva de rutas desde un monolito.

Patrones cubiertos:

- **Deployment Stamps**: dos copias de la aplicación; el gateway elige una según
  un encabezado de región o cliente.
- **External Configuration Store**: URLs, límites y *feature flags* en un
  almacén central que se recarga sin redesplegar.
- **Messaging Bridge**: traduce los eventos de `Orders` al formato de un ERP
  legado simulado, y viceversa.
- **Strangler Fig**: un monolito que atiende `Users`, `Products` y `Orders`; el
  gateway desvía ruta por ruta hacia los microservicios hasta reemplazarlo.

Resultado observable: despliegues independientes por región, cambios de
configuración sin redespliegue y convivencia ordenada con sistemas existentes.

## Demo 5: Estrategias de lanzamiento y experimentación

Escenario: publicar una nueva versión de un servicio, por ejemplo `Products`,
con riesgo controlado y medir su impacto.

Qué se muestra:

- Dos entornos vivos y conmutación inmediata.
- Reemplazo de instancias por lotes.
- Una fracción del tráfico derivada a la versión nueva.
- Experimentos por segmento de usuarios.

Patrones y estrategias cubiertos:

- **Arquitectura de microservicios**: el estilo común sobre el que se apoyan
  todas las demos.
- **Blue/Green Deployment**: dos versiones de `Products` detrás del gateway, con
  cambio y *rollback* inmediatos.
- **Rolling Upgrade**: varias réplicas que se reemplazan por lotes, verificando
  su salud.
- **Canary Testing**: entre 1 % y 10 % del tráfico a la versión nueva, por
  ejemplo con `split_clients` de nginx.
- **A/B Testing**: segmentos por usuario, con cookie o encabezado, y comparación
  de métricas.

Resultado observable: despliegues frecuentes con impacto acotado y decisiones
basadas en métricas.

## Cobertura

| Categoría | Patrones | Demo |
|---|---|---|
| Disponibilidad | Bulkhead, Circuit Breaker, Competing Consumers, Event Sourcing, Health Endpoint Monitoring, Rate Limiting, Retry, Throttling | 1 |
| Rendimiento | Cache-Aside, Queue-Based Load Leveling, Competing Consumers, Publisher/Subscriber, Asynchronous Request-Reply, CQRS, Materialized View | 2 |
| Seguridad | Gatekeeper, Federated Identity, Gateway Offloading, Sidecar, Valet Key | 3 |
| Modificabilidad y despliegue | Deployment Stamps, External Configuration Store, Messaging Bridge, Strangler Fig | 4 |
| Modificabilidad y despliegue: estrategias de lanzamiento | Arquitectura de microservicios, Blue/Green, Rolling Upgrade, Canary Testing, A/B Testing | 5 |

## Recomendaciones de implementación

- Un repositorio por demo, partiendo de esta aplicación base, con su propio
  `docker-compose.yaml`.
- Observabilidad suficiente -logs, métricas, trazas- para que cada patrón se vea
  en acción. Cada demo reutiliza el perfil `observability`, las métricas de
  [Common/Observability.cs](Common/Observability.cs) y el dashboard de esta
  demo, y agrega los paneles de sus propios patrones.
- Scripts que simulen picos, fallas y lentitud para hacer visibles los efectos:
  los de [demos/](demos) para mostrar cada patrón paso a paso, y escenarios de
  k6 para la carga, partiendo de [k6/lib.js](k6/lib.js) y
  [k6/load.js](k6/load.js).
- Los mismos datos iniciales en todas las demos, para comparar latencias y tasas
  de error de forma justa.
