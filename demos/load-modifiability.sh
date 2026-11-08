#!/usr/bin/env bash
# Antes y después de los patrones de facilidad de modificación, bajo carga y en
# Grafana. Lanza un escenario de k6 y hace los cambios mientras corre, marcando
# cada uno en el dashboard. Necesita el perfil de observabilidad:
#
#   docker compose --profile observability up -d --wait
#   ./demos/load-modifiability.sh <acl|config|bridge|strangler|bff>
#
# Los paneles están en la fila "Facilidad de modificación".
source "$(dirname "$0")/common.sh"

OVERRIDE=$(mktemp)
K6_PID=""

# load <escenario>: lanza k6 en segundo plano; sus resultados quedan en la terminal.
load() {
    docker compose run --rm k6 run --quiet "/scripts/$1" &
    K6_PID=$!
}

# mark <texto>: marca el momento en Grafana y lo muestra en la terminal.
mark() {
    annotate "$1"
    note "$(date +%T)  $1"
}

strangler() {
    title "Strangler Fig bajo carga"
    load strangler-fig.js
    sleep 15
    mark "Products migrado"
    strangler_route products products-service:8000
    sleep 14
    mark "Users migrado"
    strangler_route users users-service:8000
    sleep 14
    mark "Orders migrado"
    strangler_route orders orders-service:8000
    sleep 14
    mark "Monolito apagado"
    docker compose stop monolith > /dev/null 2>&1
}

strangler_cleanup() {
    docker compose up -d --force-recreate --wait monolith > /dev/null 2>&1
    strangler_route users monolith:8000
    strangler_route products monolith:8000
    strangler_route orders monolith:8000
}

# La misma línea de configuración, en las variables de entorno del contenedor:
# recrearlo es el costo de esta forma de cambiar un valor.
recreate_orders() {
    if [ "$1" = limite ]; then
        printf 'services:\n  orders-service:\n    environment:\n      Orders__MaxQuantity: "3"\n' > "$OVERRIDE"
        docker compose -f docker-compose.yaml -f "$OVERRIDE" up -d --no-deps --wait orders-service > /dev/null 2>&1
    else
        docker compose up -d --no-deps --wait orders-service > /dev/null 2>&1
    fi
}

config() {
    title "External Configuration Store bajo carga"
    load external-configuration.js
    sleep 15
    mark "Límite por variable de entorno: se recrea orders-service"
    recreate_orders limite
    sleep 15
    mark "Se quita la variable de entorno: se recrea orders-service"
    recreate_orders sin-limite
    sleep 20
    mark "Límite en el almacén: sin reiniciar"
    curl -s -X PUT "$CONSUL/v1/kv/andis/Orders/MaxQuantity" -d 3 > /dev/null
    sleep 20
    mark "Se borra el límite del almacén"
    curl -s -X DELETE "$CONSUL/v1/kv/andis/Orders/MaxQuantity" > /dev/null
}

config_cleanup() {
    curl -s -X DELETE "$CONSUL/v1/kv/andis/Orders/MaxQuantity" > /dev/null
    docker compose up -d --no-deps --wait orders-service > /dev/null 2>&1
}

bridge() {
    title "Messaging Bridge bajo carga"
    load messaging-bridge.js
    sleep 20
    mark "ActiveMQ detenido"
    docker compose stop legacy-broker > /dev/null 2>&1
    sleep 30
    mark "ActiveMQ vuelve"
    docker compose start legacy-broker > /dev/null 2>&1
}

bridge_cleanup() {
    docker compose start legacy-broker > /dev/null 2>&1
}

acl() {
    title "Anti-corruption Layer bajo carga"
    load anti-corruption-layer.js
    sleep 20
    mark "Users con 200 ms de demora"
    chaos users 0 200
    sleep 25
    mark "Users vuelve a la normalidad"
    reset_chaos
}

acl_cleanup() {
    reset_chaos
}

bff() {
    title "Backends for Frontends bajo carga"
    load backends-for-frontends.js
    sleep 25
    mark "Se redespliega bff-mobile"
    docker compose up -d --no-deps --force-recreate --wait bff-mobile > /dev/null 2>&1
    mark "bff-mobile redesplegado"
}

bff_cleanup() {
    docker compose up -d --no-deps --wait bff-mobile > /dev/null 2>&1
}

pattern=${1:-}
case "$pattern" in
    acl|config|bridge|strangler|bff) ;;
    *) echo "Uso: $0 <acl|config|bridge|strangler|bff>" >&2; exit 2 ;;
esac

cleanup() {
    [ -n "$K6_PID" ] && wait "$K6_PID" 2> /dev/null || true
    note "Dejando el entorno como estaba..."
    "${pattern}_cleanup"
    rm -f "$OVERRIDE"
}
trap cleanup EXIT

"$pattern"
