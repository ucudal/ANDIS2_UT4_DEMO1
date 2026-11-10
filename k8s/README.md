# Demos de despliegue con Kubernetes

Los manifiestos de las demos 31 a 33: Blue/Green, Rolling Upgrade y Canary
Testing en un clúster de [Kubernetes](https://kubernetes.io/). Son las demos 27
a 29 de Docker Compose, pero los pasos los hace [Argo
Rollouts](https://argoproj.github.io/rollouts/) o Kubernetes, y en la 32 y la 33
avanzar o volver atrás lo decide un análisis de las métricas de la versión
nueva. Qué muestra cada demo está en
[PATTERNS.md](../PATTERNS.md#patrones-de-facilidad-de-despliegue), y el paso a
paso, en [06-deployability.azcli](../commands/06-deployability.azcli).

## Cómo se usa

Se necesitan [Docker](https://www.docker.com/), [kind](https://kind.sigs.k8s.io/),
[kubectl](https://kubernetes.io/docs/reference/kubectl/) y el [plugin de Argo
Rollouts](https://argoproj.github.io/argo-rollouts/features/kubectl-plugin/)
para kubectl. En macOS:

```bash
brew install kind kubectl argoproj/tap/kubectl-argo-rollouts
```

```bash
./demos/k8s-up.sh                  # crea el clúster, una sola vez
./demos/31-argo-blue-green.sh
./demos/32-k8s-rolling-upgrade.sh
./demos/33-argo-canary.sh
./demos/k8s-down.sh                # borra el clúster
```

Cada demo aplica los manifiestos de su estrategia y los borra al terminar: las
tres usan el mismo nombre, `users`, y hay una sola por vez.

## Qué hay en cada carpeta

| Archivo | Qué es |
| ------- | ------ |
| [kind.yaml](kind.yaml) | El clúster: un nodo, que publica en la máquina el Gateway en el puerto 8092 y Prometheus en el 30090 |
| [common.sh](common.sh) | Las funciones de las demos 31 a 33 |
| [base](base/) | Lo que comparten las tres demos. Lo aplica `demos/k8s-up.sh` |
| [base/users-db.yaml](base/users-db.yaml) | La base de `Users`, con los mismos datos iniciales que en Docker Compose |
| [base/users-config.yaml](base/users-config.yaml) | La configuración de `Users`. No lleva la versión: cada versión es una imagen |
| [base/gateway.yaml](base/gateway.yaml) | La entrada de los clientes: un *Gateway* de [Gateway API](https://gateway-api.sigs.k8s.io/), implementado con [NGINX Gateway Fabric](https://docs.nginx.com/nginx-gateway-fabric/) |
| [base/prometheus.yaml](base/prometheus.yaml) | [Prometheus](https://prometheus.io/), que encuentra los pods de `Users` preguntándole a Kubernetes |
| [base/analysis-error-rate.yaml](base/analysis-error-rate.yaml) | El análisis de las demos 32 y 33: la fracción de errores de los pods de la versión nueva |
| [base/argo-rollouts-gateway-api.yaml](base/argo-rollouts-gateway-api.yaml) | El permiso del plugin de Gateway API de Argo Rollouts para cambiar los pesos de las rutas |
| [blue-green](blue-green/) | La demo 31: el `Rollout` con la estrategia `blueGreen`, los Service `users-active` y `users-preview`, y sus rutas |
| [rolling-upgrade](rolling-upgrade/) | La demo 32: el Deployment, el `Rollout` con análisis, y el Service y la ruta que comparten |
| [canary](canary/) | La demo 33: el `Rollout` con la estrategia `canary`, los Service `users-stable` y `users-canary`, y la ruta con los pesos |

## Cómo se relaciona con Docker Compose

El clúster no usa nada de Docker Compose: tiene su propia base de `Users`, y
`Users` no publica eventos, porque no hay [RabbitMQ](https://www.rabbitmq.com/),
ni lee el experimento de A/B Testing, porque no hay
[Unleash](https://www.getunleash.io/). Las dos cosas se pueden levantar a la
vez, si Docker tiene memoria suficiente: el clúster usa alrededor de 1,5 GB.

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

## Versiones

Las fija [demos/k8s-up.sh](../demos/k8s-up.sh):

| Componente | Versión |
| ---------- | ------- |
| Gateway API | 1.6.3 |
| NGINX Gateway Fabric | 2.8.0 |
| Argo Rollouts | 1.10.2 |
| Plugin de Gateway API de Argo Rollouts | 0.17.0 |
