# Demos con Kubernetes

Los manifiestos de las demos 32 a 37. Qué es cada concepto de Kubernetes que
usan, y su análogo en Docker Compose, está en [Conceptos de
Kubernetes](../PATTERNS.md#conceptos-de-kubernetes), en PATTERNS.md.

Las demos 32 a 34 son Blue/Green, Rolling Upgrade y Canary
Testing en un clúster de [Kubernetes](https://kubernetes.io/). Son las demos 28
a 30 de Docker Compose, pero los pasos los hace [Argo
Rollouts](https://argoproj.github.io/rollouts/) o Kubernetes, y en la 33 y la 34
avanzar o volver atrás lo decide un análisis de las métricas de la versión
nueva. Qué muestra cada demo está en [Patrones de facilidad de
despliegue](../PATTERNS.md#patrones-de-facilidad-de-despliegue), en PATTERNS.md,
y el paso a paso, en
[06-deployability.azcli](../commands/06-deployability.azcli).

Las demos 35 a 37 son las de eficiencia energética: el *worker* de `Orders`
corre en el clúster, [Prometheus](https://prometheus.io/) estima la energía de
cada pod, y [KEDA](https://keda.sh/) cambia sus réplicas según la cola o según
una ventana. Qué muestra cada demo está en [Patrones de eficiencia
energética](../PATTERNS.md#patrones-de-eficiencia-energética), en PATTERNS.md, y
el paso a paso, en
[07-energy-efficiency.azcli](../commands/07-energy-efficiency.azcli).

## Cómo se usa

Se necesitan [Docker](https://www.docker.com/),
[kind](https://kind.sigs.k8s.io/),
[kubectl](https://kubernetes.io/docs/reference/kubectl/) y el [plugin de Argo
Rollouts](https://argoproj.github.io/argo-rollouts/features/kubectl-plugin/)
para kubectl. En macOS:

```bash
brew install kind kubectl argoproj/tap/kubectl-argo-rollouts
```

```bash
./demos/k8s-up.sh                  # crea el clúster, una sola vez
./demos/32-argo-blue-green.sh
./demos/33-k8s-rolling-upgrade.sh
./demos/34-argo-canary.sh
./demos/35-power-monitor.sh        # necesitan además docker compose up
./demos/36-scale-to-zero.sh
./demos/37-deferred-processing.sh
./demos/k8s-down.sh                # borra el clúster
```

Cada demo de despliegue aplica los manifiestos de su estrategia y los borra al
terminar: las tres usan el mismo nombre, `users`, y hay una sola por vez. Cada
demo de eficiencia energética aplica el *worker* y su [ScaledObject](https://keda.sh/docs/latest/reference/scaledobject-spec/), y los borra
al terminar.

## Qué hay en cada carpeta

| Archivo | Qué es |
| ------- | ------ |
| [kind.yaml](kind.yaml) | El clúster: un nodo, que publica en la máquina el Gateway en el puerto 8092 y Prometheus en el 30090 |
| [common.sh](common.sh) | Las funciones de las demos 32 a 37 |
| [base](base/) | Lo que comparten las tres demos. Lo aplica `demos/k8s-up.sh` |
| [users-db.yaml](base/users-db.yaml) | La base de `Users`, con los mismos datos iniciales que en Docker Compose |
| [users-config.yaml](base/users-config.yaml) | La configuración de `Users`. No lleva la versión: cada versión es una imagen |
| [gateway.yaml](base/gateway.yaml) | La entrada de los clientes: un *Gateway* de [Gateway API](https://gateway-api.sigs.k8s.io/), implementado con [NGINX Gateway Fabric](https://docs.nginx.com/nginx-gateway-fabric/) |
| [prometheus.yaml](base/prometheus.yaml) | Prometheus, que encuentra los pods de `Users` preguntándole a [Kubernetes](https://kubernetes.io/). También lee la CPU que usa cada contenedor, del [cAdvisor](https://github.com/google/cadvisor) del kubelet, y la que reserva, de [kube-state-metrics](https://github.com/kubernetes/kube-state-metrics), y estima la potencia de cada pod con las reglas de `energy.rules.yml` |
| [analysis-error-rate.yaml](base/analysis-error-rate.yaml) | El análisis de las demos 33 y 34: la fracción de errores de los pods de la versión nueva |
| [argo-rollouts-gateway-api.yaml](base/argo-rollouts-gateway-api.yaml) | El permiso del plugin de Gateway API de [Argo Rollouts](https://argoproj.github.io/rollouts/) para cambiar los pesos de las rutas |
| [blue-green](blue-green/) | La demo 32: el `Rollout` con la estrategia `blueGreen`, los Service `users-active` y `users-preview`, y sus rutas |
| [rolling-upgrade](rolling-upgrade/) | La demo 33: el [Deployment](https://kubernetes.io/docs/concepts/workloads/controllers/deployment/), el `Rollout` con análisis, y el Service y la ruta que comparten |
| [canary](canary/) | La demo 34: el `Rollout` con la estrategia `canary`, los Service `users-stable` y `users-canary`, y la ruta con los pesos |
| [energy](energy/) | Las demos 35 a 37 |
| [worker-config.yaml](energy/worker-config.yaml) | La configuración del *worker* de `Orders`, con los nombres de host de Docker Compose |
| [worker.yaml](energy/worker.yaml) | El Deployment del *worker*: 3 réplicas que reservan 0.25 vCPU cada una. La demo 35 |
| [rabbitmq-auth.yaml](energy/rabbitmq-auth.yaml) | Cómo llega [KEDA](https://keda.sh/) a la API de administración de RabbitMQ |
| [scaledobject-queue.yaml](energy/scaledobject-queue.yaml) | La demo 36: KEDA escala el *worker* de 0 a 6 réplicas según la cola `orders.process` |
| [scaledobject-window.yaml](energy/scaledobject-window.yaml) | La demo 37: KEDA enciende 3 réplicas solo mientras la ventana está abierta, según `GET /energy/window` de `orders-service` |

## Cómo se relaciona con Docker Compose

Las demos 32 a 34 no usan nada de Docker Compose: el clúster tiene su propia
base de `Users`, y `Users` no publica eventos, porque no hay
[RabbitMQ](https://www.rabbitmq.com/), ni lee el experimento de A/B Testing,
porque no hay [Unleash](https://www.getunleash.io/).

Las demos 35 a 37, en cambio, necesitan Docker Compose levantado. El *worker*
usa RabbitMQ, la base de `Orders`, `Users` y `Products` de Docker Compose: las
demos conectan el nodo de [kind](https://kind.sigs.k8s.io/) a la red de Docker Compose, y los pods llegan a
cada servicio por su nombre. Mientras corre el *worker* del clúster, el
`orders-worker` de Docker Compose queda en 0 réplicas.

El clúster usa alrededor de 2 GB de memoria. Para tener levantados a la vez el
clúster y Docker Compose, Docker necesita al menos 12 GB.

Las versiones de `Users` son imágenes construidas con el mismo
[Dockerfile](../src/UsersService/Dockerfile) que usa Docker Compose:

| Imagen | Argumentos | Qué hace |
| ------ | ---------- | -------- |
| `users-service:v1` | Ninguno | La versión `v1` |
| `users-service:v2` | `RELEASE_VERSION=2` | La versión `v2`, con el campo `DisplayName` |
| `users-service:v3-bad` | `RELEASE_VERSION=3` y `CHAOS_FAILURE_RATE=0.3` | Una versión con un error: falla en el 30% de las solicitudes desde que arranca, pero responde `/health` |

Para verlas bajo carga en Grafana, con el perfil `observability` de Docker
Compose levantado:

```bash
./demos/load-deployment.sh k8s-canary   # también: k8s-bluegreen, k8s-rolling
```

k6 corre en Docker Compose y llega al Gateway por la red de Docker: el script
suma el nodo de kind a esa red.

Las demos 35 a 37 tienen su propio escenario, con tres picos de `Orders` en 10
minutos:

```bash
./demos/load-energy.sh queue   # también: fixed, window
```

## Versiones

Las fija [k8s-up.sh](../demos/k8s-up.sh):

| Componente | Versión |
| ---------- | ------- |
| [Gateway API](https://gateway-api.sigs.k8s.io/) | 1.6.3 |
| NGINX Gateway Fabric | 2.8.0 |
| [Argo Rollouts](https://argoproj.github.io/rollouts/) | 1.10.2 |
| Plugin de Gateway API de Argo Rollouts | 0.17.0 |
| [KEDA](https://keda.sh/) | 2.21.0 |
| [kube-state-metrics](https://github.com/kubernetes/kube-state-metrics) | 2.20.0 |

