#!/usr/bin/env bash
# External Configuration Store: los límites, los feature flags y las URL están en
# un almacén central, Consul. Cambian sin redesplegar y sin reiniciar, y cada
# cambio les llega a todos los procesos que usan el valor.
#
# Bajo carga y en Grafana: ./demos/load-modifiability.sh config
source "$(dirname "$0")/common.sh"

# clear_store: vacía el almacén; cada proceso vuelve a sus valores por defecto.
clear_store() {
    curl -s -X DELETE "$CONSUL/v1/kv/andis/?recurse=true" > /dev/null
}

cleanup() {
    docker compose unpause consul > /dev/null 2>&1 || true
    clear_store
    reset_chaos
    reset_patterns
}
trap cleanup EXIT

JSON='Content-Type: application/json'
PRODUCT=60f7c2b5e1d3c2a1b8e4d124

# order <cantidad>: intenta crear una Order para el User 2.
order() {
    local out
    out=$(curl -s -w '\n%{http_code}' -X POST "$ORDERS_DIRECT/orders/" -H "$JSON" \
        -d "{\"userId\": 2, \"productId\": \"$PRODUCT\", \"quantity\": $1}")
    printf '  Order de %-2s unidades: HTTP %s  %s\n' "$1" "${out##*$'\n'}" "$(head -c 90 <<< "${out%$'\n'*}")"
}

# started: cuándo arrancaron los contenedores; se compara al final.
started() {
    docker inspect -f '{{.Name}} {{.State.StartedAt}}' $(docker compose ps -q orders-service orders-worker products-service)
}

title "External Configuration Store"
before=$(started)

step "Se vacía el almacén: la demo empieza con los valores por defecto"
clear_store
sleep 3
since=$(date +%s)

step "Un límite: cuántas unidades de un Product se pueden pedir en una Order; sin valor, no hay límite"
order 10
annotate "Orders/MaxQuantity = 3"
config_set Orders/MaxQuantity 3
order 10
order 2
note "Nadie reinició Orders: leyó el valor nuevo del almacén."

step "Un feature flag: si el detalle de las Orders muestra el precio unitario; sin valor, no lo muestra"
details
config_set Features/ProductPrices true
details
config_delete Features/ProductPrices

step "Un valor de otro servicio: cuántos segundos dura la lista de Products en la caché de Redis"
ttl() {
    docker compose exec -T redis redis-cli del products:all > /dev/null
    curl -s -o /dev/null "$GATEWAY/products/"
    echo "  segundos de vida de la lista de Products en Redis: $(docker compose exec -T redis redis-cli ttl products:all | tr -d '\r')"
}
ttl
config_set Products/CacheTtlSeconds 5
ttl

step "Una URL: la dirección con la que Orders llama a Products; se cambia y se restaura, sin redesplegar"
only_pattern ninguno
config_set ProductsService/BaseUrl http://products-service:8999/products/
details
config_delete ProductsService/BaseUrl
details
note "Así se mueve una dependencia a otra dirección sin tocar las imágenes."

step "Si el almacén no responde, los servicios siguen con los últimos valores"
annotate "Consul pausado"
docker compose pause consul > /dev/null 2>&1
note "Consul pausado; se esperan 5 segundos..."
sleep 5
order 10
docker compose unpause consul > /dev/null 2>&1
sleep 3
note "El almacén no puede ser una dependencia sin la cual nada funciona. Si un"
note "proceso arrancara con el almacén caído, usaría sus valores por defecto."

step "Lo que vio cada proceso"
sleep 1
docker compose logs --no-color --since "$since" orders-service orders-worker products-service 2>&1 \
    | grep -E 'EXTERNAL CONFIG\] Orders:MaxQuantity = 3' \
    | sed -E 's/^([a-z0-9-]+) *\| *\[EXTERNAL CONFIG\] /  \1: /; s/^([a-z0-9-]+) *\| *.*\] /  \1: /' | sort
note "Un mismo cambio, leído por cada proceso, sin que nadie se lo avise."

step "Ningún contenedor se reinició"
[ "$before" = "$(started)" ] && echo "  Las fechas de arranque de todos los contenedores siguen iguales."
