# Traspaso: idempotencia, varios consumidores y estado por réplica en ANDIS2_UT4_DEMO1

Este documento resume un análisis hecho en otra conversación y define el trabajo
a hacer en este repositorio. Léelo completo antes de empezar. Si algo de lo que
dice no coincide con el código, el código manda: verifica y avisa.

## Contexto

El repositorio es la demo de patrones de arquitectura de la UT4 de Análisis y
Diseño de Aplicaciones II (UCU). Es material docente: el código y la
documentación se leen en clase, así que **la explicación importa tanto como la
corrección**.

El análisis encontró tres problemas de idempotencia y concurrencia, y estado en
memoria por réplica que hoy no está documentado como tal. El objetivo es:

1. Permitir **varias réplicas de `orders-projector`** sin que se corrompa la
   vista materializada.
2. Cerrar la **carrera del worker** con mensajes duplicados concurrentes.
3. Hacer **idempotente `POST /orders/`** con un encabezado `Idempotency-Key`.
4. **Documentar** idempotencia y stateless donde se usan, con problemas,
   soluciones y trade-offs.
5. **Mostrar** las soluciones en las demos que ya existen, sin crear demos
   nuevas.

Fuera de alcance: `ANDIS_CleanArchitecture_Demo`, el monolito de la demo de
Strangler Fig y cualquier cambio de los patrones de resiliencia (Retry, Circuit
Breaker, Bulkhead, Throttling) más allá de documentarlos.

## Convenciones del repositorio

* Comentarios y documentación en español neutro e impersonal, como el código
  actual. Imperativo de segunda persona en los consejos -"Consulta…"-, sin
  voseo.
* Los comentarios explican **por qué**, nombran el patrón al principio -por
  ejemplo `// Competing Consumers: …`- y remiten a otros archivos con "ver
  Archivo.cs".
* Los logs llevan el patrón entre corchetes: `[COMPETING CONSUMERS]`,
  `[MATERIALIZED VIEW]`, `[MESSAGING BRIDGE]`, etc.
* `PATTERNS.md` sigue la estructura **Problema / Implementación / Cómo funciona
  / Código / Cómo verlo**, con un *tip* de documentación al final.
* `ARCHITECTURE.md` termina cada sección con preguntas "Para analizar" y
  respuestas en comentarios HTML `<!-- → … -->`.
* Los diagramas son PlantUML en `assets/*.puml` con su `.svg` generado al lado.
  Si cambias un `.puml`, regenera el `.svg` con la misma herramienta que el
  resto -ver `README.md`-; si no puedes, avisa.

## Regla de documentación (pedido explícito del docente)

1. **En cada lugar donde se implementa una solución**, documentar qué problema
   ocurriría sin ella: el escenario concreto, paso a paso, que hoy puede
   ocurrir y que la solución elimina. Va en el comentario del código **y** en
   la sección correspondiente de `PATTERNS.md` o `ARCHITECTURE.md`.
2. **Los conceptos generales -idempotencia, stateless y estado por réplica- se
   explican en detalle una sola vez**, en el primer lugar donde se usan en el
   orden de lectura. En los demás lugares, una o dos oraciones propias del caso
   y un enlace a la explicación detallada.
3. La documentación va **donde se usa**, en `PATTERNS.md` o en
   `ARCHITECTURE.md`. No crear documentos nuevos.

El orden de lectura es `README.md` → `PATTERNS.md` → `ARCHITECTURE.md` (este
último es material de la UT5). Más abajo hay una propuesta de ubicación;
**confírmala con el usuario antes de escribir la documentación**.

---

## Cambio 1: varias réplicas de `orders-projector`

### Problema actual

`OrderDetailsProjector` (`src/OrdersService/Messaging/OrderDetailsProjector.cs`)
asume una sola réplica: lo dicen su comentario de clase, `PATTERNS.md`
(sección Materialized View, "Corre con una sola réplica…"), `README.md` y la
tabla de decisiones de `ARCHITECTURE.md` ("El proyector tiene una sola
réplica"). Con varias réplicas consumiendo `orders.details-view`, cada mensaje
va a una réplica distinta y en paralelo. Se rompen tres cosas, sin errores
visibles:

**a. Pérdida del estado por desorden.**

1. La réplica A recibe `order.created` de la Order 42. `AddOrderAsync` llama a
   Users y Products por HTTP, con Retry y Circuit Breaker, así que tarda.
2. El worker confirma la Order y la réplica B recibe `order.confirmed`.
3. `SetStatusAsync` no encuentra la fila, registra "no está en la vista; se
   ignora el estado", devuelve `true` y el mensaje se confirma.
4. A inserta la fila con `Status = Pending`.

Resultado: la vista muestra `Pending` para siempre, aunque `orders` diga
`Confirmed`. Con una réplica no pasa porque `prefetchCount: 1` obliga a
procesar en orden.

**b. Duplicados que pasan el chequeo de idempotencia.**

* `OrderInvoiced`: el chequeo `AnyAsync(...)` + `Add(...)` es *check-then-act*.
  Dos copias del mensaje -el puente entrega al menos una vez- en dos réplicas a
  la vez pasan las dos el chequeo y dejan dos `OrderInvoiced` en `order_events`.
  El comentario actual reconoce que el chequeo es seguro solo con una réplica.
* `AddOrderAsync`: `FindAsync` + `Add` también es *check-then-act*. Con dos
  `order.created` duplicados en paralelo, el segundo `INSERT` viola la clave
  primaria `order_id` y lanza `DbUpdateException`.

**c. Excepción no manejada que traba el consumidor.** El manejador solo captura
`JsonException`. Una `DbUpdateException` -o cualquier otra- deja el mensaje
sin `ack` ni `nack`; con `prefetchCount: 1`, RabbitMQ no le entrega más
mensajes a esa réplica hasta que se cierre el canal. **Verifica** este
comportamiento con la versión de RabbitMQ.Client del proyecto antes de
documentarlo.

**d. Cambios de nombre aplicados al revés.** Dos `user.updated` del mismo User
("Ana", luego "Ana María") pueden aplicarse en otro orden y la vista queda con
"Ana". Cada actualización es idempotente -asigna un valor absoluto- pero no
**conmutativa**. Lo mismo con `product.updated`.

### Solución

Hacer el proyector **tolerante al desorden y a la concurrencia**, en lugar de
depender de procesar en serie. Opciones elegidas:

1. **Estado de la Order: la máquina de estados es monótona.** `Pending` →
   `Confirmed` o `Rejected`, y estos dos son finales. Basta con que:
   * `SetStatusAsync` haga un *upsert*: si la fila no existe, la crea con el
     estado final y los datos que traiga el evento -`OrderConfirmed` ya trae
     `UserId`, `ProductId` y `Quantity`; `OrderRejected` no, usar valores por
     defecto-.
   * `AddOrderAsync` complete los datos sin pisar un estado final (ya lo hace
     al actualizar; mantenerlo al pasar al *upsert* atómico).

   Así el resultado no depende del orden de llegada. No hace falta versionar
   estos eventos; documenta por qué alcanza con la monotonía.
2. **Upsert atómico en lugar de *check-then-act*.** `INSERT … ON DUPLICATE KEY
   UPDATE` en MySQL -con SQL crudo o la alternativa que prefieras en EF Core-.
   Al actualizar, no pisar `status` si ya es final.
3. **Índice único `order_events(order_id, type)`** en
   `src/OrdersService/mysqlinit/mysqlinit.sql`. Hoy todos los tipos de evento
   ocurren a lo sumo una vez por Order -`OrderCreated`, `OrderConfirmed`,
   `OrderRejected`, `OrderInvoiced`-; documenta esa suposición en el script y en
   el código. En `OrderInvoiced`, insertar y tratar la violación del índice como
   "ya registrado" (sin *check-then-act*). El índice también protege al worker
   (cambio 2): defensa en profundidad.
4. **Orden de los cambios de nombre por fecha.** Agregar un campo opcional
   `OccurredAt` -`DateTime?`- a `UserUpdated` y `ProductUpdated` en
   `src/Common/Events.cs`, con el mismo criterio de compatibilidad que
   `OrderConfirmed` (campos nuevos opcionales). Users y Products lo completan al
   publicar. En `order_details`, agregar `customer_name_at` y `product_name_at`,
   y actualizar el nombre solo si el evento es más nuevo -`WHERE
   customer_name_at IS NULL OR customer_name_at < @occurredAt`-.
   `AddOrderAsync` usa como fecha el momento en que leyó los nombres.
   Trade-off a documentar: depende de que los relojes de los contenedores estén
   sincronizados. Alternativa más robusta y más invasiva: un número de versión
   en Users y Products.
5. **Manejo de excepciones.** En el manejador del proyector, capturar
   cualquier excepción que no sea `OperationCanceledException`, registrarla y
   hacer `nack` con `requeue: true` después de la demora existente
   (`RequeueDelay`). Aplicar lo mismo en `OrderProcessingWorker` y en
   `OrdersToErpBridge` si tienen el mismo hueco. Trade-off a documentar: un
   mensaje que falla siempre -*poison message*- vuelve a la cola para siempre;
   la alternativa es una *dead-letter queue*, que no se implementa.
6. **Tres réplicas** de `orders-projector` en `docker-compose.yaml`, igual que
   `orders-worker` (`deploy: replicas: 3`).

### Documentación del cambio 1

* Comentario de clase de `OrderDetailsProjector`: reemplazar "una sola réplica"
  por la explicación de cómo tolera varias, con el escenario de cada problema
  que se evita (a, b, c, d).
* Comentario en cada solución en el código: qué pasaría sin ella.
* `PATTERNS.md`, Materialized View: reemplazar "Corre con una sola réplica…"
  por la explicación, el escenario del problema, la solución y los trade-offs.
  Enlazar a la explicación general de idempotencia.
* `PATTERNS.md`, Messaging Bridge -viñeta "Entrega al menos una vez"-: ajustar
  cómo `Orders` evita registrar dos veces la factura (índice único) y enlazar.
* `README.md`: `orders-projector` pasa a `×3` en la tabla de componentes.
* `ARCHITECTURE.md`:
  * Tabla "Decisiones abiertas a discusión": la fila "El proyector tiene una
    sola réplica" deja de ser cierta. Reemplazarla por las decisiones nuevas
    con sus costos -por ejemplo, "Los cambios de nombre se ordenan por la hora
    de cada contenedor" y "Un mensaje que falla siempre vuelve a la cola"-.
  * Respuestas ocultas que mencionan "una del proyector" (sección Servicios y
    datos) y "El proyector con una sola réplica: varias réplicas… particionando
    por Order, o single active consumer…" (preguntas finales): actualizarlas.
    Conservar la mención de las alternativas -particionar por Order, *single
    active consumer* de RabbitMQ- como opciones que **no** se eligieron, con
    su trade-off: particionar conserva el orden y escala, pero agrega
    infraestructura; *single active consumer* da disponibilidad pero no
    rendimiento.
* Diagramas `assets/16-materialized-view*.puml`: actualizar si muestran una
  sola réplica o el flujo viejo.

### Criterios de aceptación

* Con 3 réplicas del proyector, el escenario (a) termina con la vista en
  `Confirmed`.
* Un `order.invoiced` duplicado deja un solo `OrderInvoiced` en `order_events`.
* Un `order.created` duplicado no lanza excepciones ni traba réplicas.
* Un `user.updated` viejo que llega después de uno nuevo no pisa el nombre.

---

## Cambio 2: carrera del worker

### Problema actual

`OrderProcessingWorker.ProcessAsync`
(`src/OrdersService/Messaging/OrderProcessingWorker.cs`) dice ser idempotente
porque ignora las Orders que ya no están `Pending`. Vale para duplicados
**secuenciales**, no para **concurrentes**:

1. El *relay* del outbox publica `order.created` dos veces -lo documenta
   `OutboxRelay.cs`-.
2. Con 3 réplicas del worker, las dos copias llegan a réplicas distintas a la
   vez.
3. Las dos leen `Pending` con `FindAsync`, esperan `ProcessingDelayMs` (2 s) y
   las llamadas a Users y Products, y las dos guardan el cambio.

Resultado: dos `OrderConfirmed` en `order_events` -el historial de Event
Sourcing queda mal- y dos mensajes `order.confirmed` en el outbox, que llegan
dos veces al proyector y al ERP. La tabla `orders` no tiene token de
concurrencia. La demora artificial agranda la ventana.

### Solución

**Transición de estado condicional y atómica**, como primer paso de la
transacción:

```csharp
var rows = await db.Orders
    .Where(o => o.Id == orderId && o.Status == OrderStatus.Pending)
    .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, newStatus), cancellationToken);
if (rows == 0)
{
    // Otra réplica ya la procesó: no se agrega nada.
    await transaction.RollbackAsync(cancellationToken);
    return true;
}
// Recién ahora se agregan el OrderEvent y el OutboxMessage.
```

* No modificar además la entidad cargada con `FindAsync` -o cargarla con
  `AsNoTracking`- para no escribir el estado dos veces.
* Registrar en el log, con `[COMPETING CONSUMERS]`, cuando una réplica descarta
  una Order que procesó otra: así se ve en la demo.
* El índice único del cambio 1 queda como segunda barrera.

### Documentación del cambio 2

* Comentario de `ProcessAsync`: corregir "Es idempotente: si la orden ya no
  está Pending, no hace nada" para explicar que el chequeo y la escritura deben
  ser **atómicos**, con el escenario de la carrera (*check-then-act*).
* `PATTERNS.md`, Competing Consumers para disponibilidad: agregar el problema,
  la solución y el enlace a la explicación general de idempotencia.
* `PATTERNS.md`, Transactional Outbox: el párrafo "Por eso los suscriptores son
  idempotentes: el *worker* ignora las `Orders` que ya no están `Pending`…"
  debe describir la solución nueva y enlazar.
* `ARCHITECTURE.md`, Consistencia de los datos, viñeta "Cada evento llega al
  menos una vez": enlazar a la explicación general.

### Criterios de aceptación

* Dos `order.created` de la misma Order procesados a la vez por réplicas
  distintas dejan **un** `OrderConfirmed` en `order_events` y **un**
  `order.confirmed` en `outbox_messages`.

---

## Cambio 3: `POST /orders/` con `Idempotency-Key`

### Problema actual

`OrdersController.CreateOrder`
(`src/OrdersService/Controllers/OrdersController.cs`) crea una Order nueva en
cada llamada. Si el cliente no recibe el `202` -*timeout*, corte de red- y
reintenta, se crea una segunda Order, con su propio `order.created`, su
confirmación y un segundo pedido al ERP con **otro** número, así que el ERP
tampoco puede detectarlo.

### Solución

* Encabezado **opcional** `Idempotency-Key`. Sin él, el comportamiento actual
  no cambia, para que las demos y clientes existentes sigan funcionando;
  documentar el riesgo de omitirlo.
* Tabla nueva `idempotency_keys` en `mysqlinit.sql`:
  `idempotency_key`, `user_id` -alcance: el `User` del token si hay Federated
  Identity, 0 si es anónima-, `request_hash` -SHA-256 del cuerpo-, `order_id` y
  `created_at`. Clave primaria `(idempotency_key, user_id)`.
  * **No** guardar la clave en `orders`: esa tabla es una proyección y
    `POST /orders/projections/rebuild` la borra y la reconstruye desde los
    eventos. Documentar esta razón.
* La clave se inserta **en la misma transacción** que la Order, su evento y el
  mensaje del outbox.
* Comportamiento:
  * Clave nueva: se crea la Order normalmente.
  * Clave repetida con el mismo cuerpo: `202` con el **mismo** `id`, la misma
    `Location` y `Retry-After`, sin crear nada. Agregar un encabezado que
    indique que es una repetición -por ejemplo `Idempotent-Replayed: true`- y
    documentarlo.
  * Clave repetida con otro cuerpo: `422 Unprocessable Content`, como propone
    el borrador de la IETF.
  * Dos solicitudes simultáneas con la misma clave: la segunda choca con la
    clave primaria; capturar la violación, leer la fila existente y responder
    como repetición.
* Las claves no vencen en la demo. Documentar el trade-off: en producción se
  borran después de un plazo -por ejemplo 24 horas-, y un reintento posterior
  crearía otra Order.

### Documentación del cambio 3

* Comentario en `CreateOrder`: el escenario del reintento duplicado y cómo la
  clave lo evita.
* `PATTERNS.md`, Asynchronous Request-Reply -es donde se describe
  `POST /orders/`-: el encabezado, los tres casos de respuesta, el problema sin
  la clave, el trade-off de vencimiento y el enlace a la explicación general.
* `ARCHITECTURE.md`, Creación de una Order: asíncrona: mencionar la clave en el
  flujo si corresponde.

### Criterios de aceptación

* Dos `POST` con la misma clave y el mismo cuerpo devuelven el mismo `id` y
  crean una sola Order.
* La misma clave con otro cuerpo devuelve `422`.
* Un `POST` sin clave se comporta como hoy.

---

## Cambio 4: explicaciones generales

### Idempotencia

**Contenido de la explicación detallada:**

* Definición: ejecutar una operación varias veces tiene el mismo efecto que
  ejecutarla una vez.
* Por qué importa en esta aplicación: entrega **al menos una vez** del outbox
  y del puente, reintentos de Retry y de los clientes, Competing Consumers.
* Que depende de la **semántica** de la operación, no del verbo HTTP: Retry
  reintenta `GetUser`, que viaja como `POST` porque es SOAP, y es seguro porque
  es una lectura. Reintentar `CreateUser` no lo sería.
* Las técnicas que usa la demo, con enlace a cada lugar: identificador
  determinístico (factura del ERP), transición de estado condicional (worker),
  *upsert* (proyector), índice único (eventos), clave de idempotencia
  (`POST /orders/`).
* La trampa del *check-then-act*: verificar y escribir en dos pasos no es
  atómico.
* Matices: deduplicar no es lo mismo que responder igual (`CreateUser` de Users
  rechaza el email repetido con un *Fault*, en lugar de devolver el `id`
  original); idempotente no es lo mismo que conmutativo (cambios de nombre).
* Trade-offs: estado adicional para recordar claves u operaciones, y su
  vencimiento.

**Ubicación propuesta:** `PATTERNS.md`, sección **Retry**, que es el primer
lugar donde se usa en el orden de lectura. Enlaces desde: Competing Consumers
para disponibilidad, Transactional Outbox, Asynchronous Request-Reply,
Materialized View, Messaging Bridge y `ARCHITECTURE.md` (Consistencia de los
datos).

### Stateless y estado por réplica

**Contenido de la explicación detallada:**

* Definición: el servicio no depende de datos en memoria o disco local entre
  solicitudes; el estado vive afuera. Stateless no significa sin estado:
  significa que el estado no vive en la instancia.
* Por qué importa: cualquier réplica atiende cualquier solicitud, se puede
  reemplazar o escalar sin perder nada, y alcanzan instancias más chicas.
* Ejemplos positivos de la demo: Cache-Aside en Redis, compartido por todas las
  réplicas (`ProductCache.cs`); la variante del A/B Testing elegida por hash
  del id, igual en todas las réplicas (`Experiment.cs`); configuración en Consul.
* Estado por réplica que existe a propósito, y su consecuencia al escalar:
  * Circuit Breaker y Bulkhead (`Resilience.cs`): cada réplica cuenta sus
    fallas y abre su circuito por su cuenta.
  * Throttling (`OrdersService/Program.cs`): 20 lugares **por réplica**; con N
    réplicas, el límite efectivo es 20 × N.
  * `limit_req_zone` de nginx: memoria de **una** instancia del gateway; con
    dos gateways, el límite por IP se duplica.
  * Ajustes de las demos -`PatternSettings`, `ChaosSettings`, `CacheSettings`-:
    un `PUT` cambia **una** réplica.
  * Para un límite global, el contador tiene que salir del proceso -por
    ejemplo a Redis-, a cambio de una llamada remota por solicitud.
* Stateless no alcanza para escalar: el proyector no guardaba nada en memoria y
  aun así no se podía escalar, porque su corrección dependía del orden. Enlazar
  a Materialized View.
* Trade-offs: el almacén externo puede ser cuello de botella y agrega latencia.

**Ubicación propuesta:** el **primer lugar de `PATTERNS.md` donde aparece
estado por réplica**. Según el análisis es la inyección de fallas (chaos) en
"Demos de disponibilidad › Qué agrega a la aplicación base", seguida de
**Circuit Breaker**. Explicar un concepto general en una sección de
preparación puede no ser natural, así que **confirma con el usuario** cuál de
los dos prefiere. Enlaces desde: Circuit Breaker o chaos -el que no sea el
detallado-, Bulkhead, Throttling, Competing Consumers para disponibilidad,
Cache-Aside, Materialized View, External Configuration Store -ya menciona
réplicas con valores distintos-, A/B Testing y `ARCHITECTURE.md` donde
corresponda.

### Referencias para citar

Son fuentes que el curso ya usa. Verifica los enlaces antes de citarlos.

* AWS Well-Architected, REL04-BP04 *Make mutating operations idempotent*:
  https://docs.aws.amazon.com/wellarchitected/latest/framework/rel_prevent_interaction_failure_idempotent.html
* AWS Well-Architected, REL05-BP06 *Make systems stateless where possible*:
  https://docs.aws.amazon.com/wellarchitected/latest/framework/rel_mitigate_interaction_failure_stateless.html
* Azure Architecture Center, Retry pattern -"Consider whether the operation is
  idempotent"-: https://learn.microsoft.com/azure/architecture/patterns/retry
* Azure Architecture Center, Competing Consumers pattern -procesamiento
  idempotente-: https://learn.microsoft.com/azure/architecture/patterns/competing-consumers
* Azure Well-Architected, *Recommendations for designing a reliable scaling
  strategy* -diseño stateless, afinidad de sesión-:
  https://learn.microsoft.com/azure/well-architected/reliability/scaling
* IETF, *The Idempotency-Key HTTP Header Field* -borrador; verifica la versión
  vigente-: https://datatracker.ietf.org/doc/draft-ietf-httpapi-idempotency-key-header/
* RFC 9110, métodos seguros e idempotentes:
  https://datatracker.ietf.org/doc/html/rfc9110#name-idempotent-methods
* Green Software Foundation, *Implement stateless design* -vínculo con
  eficiencia energética-:
  https://patterns.greensoftware.foundation/architecture/system-topology/implement-stateless-design

---

## Cambio 5: demos

**No crear demos nuevas.** Extender las existentes con pasos que muestren cada
solución, sin romper lo que ya muestran. Propuesta:

* `demos/16-materialized-view.sh` -cambio 1-. Ya detiene el proyector y crea
  una Order. Extender ese paso:
  1. Con los proyectores detenidos, agregar una demora a Users con chaos, para
     que `AddOrderAsync` tarde.
  2. Crear la Order y esperar a que el worker la confirme.
  3. Levantar las 3 réplicas: `order.created` y `order.confirmed` llegan a la
     vez a réplicas distintas.
  4. Mostrar que la vista termina en `Confirmed` y explicar en el texto del
     paso que, sin la solución, quedaría en `Pending`.
  5. Restaurar el chaos en `cleanup`.
* `demos/07-competing-consumers.sh` o `demos/09-transactional-outbox.sh`
  -cambio 2-. Simular la republicación del *relay* insertando una fila
  duplicada en `outbox_messages` para una Order recién creada -por ejemplo con
  `docker compose exec orders-db mysql …`-. Las dos copias se publican juntas y
  van a workers distintos. Mostrar con `GET /orders/{id}/events` que hay un solo
  `OrderConfirmed`, y el log `[COMPETING CONSUMERS]` de la réplica que la
  descartó. Elige la demo donde encaje mejor y justifica.
* `demos/14-asynchronous-request-reply.sh` -cambio 3-. Enviar el `POST` dos
  veces con la misma `Idempotency-Key`: mismo `id`. Una tercera vez con otro
  cuerpo: `422`.
* `demos/24-messaging-bridge.sh`: verificar que sigue funcionando con el índice
  único y, si ya muestra la factura repetida, que el texto coincida con la
  nueva implementación.
* Usa las funciones de `demos/common.sh` -`title`, `step`, `chaos`,
  `reset_chaos`, etc.- y agrega ahí las que falten.
* Actualiza la sección **Cómo verlo** de cada patrón en `PATTERNS.md` para
  describir los pasos nuevos.

---

## Verificación

* `mysqlinit.sql` corre solo con un volumen nuevo: para probar los cambios de
  esquema hay que recrear el volumen (`docker compose down -v`). Si el
  `README.md` explica cómo levantar la demo, agrega esa advertencia.
* Compila la solución (`ANDIS2_UT4_DEMO1.sln`).
* Ejecuta las demos modificadas y además 07, 09, 12, 13, 14, 15, 16 y 24, que
  dependen del worker, el outbox o el proyector. Deben seguir mostrando lo
  mismo que antes, más los pasos nuevos.
* Verifica que los enlaces internos de `PATTERNS.md` y `ARCHITECTURE.md`
  resuelvan -incluidos los anclajes con tildes- y, si el repositorio tiene un
  *linter* de Markdown o de enlaces, que pase.

## Forma de trabajo sugerida

1. Confirmar con el usuario las dos ubicaciones de la documentación general.
2. Trabajar en una rama y hacer un commit por cambio (1 a 5), para que el
   docente pueda revisarlos por separado.
3. Abrir un *pull request* con un resumen de cada cambio: problema, solución,
   trade-offs y demo donde se ve.
