#!/usr/bin/env bash
# Las estrategias de despliegue bajo carga y en Grafana. Lanza un escenario de k6
# y despliega la versión v2 de Users mientras corre, marcando cada paso en el
# dashboard. Necesita el perfil de observabilidad:
#
#   docker compose --profile observability up -d --wait
#   ./demos/load-deployment.sh <bluegreen|rolling|canary|ab>
#
# Los paneles están en la fila "Facilidad de despliegue".
source "$(dirname "$0")/common.sh"

K6_PID=""

# load <escenario>: lanza k6 en segundo plano; sus resultados quedan en la terminal.
load() {
    docker compose run --rm k6 run --quiet "/scripts/$1" &
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
    mark "Canary: 10 % a la versión v2"
    deploy_canary 10
    sleep 15
    mark "Canary: 50 % a la versión v2"
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
    mark "La versión v2, con el experimento, en producción"
    deploy_target users-service-v2:8000
    load ab-testing.js
}

ab_cleanup() {
    deploy_target users-service:8000
}

pattern=${1:-}
case "$pattern" in
    bluegreen|rolling|canary|ab) ;;
    *) echo "Uso: $0 <bluegreen|rolling|canary|ab>" >&2; exit 2 ;;
esac

cleanup() {
    [ -n "$K6_PID" ] && wait "$K6_PID" 2> /dev/null || true
    note "Dejando el entorno como estaba..."
    "${pattern}_cleanup"
    reset_chaos
}
trap cleanup EXIT

"$pattern"
