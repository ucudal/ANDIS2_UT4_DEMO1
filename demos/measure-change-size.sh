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

# diff_base <archivo>: el diff del archivo contra BASE, sin contar los cambios
# que son solo de mayúsculas y minúsculas, como renombrar una variable de entorno
# de Consul__Url a CONSUL__URL: no son parte de ningún patrón.
diff_base() {
    diff -i -U0 <(git show "$BASE:$1" 2> /dev/null) "$1" || true
}

# changed <archivos>: líneas agregadas y borradas de archivos existentes, sin
# líneas vacías ni comentarios.
changed() {
    local f
    for f in "$@"; do
        diff_base "$f" | grep -E '^[+-]' | grep -vE '^(\+\+\+|---)' | cut -c2- || true
    done
}

# Los archivos que tocan varios patrones se reparten: cada línea cuenta para el
# patrón al que pertenece, por su contenido.
compose_service() { # <servicio>: líneas del servicio en docker-compose.yaml
    awk -v name="  $1:" '$0 == name { on = 1; print; next } /^  [a-z]/ { on = 0 } on' docker-compose.yaml | count
}
nginx_location() { # <ruta>: líneas de ese location en la entrada HTTP de nginx.conf
    awk -v start="        location $1 {" '$0 == start { on = 1 } on { print } on && /^        }/ { on = 0 }' \
        src/ApiGateway/nginxconf/nginx.conf | count
}
nginx_bff=$(($(nginx_location /web/) + $(nginx_location /mobile/)))
nginx_strangler=$(($(awk '/log_format strangler/ { print } /listen 8090;/ { on = 1 } on { print } on && /^    }/ { on = 0 }' src/ApiGateway/nginxconf/nginx.conf | count) + 1))
compose_consul=$(($(compose_service consul) + $(grep -c 'CONSUL__URL' docker-compose.yaml)))
compose_bridge=$(compose_service messaging-bridge)
compose_bff=$(($(compose_service bff-web) + $(compose_service bff-mobile)))
# El puerto 8090 de la fachada, en api-gateway.
compose_strangler=1
# Fuera de los patrones: los sistemas simulados, que ya existirían en un caso real,
# y Purchases, el cliente nuevo de Anti-corruption Layer. Del patrón es solo su capa.
outside=$(($(compose_service legacy-broker) + $(compose_service legacy-erp) + $(compose_service monolith) \
    + $(compose_service purchases-service) + $(nginx_location /purchases/)))
# Las demos de facilidad de despliegue: la versión v2 de Users, el puerto 8091 del
# gateway y su bloque en nginx.conf. No son de estos patrones, y el control las
# descuenta de lo que dice git.
deployment=$(($(compose_service users-service-v2) + $(grep -c '"8091:8091"' docker-compose.yaml) \
    + $(awk '/log_format deployment/ { on = 1 } on { print } on && /^    }/ { on = 0 }' src/ApiGateway/nginxconf/nginx.conf | count)))

# changed_in <archivo> <desde> <hasta>: como changed, pero solo los cambios entre
# esas líneas del archivo actual.
changed_in() {
    diff_base "$1" | awk -v from="$2" -v to="$3" '
        /^(\+\+\+|---)/ { next }
        /^@@/ { split($3, n, ","); line = substr(n[1], 2); next }
        /^\+/ { if (line >= from && line <= to) print substr($0, 2); line++; next }
        /^-/ { if (line >= from && line <= to) print substr($0, 2) }'
}

# El flag de la fecha de creación, en el método ListOrders, simula un cambio de
# otro equipo para la demo de Anti-corruption Layer: no es costo de un patrón.
controller=src/OrdersService/Controllers/OrdersController.cs
list_from=$(grep -n 'ListOrders()' "$controller" | cut -d: -f1)
list_to=$(awk -v from="$list_from" 'NR > from && /^    }$/ { print NR; exit }' "$controller")
order_dates=$(changed_in "$controller" "$list_from" "$list_to" | count)

config_existing=$(($(changed "$controller" src/OrdersService/Program.cs \
    src/ProductsService/Program.cs src/ProductsService/ProductCache.cs src/Common/Observability.cs | count) - order_dates))
bridge_existing=$(($(changed src/Common/Events.cs src/OrdersService/Models/OrderEvent.cs \
    src/OrdersService/Messaging/OrderDetailsProjector.cs | count) \
    + $(changed src/OrdersService/Messaging/OrderProcessingWorker.cs | grep -c 'OrderConfirmed')))
strangler_existing=$(changed src/ApiGateway/Dockerfile | grep -v 'deployment-routes' | count)

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
total "Anti-corruption Layer" "$(new_files src/PurchasesService/AntiCorruption)" "$(new_lines src/PurchasesService/AntiCorruption)" 0
total "External Configuration Store" 1 "$(new_lines src/Common/ExternalConfiguration.cs)" "$((config_existing + compose_consul))"
total "Messaging Bridge" "$(new_files src/MessagingBridge)" "$(new_lines src/MessagingBridge)" "$((bridge_existing + compose_bridge))"
total "Strangler Fig" 1 "$(new_lines src/ApiGateway/nginxconf/strangler-routes.conf)" "$((nginx_strangler + compose_strangler + strangler_existing))"
total "Backends for Frontends" "$(new_files src/BffWeb src/BffMobile)" "$(new_lines src/BffWeb src/BffMobile)" "$((nginx_bff + compose_bff))"

# Control: lo repartido tiene que sumar lo que dice git.
expected=$(($(changed docker-compose.yaml src/ApiGateway/nginxconf/nginx.conf | count) - deployment))
split=$((nginx_strangler + compose_consul + compose_bridge + compose_strangler + nginx_bff + compose_bff))
note "Control de docker-compose.yaml y nginx.conf: repartidas $split, fuera de los patrones $outside, según git $expected."

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
printf 'services:\n  orders-service:\n    environment:\n      ORDERS__MAXQUANTITY: "3"\n' > "$OVERRIDE"
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

