#!/usr/bin/env bash
# Las estrategias de despliegue bajo carga y en Grafana. Lanza un escenario de k6
# y despliega la versión v2 de Users mientras corre, marcando cada paso en el
# dashboard. Necesita el perfil de observabilidad:
#
#   docker compose --profile observability up -d --wait
#   ./demos/load-deployment.sh <bluegreen|rolling|canary|ab>
#
# Los escenarios k8s-bluegreen, k8s-rolling y k8s-canary son las demos 31 a 33:
# k6 apunta al Gateway del clúster, y el despliegue lo hace Argo Rollouts o
# Kubernetes. Necesitan además el clúster, ./demos/k8s-up.sh, pero no el resto
# de Docker Compose:
#
#   ./demos/load-deployment.sh <k8s-bluegreen|k8s-rolling|k8s-canary>
#
# Los paneles están en la fila "Facilidad de despliegue".
source "$(dirname "$0")/common.sh"

K6_PID=""
# Variables de entorno para k6: las cambian los escenarios de Kubernetes.
K6_ENV=()

# load <escenario>: lanza k6 en segundo plano; sus resultados quedan en la terminal.
load() {
    docker compose run --rm ${K6_ENV[@]+"${K6_ENV[@]}"} k6 run --quiet "/scripts/$1" &
    K6_PID=$!
}

# mark <texto>: marca el momento en Grafana y lo muestra en la terminal.
mark() {
    annotate "$1" despliegue
    note "$(date +%T)  $1"
}

bluegreen() {
    title "Blue/Green Deployment bajo carga"
    load blue-green.js
    sleep 20
    mark "Se conmuta a la versión v2"
    deploy_target users-service-v2:8000
    sleep 20
    mark "Vuelta atrás a la versión v1"
    deploy_target users-service:8000
}

bluegreen_cleanup() {
    deploy_target users-service:8000
}

rolling() {
    title "Rolling Upgrade bajo carga"
    note "Preparando el grupo con tres instancias de la versión v1..."
    pool_start_v1
    deploy_target users-pool:8000
    load rolling-upgrade.js
    sleep 20
    for n in 1 2 3; do
        mark "Lote $n: entra una instancia de la versión v2, sale una de la v1"
        pool_replace "$n"
        sleep 20
    done
}

rolling_cleanup() {
    deploy_target users-service:8000
    pool_cleanup
}

canary() {
    title "Canary Testing bajo carga"
    load canary.js
    sleep 15
    mark "Canary: 10% a la versión v2"
    deploy_canary 10
    sleep 15
    mark "Canary: 50% a la versión v2"
    deploy_canary 50
    sleep 15
    mark "Canary: la versión v2 falla"
    v2_chaos 1 0
    sleep 15
    mark "Canary: vuelta atrás a la versión v1"
    deploy_target users-service:8000
}

canary_cleanup() {
    v2_reset_chaos
    deploy_target users-service:8000
}

ab() {
    title "A/B Testing bajo carga"
    note "Levantando Unleash, con la mitad de los Users en cada variante..."
    experiment_start
    mark "La versión v2, con el experimento, en producción"
    deploy_target users-service-v2:8000
    load ab-testing.js
}

ab_cleanup() {
    deploy_target users-service:8000
}

# Las demos 31 a 33 bajo carga. k6 corre en Docker Compose y llega al Gateway del
# clúster por la red de Docker; sus métricas van al Prometheus de Compose, como
# en las demos 27 a 30. El panel "Errores de cada versión, según Users" lee
# el Prometheus del clúster.

k8s_bluegreen() {
    title "Blue/Green con Argo Rollouts bajo carga"
    note "Aplicando k8s/blue-green, con la versión v1..."
    strategy_apply blue-green
    load blue-green.js
    sleep 15
    mark "Argo Rollouts: la versión v2 en preview, sin tráfico"
    rollouts set image users users=users-service:v2 > /dev/null
    wait_phase Paused
    sleep 5
    mark "Argo Rollouts: promote, se conmuta a la versión v2"
    rollouts promote users > /dev/null
    sleep 20
    mark "Argo Rollouts: undo, vuelta atrás a la versión v1"
    rollouts undo users > /dev/null
}

k8s_bluegreen_cleanup() {
    strategy_delete blue-green
}

# Solo la parte del Deployment de la demo 32: la versión con el error queda
# hasta que alguien vuelve atrás. La vuelta atrás automática de Argo Rollouts se
# ve bajo carga con k8s-canary.
k8s_rolling() {
    title "Rolling Upgrade con un Deployment de Kubernetes bajo carga"
    note "Aplicando el Deployment de k8s/rolling-upgrade, con 3 pods de la versión v1..."
    kubectl apply -f k8s/rolling-upgrade/service.yaml -f k8s/rolling-upgrade/deployment.yaml > /dev/null
    kubectl -n andis rollout status deployment/users --timeout=180s > /dev/null
    until [ "$(get_user 1 -o /dev/null -w '%{http_code}')" = 200 ]; do sleep 1; done
    load rolling-upgrade.js
    sleep 20
    mark "Deployment: se reemplazan los pods por la versión v2"
    kubectl -n andis set image deployment/users users=users-service:v2 > /dev/null
    kubectl -n andis rollout status deployment/users --timeout=180s > /dev/null
    sleep 25
    mark "Deployment: se reemplazan los pods por una versión v3 con un error"
    kubectl -n andis set image deployment/users users=users-service:v3-bad > /dev/null
    kubectl -n andis rollout status deployment/users --timeout=180s > /dev/null
    sleep 25
    mark "Deployment: vuelta atrás a mano, a la versión v2"
    kubectl -n andis rollout undo deployment/users > /dev/null 2>&1
}

k8s_rolling_cleanup() {
    kubectl -n andis delete deployment users --ignore-not-found --wait=false > /dev/null 2>&1 || true
    strategy_delete rolling-upgrade
}

k8s_canary() {
    title "Canary con Argo Rollouts bajo carga"
    note "Aplicando k8s/canary, con la versión v1..."
    strategy_apply canary
    # La v2 tarda un minuto y medio en llegar al 100%, y la v3 medio minuto en
    # volver atrás: más que los 90 segundos del escenario de la demo 29.
    K6_ENV+=(-e DURATION=200s)
    load canary.js
    sleep 10
    mark "Argo Rollouts: canary de la versión v2"
    rollouts set image users users=users-service:v2 > /dev/null
    sleep 3
    wait_phase Healthy
    mark "Argo Rollouts: la versión v2 llegó al 100%"
    sleep 10
    mark "Argo Rollouts: canary de una versión v3 con un error"
    rollouts set image users users=users-service:v3-bad > /dev/null
    wait_phase Degraded
    mark "Argo Rollouts: el análisis falló, vuelta atrás a la versión v2"
}

k8s_canary_cleanup() {
    strategy_delete canary
}

pattern=${1:-}
case "$pattern" in
    bluegreen|rolling|canary|ab) ;;
    k8s-bluegreen|k8s-rolling|k8s-canary)
        source k8s/common.sh
        # k6 llega al Gateway del clúster por la red de Docker Compose, como a la
        # entrada de despliegue en las demos 27 a 30: el nodo de kind se suma a
        # esa red. Por host.docker.internal pasaría por el reenvío de puertos de
        # Docker Desktop, que con 20 solicitudes por segundo pierde algunas.
        network=$(docker inspect -f '{{range $name, $_ := .NetworkSettings.Networks}}{{$name}}{{end}}' \
            "$(docker compose ps -q prometheus)")
        docker network connect "$network" andis-control-plane 2> /dev/null || true
        K6_ENV=(-e DEPLOY=http://andis-control-plane:30092)
        ;;
    *) echo "Uso: $0 <bluegreen|rolling|canary|ab|k8s-bluegreen|k8s-rolling|k8s-canary>" >&2; exit 2 ;;
esac
function=${pattern//-/_}

cleanup() {
    [ -n "$K6_PID" ] && wait "$K6_PID" 2> /dev/null || true
    note "Dejando el entorno como estaba..."
    "${function}_cleanup"
    [[ $pattern == k8s-* ]] || reset_chaos
}
trap cleanup EXIT

"$function"
