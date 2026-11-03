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

## Demos de disponibilidad

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
- Mensajes que no se pierden aunque RabbitMQ no esté disponible.

Resultado observable: si `Products` se cae, el circuito se abre, `Orders` deja
de esperar *timeouts* y el detalle devuelve las `Orders` con "no disponible
-circuito abierto-" en lugar de un error. `Users` y `Products` siguen atendiendo
sus propias rutas.

### Qué agrega a la aplicación base

| Componente | Para qué |
| ---------- | -------- |
| `rabbitmq` | Exchange `andis.events`, donde se publican los eventos, y cola `orders.process` con las `Orders` pendientes de procesar. Consola en <http://localhost:15672> -usr: `guest` pwd: `guest`-. |
| `orders-worker` -3 réplicas- | La misma imagen que `orders-service`, con el *worker* habilitado -`Worker__Enabled=true`-. Consume la cola y confirma o rechaza cada `Order`. |
| Estado de la [`Order`](OrdersService/Models/Order.cs) | `POST /orders/` guarda la `Order` como `Pending`. Un *worker* verifica que el `User` y el `Product` existan y la pasa a `Confirmed` o `Rejected`. |
| Inyección de fallas | `PUT /users/chaos`, `PUT /products/chaos` y `PUT /orders/chaos` hacen que el servicio falle o demore a propósito, sin reiniciarlo. Así se provocan las situaciones que activan cada patrón. Está en [Common/Chaos.cs](Common/Chaos.cs). |
| Interruptores de los patrones | `PUT /orders/patterns` prende y apaga el Retry, el Circuit Breaker, el Bulkhead y el Throttling de `Orders`, sin reiniciarlo; `DELETE /orders/patterns` los vuelve a prender. Cada demo deja prendido solo su patrón, para que no intervengan los demás. Está en [OrdersService/Patterns.cs](OrdersService/Patterns.cs). |
| Observabilidad y carga -opcional- | Perfil `observability` de Docker Compose con Prometheus y un dashboard de Grafana ya configurado, y escenarios de carga con k6 en [k6/](k6). Muestra los patrones bajo carga y a lo largo del tiempo. Cómo usarlos está en el [README](README.md#observabilidad-y-pruebas-de-carga). |

Ejemplo de inyección de fallas:

```bash
# Products falla en el 50 % de las solicitudes y demora 1 segundo en todas
curl -X PUT http://localhost/products/chaos -H "Content-Type: application/json" \
  -d '{"failureRate": 0.5, "delayMs": 1000}'

# Vuelve a la normalidad
curl -X DELETE http://localhost/products/chaos
```

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

**Cómo verlo:** [demos/02-circuit-breaker.sh](demos/02-circuit-breaker.sh) apaga
el Retry y hace que `Products` falle en el 100 % de las solicitudes. Sin
reintentos, cada falla cuenta una vez: las dos primeras consultas dicen "no
disponible [error HTTP 503]", en la tercera el circuito se abre, y desde ahí
responden con "no disponible [circuito abierto]" sin llamar a `Products`. Cuando
`Products` se recupera, el circuito sigue abierto hasta que pasan los 15
segundos. Los *logs* de Docker muestran las transiciones ABIERTO → SEMIABIERTO →
CERRADO. Con el Retry prendido, la primera consulta tardaría unos 3 segundos,
porque reintentaría cada llamada antes de que se abriera el circuito.

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
rechazan de inmediato con `"no disponible [bulkhead lleno]"`. El nombre del
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
200 con todos los nombres y 20 se rechazan al instante con 503. La demo apaga
el Bulkhead; con el Bulkhead prendido, algunas respuestas 200 traerían un
producto "no disponible", porque las 20 consultas atendidas llaman a `Products`
a la vez y llenan su compartimento: en un sistema real, el Throttling y el
Bulkhead actúan juntos. El Throttling por cliente del gateway se ve en la demo
de Rate Limiting.

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
- `orders-service`: conexión a MySQL y a RabbitMQ. Si RabbitMQ no está, queda
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

**Cómo verlo:** [demos/06-health.sh](demos/06-health.sh) detiene la base de
datos de `Products`. `/health/products` pasa a 503 con `"MongoDB no responde"`,
`Orders` sigue sano, y a los pocos segundos Docker marca `products-service` como
`unhealthy`. Al levantar la base, todo vuelve a la normalidad. Para verlo en
vivo, corre [demos/monitor-health.sh](demos/monitor-health.sh) en otra terminal.

> [!TIP]
>
> Consulta [Health checks in ASP.NET
> Core](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/health-checks?view=aspnetcore-10.0)
> para tener más información sobre cómo está implementado el chequeo de salud.

#### Competing Consumers

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

#### Transactional Outbox

Este patrón no está en la lista de la lectura de patrones de disponibilidad;
se agrega porque la Demo 2 depende de él. Con eventos que alimentan una vista
materializada, un evento perdido deja esa vista desactualizada para siempre.

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
RabbitMQ. Ver [OutboxMessage.cs](OrdersService/Models/OutboxMessage.cs).

El *relay* del *outbox* -[OutboxRelay.cs](OrdersService/Messaging/OutboxRelay.cs)-
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

**Cómo verlo:** [demos/09-transactional-outbox.sh](demos/09-transactional-outbox.sh)
detiene RabbitMQ y crea 5 `Orders`. Todas responden `202`; antes del *outbox*,
`Orders` respondía `503` y la `Order` quedaba `Pending` para siempre.
`/health/orders` responde `Degraded`, y los 5 mensajes esperan en
`outbox_messages`. Al levantar RabbitMQ, el *relay* los publica, los *workers*
confirman las `Orders` y el *outbox* queda vacío. En Grafana, el panel "Outbox
de Orders" muestra los pendientes subir y volver a cero.

## Demos de rendimiento y procesamiento asíncrono

**Estado: implementada en este repositorio, sobre las demos de resiliencia y
disponibilidad.**

Escenario: un pico de tráfico en una campaña. La aplicación debe mantener
latencias bajas y absorber la carga.

Qué se muestra:

- Lecturas rápidas con caché y vistas materializadas.
- Creación de `Orders` encolada, con consumidores en competencia.
- Eventos pub/sub para desacoplar los servicios.

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
| Exchange `andis.events` | Eventos `order.created`, `order.confirmed`, `order.rejected`, `user.updated` y `product.updated`. Los contratos están en [Common/Events.cs](Common/Events.cs). |
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
una dependencia, y por eso no entra en `/health`. Está en
[ProductsService/ProductCache.cs](ProductsService/ProductCache.cs).

Se borra la clave en lugar de escribir el valor nuevo: así la caché nunca guarda
un valor que MongoDB no tiene. El TTL acota cuánto puede durar un dato
desactualizado si una invalidación falla.

**Cómo verlo:** [demos/10-cache-aside.sh](demos/10-cache-aside.sh) simula una
base que demora 200 ms y lee el mismo `Product` varias veces: la primera lectura
demora 200 ms y las siguientes, 2 o 3 ms. Después cambia su nombre: la lectura
siguiente es un *miss* y trae el nombre nuevo. Con Redis detenido, `Products`
sigue respondiendo, a la velocidad de MongoDB. Bajo carga,
[k6/cache-aside.js](k6/cache-aside.js) lee 50 veces por segundo con la caché
apagada y después prendida: la p95 baja de unos 100 ms a unos 3 ms.

#### Queue-Based Load Leveling

**Problema:** un pico de solicitudes que un servicio procesa en el momento lo
satura, y satura también a los servicios que llama.

**Implementación:** `POST /orders/` solo guarda la `Order` y responde; el
trabajo pesado -verificar el `User` y el `Product` con `Users` y `Products`- lo
hacen los *workers* desde la cola `orders.process`, a su propio ritmo. La cola
absorbe el pico: `Users` y `Products` reciben como máximo una llamada por
réplica del *worker*, sin importar cuántas `Orders` lleguen. Ya estaba en las
demos de disponibilidad, como base de [Competing
Consumers](#competing-consumers); las demos de rendimiento lo muestra bajo un
pico.

**Cómo verlo:** [demos/11-queue-based-load-leveling.sh](demos/11-queue-based-load-leveling.sh)
crea 30 `Orders` a la vez, directamente en `orders-service`: el Throttling por
cliente del gateway rechazaría el pico antes de que llegara a la cola. Todas
responden `202` en pocos milisegundos; la cola baja de a 3 mensajes cada 2
segundos hasta vaciarse. [k6/load-leveling.js](k6/load-leveling.js) envía 10
`POST` por segundo durante 10 segundos: la latencia de `create order` se
mantiene baja y la cola crece durante el pico y se vacía después.

#### Competing Consumers

**Problema:** un solo consumidor procesa la cola a un ritmo fijo; si el pico
dura mucho, la cola crece sin límite.

**Implementación:** la misma de la [demo de
disponibilidad](#competing-consumers). Visto como patrón de rendimiento, el
throughput crece agregando réplicas de `orders-worker`, sin cambiar el código.

**Cómo verlo:**
[demos/12-competing-consumers-scaling.sh](demos/12-competing-consumers-scaling.sh)
procesa 30 `Orders` con 3 réplicas, unos 21 segundos, y otras 30 con 6 réplicas,
unos 12 segundos. Al final vuelve a 3 réplicas.

#### Publisher/Subscriber

**Problema:** si un servicio avisa de sus cambios llamando a cada interesado,
tiene que conocerlos a todos, y cada interesado nuevo obliga a cambiarlo.

**Implementación:** `Orders`, `Users` y `Products` publican eventos en el
exchange `andis.events`, de tipo *topic*. Cada suscriptor tiene su propia cola y
la enlaza con las claves de ruteo que le interesan:

| Cola | Suscriptor | Claves |
| ---- | ---------- | ------ |
| `orders.process` | `orders-worker` | `order.created` |
| `orders.details-view` | `orders-projector` | `order.*`, `user.updated`, `product.updated` |

Un mismo `order.created` llega a las dos colas. `Orders` no sabe que existe el
proyector: agregarlo no cambió el código que publica. La conexión y la
publicación están en [Common/RabbitMq.cs](Common/RabbitMq.cs).

**Cómo verlo:** [demos/13-publisher-subscriber.sh](demos/13-publisher-subscriber.sh)
muestra los enlaces del exchange, crea una `Order` y muestra los *logs* del
*worker* y del proyector, que la reciben los dos. Después cambia el nombre de un
`Product` y de un `User`: esos eventos solo los recibe el proyector.

#### Asynchronous Request-Reply

**Problema:** una operación que tarda no puede tener al cliente esperando la
respuesta: el cliente agota su *timeout* o retiene una conexión que no hace
nada.

**Implementación:** `POST /orders/` responde `202 Accepted` enseguida, con
`Location: /orders/{id}/status` y `Retry-After: 2`. `GET /orders/{id}/status`
responde `200` con el estado mientras la `Order` está `Pending`, también con
`Retry-After`; cuando un *worker* la confirma o la rechaza, responde `303 See
Other` a `/orders/{id}`, y el cliente sigue la redirección para obtener el
resultado. Está en
[OrdersController.cs](OrdersService/Controllers/OrdersController.cs).

**Cómo verlo:** [demos/14-asynchronous-request-reply.sh](demos/14-asynchronous-request-reply.sh)
crea una `Order`, muestra el `202` con sus encabezados y consulta el estado
respetando `Retry-After` hasta el `303`. Al final sigue la redirección y muestra
la `Order` confirmada.

#### CQRS

**Problema:** el modelo que conviene para escribir -tablas normalizadas, cada
dato en el servicio que es su dueño- no es el que conviene para leer. La
consulta de detalle tiene que juntar datos de tres servicios en cada lectura.

**Implementación:** los comandos y las consultas usan modelos distintos.

- **Comandos:** `POST /orders/` y el *worker* escriben `orders`, `order_events`
  y `outbox_messages`, en
  [OrdersController.cs](OrdersService/Controllers/OrdersController.cs) y
  [OrderProcessingWorker.cs](OrdersService/Messaging/OrderProcessingWorker.cs).
- **Consultas:** `GET /orders/user/{userId}/details/view` lee solo la vista
  `order_details` -ver [Materialized View](#materialized-view)-, en
  [OrdersQueriesController.cs](OrdersService/Controllers/OrdersQueriesController.cs).
  Es una consulta a MySQL, sin llamadas a `Users` ni a `Products`, y sin
  Throttling.

`GET /orders/user/{userId}/details`, la consulta síncrona de la demo de
disponibilidad, queda como está: la usan los demos de Retry, Circuit Breaker,
Bulkhead y Throttling, y sirve para comparar. El costo es la **consistencia
eventual**: el lado de las consultas se actualiza un momento después que el de
los comandos.

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
con el nombre del cliente y el nombre del producto ya copiados. La mantiene
[OrderDetailsProjector.cs](OrdersService/Messaging/OrderDetailsProjector.cs),
que corre en `orders-projector` y consume la cola `orders.details-view`:

- `order.created`: busca el nombre del cliente y del producto **una vez**, con
  los mismos `HttpClient` y la misma resiliencia de la demo de disponibilidad, y
  agrega la fila. Si `Users` o `Products` no responden, el mensaje vuelve a la
  cola.
- `order.confirmed` y `order.rejected`: actualizan el estado.
- `user.updated` y `product.updated`: actualizan el nombre en todas las filas
  de ese `User` o `Product`.

Corre con una sola réplica, para que los eventos de una misma `Order` se
apliquen en orden.

**Cómo verlo:** [demos/16-materialized-view.sh](demos/16-materialized-view.sh)
cambia el nombre de un `Product` y la vista lo refleja. Con `Products` fallando
siempre, la consulta síncrona responde "no disponible" y la vista sigue
respondiendo con los nombres. Por último, detiene el proyector y crea una
`Order`: la `Order` se confirma, pero la vista no la tiene y sus eventos esperan
en la cola. Al levantar el proyector, la vista se pone al día.

## Demos de seguridad

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

## Demos de facilidad de modificación

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

## Demos de facilidad de despliegue

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
| Disponibilidad | Bulkhead, Circuit Breaker, Competing Consumers, Event Sourcing, Health Endpoint Monitoring, Rate Limiting, Retry, Throttling, Transactional Outbox | 1 |
| Rendimiento | Cache-Aside, Queue-Based Load Leveling, Competing Consumers, Publisher/Subscriber, Asynchronous Request-Reply, CQRS, Materialized View | 2 |
| Seguridad | Gatekeeper, Federated Identity, Gateway Offloading, Sidecar, Valet Key | 3 |
| Modificabilidad y despliegue | Deployment Stamps, External Configuration Store, Messaging Bridge, Strangler Fig | 4 |
| Modificabilidad y despliegue: estrategias de lanzamiento | Arquitectura de microservicios, Blue/Green, Rolling Upgrade, Canary Testing, A/B Testing | 5 |
