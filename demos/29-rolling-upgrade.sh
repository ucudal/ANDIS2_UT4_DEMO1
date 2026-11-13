#!/usr/bin/env bash
# Rolling Upgrade: las instancias de Users se reemplazan de a una por la versión
# nueva. Cada lote espera a que la instancia nueva esté sana antes de detener
# una vieja, así que el servicio nunca se corta, aunque durante un rato
# responden las dos versiones.
#
# Bajo carga y en Grafana: ./demos/load-deployment.sh rolling
source "$(dirname "$0")/common.sh"

PROBES=$(mktemp)
RUNNING=$(mktemp)
cleanup() {
    rm -f "$RUNNING"
    wait 2> /dev/null || true
    echo
    note "Borrando las instancias de la demo y volviendo a la versión v1..."
    deploy_target users-service:8000
    pool_cleanup
    rm -f "$PROBES"
    reset_chaos
}
trap cleanup EXIT

# pool: qué instancias tiene el grupo users-pool, según el DNS de Docker.
pool() {
    docker compose exec -T api-gateway sh -c 'getent ahostsv4 users-pool | awk "{ print \$1 }" | sort -u' \
        | while read -r ip; do
            printf '  %s  %s\n' "$ip" "$(docker ps --format '{{.Names}}' --filter "network=$(app_network)" \
                | while read -r c; do
                    [ "$(docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' "$c")" = "$ip" ] && echo "$c"
                done)"
        done
}

title "Rolling Upgrade"

step "El grupo arranca con tres instancias de la versión v1"
note "Se crean a partir de la definición de users-service, con el alias users-pool."
pool_start_v1
deploy_target users-pool:8000
pool
versions 12
note "nginx reparte las solicitudes entre las IP que devuelve el DNS para users-pool."

touch "$RUNNING"
deploy_probe "$PROBES" "$RUNNING" &

for n in 1 2 3; do
    step "Lote $n: una instancia de la versión v2 entra, y recién entonces sale una de la v1"
    annotate "Rolling Upgrade: lote $n" despliegue
    pool_replace "$n"
    sleep 6
    pool
    versions 12
done

rm -f "$RUNNING"
wait

step "Durante todo el reemplazo, una sonda pidió el User de alice cada 0.1\""
tally "$PROBES"
note "Las dos versiones respondieron a la vez durante el reemplazo: tienen que ser"
note "compatibles entre sí, y con la base que comparten. Si una instancia nueva no"
note "hubiera estado sana, el lote se detenía antes de sacar una vieja."
