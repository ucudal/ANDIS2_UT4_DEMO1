#!/usr/bin/env bash
# Mide el tamaño del cambio de los patrones de facilidad de modificación. Cómo se
# calcula cada número está en PATTERNS.md, en "Tamaño del cambio".
#
# Primera parte: cuánto costó introducir cada patrón, en líneas de código, contra
# el commit anterior a estas demos. Segunda parte: cuánto cuesta cada cambio
# posterior, medido sobre la aplicación en marcha.
source "$(dirname "$0")/common.sh"

# El último commit sin los patrones de facilidad de modificación.
BASE=3a02c77
STAMPS="-f docker-compose.yaml -f docker-compose.stamps.yaml"
OVERRIDE=$(mktemp)

# Líneas efectivas: sin líneas vacías ni comentarios.
effective() { grep -vE '^[[:space:]]*(//|#|$)' || true; }
count() { effective | wc -l | tr -d ' '; }

# new_lines <archivos o carpetas>: líneas efectivas de los archivos nuevos.
new_lines() {
    find "$@" -type f \( -name '*.cs' -o -name '*.csproj' -o -name 'Dockerfile' \
        -o -name '*.yaml' -o -name '*.conf' \) -not -path '*/bin/*' -not -path '*/obj/*' \
        -exec cat {} + | count
}

new_files() {
    find "$@" -type f \( -name '*.cs' -o -name '*.csproj' -o -name 'Dockerfile' \
        -o -name '*.yaml' -o -name '*.conf' \) -not -path '*/bin/*' -not -path '*/obj/*' \
        | wc -l | tr -d ' '
}

# changed <archivos>: líneas agregadas y borradas de archivos existentes, sin
# líneas vacías ni comentarios.
changed() {
    git diff -U0 "$BASE" -- "$@" | grep -E '^[+-]' | grep -vE '^(\+\+\+|---)' | cut -c2- || true
}

# Los archivos que tocan varios patrones se reparten: cada línea cuenta para el
# patrón al que pertenece, por su contenido.
compose_service() { # <servicio>: líneas del servicio en docker-compose.yaml
    awk -v name="  $1:" '$0 == name { on = 1; print; next } /^  [a-z]/ { on = 0 } on' docker-compose.yaml | count
}
nginx_stamps=$(($(awk '/^    map \$http_x_region/,/^    }/' src/ApiGateway/nginxconf/nginx.conf | count) + 1 + 6))
nginx_strangler=$(($(awk '/log_format strangler/ { print } /listen 8090;/ { on = 1 } on { print } on && /^    }/ { on = 0 }' src/ApiGateway/nginxconf/nginx.conf | count) + 1))
compose_consul=$(($(compose_service consul) + $(grep -c 'Consul__Url' docker-compose.yaml)))
compose_bridge=$(($(compose_service legacy-broker) + $(compose_service legacy-erp) + $(compose_service messaging-bridge)))
compose_strangler=$(($(compose_service monolith) + 1))

config_existing=$(changed src/OrdersService/Controllers/OrdersController.cs src/OrdersService/Program.cs \
    src/ProductsService/Program.cs src/ProductsService/ProductCache.cs src/Common/Observability.cs | count)
bridge_existing=$(($(changed src/Common/Events.cs src/OrdersService/Models/OrderEvent.cs \
    src/OrdersService/Messaging/OrderDetailsProjector.cs | count) \
    + $(changed src/OrdersService/Messaging/OrderProcessingWorker.cs | grep -c 'OrderConfirmed')))
strangler_existing=$(changed src/ApiGateway/Dockerfile | count)

title "Costo de introducir cada patrón, en líneas efectivas"
# printf cuenta bytes y no caracteres: una tilde correría la columna.
left() { printf '%s%*s' "$2" "$(($1 - ${#2}))" ''; }
right() { printf '%*s%s' "$(($1 - ${#2}))" '' "$2"; }

row() {
    printf '  %s %s %s %s %s\n' "$(left 30 "$1")" "$(right 8 "$2")" "$(right 10 "$3")" \
        "$(right 12 "$4")" "$(right 8 "$5")"
}
row "patrón" "archivos" "nuevas" "existentes" "total"
total() {
    local files=$2 new=$3 existing=$4
    row "$1" "$files" "$new" "$existing" "$((new + existing))"
}
total "Deployment Stamps" 1 "$(new_lines docker-compose.stamps.yaml)" "$nginx_stamps"
total "External Configuration Store" 1 "$(new_lines src/Common/ExternalConfiguration.cs)" "$((config_existing + compose_consul))"
total "Messaging Bridge" "$(new_files src/MessagingBridge src/LegacyErp)" "$(new_lines src/MessagingBridge src/LegacyErp)" "$((bridge_existing + compose_bridge))"
total "Strangler Fig" "$(($(new_files src/Monolith) + 1))" "$(($(new_lines src/Monolith) + $(new_lines src/ApiGateway/nginxconf/strangler-routes.conf)))" "$((nginx_strangler + compose_strangler + strangler_existing))"

# Control: lo repartido tiene que sumar lo que dice git.
expected=$(changed docker-compose.yaml src/ApiGateway/nginxconf/nginx.conf | count)
split=$((nginx_stamps + nginx_strangler + compose_consul + compose_bridge + compose_strangler))
note "Control de docker-compose.yaml y nginx.conf: repartidas $split, según git $expected."

# Estado de cada contenedor: al comparar dos fotos se ve cuáles se reiniciaron.
snapshot() {
    docker compose ps -q | xargs docker inspect -f '{{.Name}} {{.State.StartedAt}}' | sort
}
restarted() { diff "$1" <(snapshot) | grep -c '^>' || true; }

# now_ms: la hora en milisegundos. date de macOS no tiene %N.
now_ms() { perl -MTime::HiRes=time -e 'printf "%d", time * 1000'; }
elapsed() { awk -v a="$1" -v b="$(now_ms)" 'BEGIN { printf "%.1f s", (b - a) / 1000 }'; }

post_code() {
    curl -s -m 2 -o /dev/null -w '%{http_code}' -X POST "$ORDERS_DIRECT/orders/" -H 'Content-Type: application/json' \
        -d '{"userId": 2, "productId": "60f7c2b5e1d3c2a1b8e4d124", "quantity": 10}' || true
}
wait_limit() {
    until [ "$(post_code)" = 400 ]; do sleep 0.2; done
}

cleanup() {
    curl -s -X DELETE "$CONSUL/v1/kv/andis/Orders/MaxQuantity" > /dev/null
    docker compose up -d --no-deps --wait orders-service > /dev/null 2>&1
    strangler_route products monolith:8000
    # shellcheck disable=SC2046
    docker compose $STAMPS rm -sfv $(docker compose $STAMPS config --services | grep '^stamp-b-') > /dev/null 2>&1
    rm -f "$OVERRIDE" "$BEFORE"
    reset_chaos
}
trap cleanup EXIT

title "Costo de cada cambio posterior"
measure() {
    printf '  %s %s %s %s\n' "$(left 44 "$1")" "$(right 6 "$2")" "$(right 11 "$3")" "$(right 8 "$4")"
}
measure "cambio" "líneas" "reiniciados" "demora"
BEFORE=$(mktemp)

snapshot > "$BEFORE"
start=$(now_ms)
curl -s -X PUT "$CONSUL/v1/kv/andis/Orders/MaxQuantity" -d 3 > /dev/null
wait_limit
measure "Límite, en el almacén" 0 "$(restarted "$BEFORE")" "$(elapsed "$start")"
curl -s -X DELETE "$CONSUL/v1/kv/andis/Orders/MaxQuantity" > /dev/null
sleep 3

snapshot > "$BEFORE"
printf 'services:\n  orders-service:\n    environment:\n      Orders__MaxQuantity: "3"\n' > "$OVERRIDE"
start=$(now_ms)
docker compose -f docker-compose.yaml -f "$OVERRIDE" up -d --no-deps orders-service > /dev/null 2>&1
wait_limit
measure "Límite, en una variable de entorno" 1 "$(restarted "$BEFORE")" "$(elapsed "$start")"
docker compose up -d --no-deps --wait orders-service > /dev/null 2>&1

snapshot > "$BEFORE"
start=$(now_ms)
strangler_route products products-service:8000
until curl -s -D - -o /dev/null "$FACADE/products/" | grep -qi 'x-served-by: products-service'; do sleep 0.1; done
measure "Migrar una ruta del monolito" 1 "$(restarted "$BEFORE")" "$(elapsed "$start")"

snapshot > "$BEFORE"
start=$(now_ms)
docker compose $STAMPS up -d --wait > /dev/null 2>&1
region_time=$(elapsed "$start")
region_lines=$(($(new_lines docker-compose.stamps.yaml) + 2))
new_containers=$(docker compose $STAMPS ps -q | wc -l | tr -d ' ')
existing=$(wc -l < "$BEFORE" | tr -d ' ')
measure "Agregar una región" "$region_lines" "$(($(diff "$BEFORE" <(docker compose $STAMPS ps -q | xargs docker inspect -f '{{.Name}} {{.State.StartedAt}}' | sort) | grep -c '^<' || true)))" "$region_time"
note "La región agrega $((new_containers - existing)) contenedores, sin tocar los $existing que ya estaban."
