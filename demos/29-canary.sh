#!/usr/bin/env bash
# Canary Testing: una fracción pequeña de las solicitudes va a la versión nueva
# de Users. Si sus métricas son buenas, la fracción crece; si no, vuelve a cero,
# y solo esa fracción vio el problema.
#
# Bajo carga y en Grafana: ./demos/load-deployment.sh canary
source "$(dirname "$0")/common.sh"

cleanup() {
    echo
    note "Volviendo la entrada de despliegue a la versión v1..."
    v2_reset_chaos
    deploy_target users-service:8000
    reset_chaos
}
trap cleanup EXIT

title "Canary Testing"

step "Al principio, todo va a la versión v1"
versions 20

for pct in 10 50 100; do
    step "El $pct % de las solicitudes va a la versión v2"
    annotate "Canary: $pct % a la versión v2" despliegue
    deploy_canary "$pct"
    docker compose exec -T api-gateway grep '%' /etc/nginx/deployment-routes.conf | sed 's/^ */  /'
    versions 50
done
note "Con 100%, la versión v2 recibe todo: el lanzamiento terminó."

step "Otra versión v2, con un error: el canario vuelve al 10%"
deploy_canary 10
v2_chaos 1 0
annotate "Canary: la versión v2 falla" despliegue
versions 50
note "Solo fallan las solicitudes que fueron a la versión v2: cerca del 10%."

step "Las métricas del canario deciden: se vuelve a la versión v1"
annotate "Canary: vuelta atrás" despliegue
deploy_target users-service:8000
versions 20
note "El error lo vio una fracción de las solicitudes, y no todas, como habría"
note "pasado conmutando de una vez. Para decidir hacen falta métricas por versión."
