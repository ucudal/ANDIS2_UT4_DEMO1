#!/usr/bin/env bash
# Funciones comunes de los scripts de prueba. No se ejecuta directamente.

set -euo pipefail
cd "$(dirname "$0")/.."

GATEWAY=${GATEWAY:-http://localhost}
ORDERS_DIRECT=${ORDERS_DIRECT:-http://localhost:8000}
PRODUCTS_DIRECT=${PRODUCTS_DIRECT:-http://localhost:8001}

title() { printf '\n\033[1;34m== %s ==\033[0m\n' "$*"; }
step()  { printf '\n\033[1m> %s\033[0m\n' "$*"; }
note()  { printf '\033[2m  %s\033[0m\n' "$*"; }

# chaos <users|products|orders> <failureRate> <delayMs>
chaos() {
    printf '  chaos en %s: ' "$1"
    curl -s -X PUT "$GATEWAY/$1/chaos" -H 'Content-Type: application/json' \
        -d "{\"failureRate\": $2, \"delayMs\": $3}"
    echo
}

reset_chaos() {
    curl -s -X DELETE "$GATEWAY/users/chaos" > /dev/null
    curl -s -X DELETE "$GATEWAY/products/chaos" > /dev/null
    curl -s -X DELETE "$GATEWAY/orders/chaos" > /dev/null
}

# only_pattern <retry|circuitBreaker|bulkhead|throttling|ninguno>: deja prendido
# en Orders solo ese patrón de resiliencia, para que la demo muestre un patrón
# por vez. Con "ninguno", los apaga todos.
only_pattern() {
    local json="" p
    for p in retry circuitBreaker bulkhead throttling; do
        json+="\"$p\": $([ "$p" = "$1" ] && echo true || echo false), "
    done
    printf '  patrones de Orders: '
    curl -s -X PUT "$GATEWAY/orders/patterns" -H 'Content-Type: application/json' -d "{${json%, }}"
    echo
}

reset_patterns() {
    curl -s -X DELETE "$GATEWAY/orders/patterns" > /dev/null
}

# Cuántas solicitudes GET está atendiendo Products en este momento, según sus
# propias métricas, sin contar la que lee las métricas.
products_active() {
    curl -s "$PRODUCTS_DIRECT/metrics" \
        | grep '^http_server_active_requests{' | grep 'http_request_method="GET"' \
        | awk '{ total += $NF } END { print total - 1 }'
}

# Cuántas consultas de detalle está atendiendo Orders en este momento, según sus
# propias métricas: las que tienen lugar en el Throttling, que las cuenta
# aunque el patrón esté apagado.
orders_in_progress() {
    curl -s "$ORDERS_DIRECT/metrics" \
        | grep '^aspnetcore_rate_limiting_active_request_leases{' \
        | awk '{ total += $NF } END { print total + 0 }'
}

# Cuántas consultas de un Product recibió Products desde que arrancó, sean
# respuestas buenas o fallas inyectadas, según sus propias métricas. La
# diferencia entre dos lecturas dice cuántas veces lo llamó Orders entre medio.
products_calls() {
    curl -s "$PRODUCTS_DIRECT/metrics"         | grep '^http_server_request_duration_seconds_count{'         | grep 'http_request_method="GET"' | grep 'http_route="products/{productId}"'         | awk '{ total += $NF } END { print total + 0 }'
}

# table [filtro]: muestra una respuesta JSON como tabla, igual que la función
# table de los archivos de commands/. El filtro de jq opcional elige una parte
# de la respuesta. Las celdas vacías se muestran con un guion, porque column
# junta los separadores seguidos y correría las columnas. Si falta jq o column,
# o la respuesta no es JSON, la muestra tal cual.
table() {
    local json
    json=$(cat)
    if command -v jq > /dev/null && command -v column > /dev/null \
        && jq -e "${1:-.}" <<< "$json" > /dev/null 2>&1; then
        jq -r "${1:-.}"' | if type == "array"
            then (.[0] | keys_unsorted) as $k | ($k | @tsv), (.[] | [.[$k[]] | tostring | if . == "" then "-" else . end] | @tsv)
            else to_entries[] | [.key, (.value | tostring)] | @tsv end' <<< "$json" \
        | column -t -s $'\t'
    else
        echo "$json"
    fi | sed 's/^/  /'
}

# Detalle de las Orders del User 1 como tabla, con código HTTP y tiempo de
# respuesta.
details() {
    local out
    out=$(curl -s -w '\n%{http_code} %{time_total}' "$GATEWAY/orders/user/1/details")
    table <<< "${out%$'\n'*}"
    read -r code time <<< "${out##*$'\n'}"
    printf '  [HTTP %s, %s s]\n\n' "$code" "$time"
}

# concurrent_details <n> [orden]: lanza n consultas de detalle simultáneas y
# devuelve un JSON con una fila por consulta, ordenadas por tiempo de respuesta
# o, si orden es "lanzamiento", en el orden en que se lanzaron: los
# segundos, el código HTTP, el encabezado Retry-After, el nombre del cliente y
# el nombre del producto de cada Order. Las respuestas sin cuerpo -por ejemplo,
# un 503- dejan vacías las columnas de datos, y la columna Retry-After solo
# aparece si alguna respuesta trae ese encabezado. Cada respuesta va a su propio archivo: si las n escribieran
# a la vez en la consola, sus salidas podrían mezclarse.
concurrent_details() {
    local dir
    dir=$(mktemp -d)
    (cd "$dir" && seq "$1" | xargs -P "$1" -I{} curl -s -o {}.body -w '{} %{http_code} %{time_total} %header{retry-after}\n' \
        "$GATEWAY/orders/user/1/details" > times)
    local key=3
    [ "${2:-}" = lanzamiento ] && key=1
    sort -k"$key" -n "$dir/times" | while read -r i code t retry_after; do
        jq -R -s -c --arg t "$t" --arg code "$code" --arg ra "$retry_after" '(try fromjson catch null) as $o
            | {segundos: (($t | tonumber) * 10 | round / 10), HTTP: ($code | tonumber), "Retry-After": $ra}
            + if ($o | type) == "array"
              then {cliente: $o[0].customer_name}
                + ($o | to_entries | map({key: "producto \(.key + 1)", value: .value.product_name}) | from_entries)
              else {} end' "$dir/$i.body"
    done | jq -s '. as $rows | (max_by(length) | keys_unsorted
            | map(select(. as $key | any($rows[]; (.[$key] // "") != "")))) as $k
        | map(. as $r | reduce $k[] as $key ({}; .[$key] = ($r[$key] // "")))'
    rm -rf "$dir"
}

# Hace algunas rondas de consultas simultáneas antes de una prueba de
# concurrencia. Así los servicios ya tienen abiertas las conexiones que la
# prueba necesita; si no, las primeras solicitudes demoran más y eso
# distorsionaría el resultado.
warm_up() {
    for i in 1 2 3; do
        seq 15 | xargs -P 15 -I{} curl -s -o /dev/null "$GATEWAY/orders/user/1/details"
    done
}

# Funciones de los demos de la 09 en adelante. Usan jq.

# create_order <userId> <productId> [cantidad]: crea una Order directamente en
# orders-service, sin pasar por el límite de POST del gateway, y devuelve su id.
create_order() {
    curl -s -X POST "$ORDERS_DIRECT/orders/" -H 'Content-Type: application/json' \
        -d "{\"userId\": $1, \"productId\": \"$2\", \"quantity\": ${3:-1}}" | jq -r .id
}

# wait_processed <ids>: espera a que un worker confirme o rechace cada Order.
wait_processed() {
    for id in $1; do
        until curl -s "$ORDERS_DIRECT/orders/$id" | grep -qv '"status":"Pending"'; do sleep 1; done
    done
}

# orders_sql <consulta>: ejecuta una consulta en la base de Orders.
orders_sql() {
    docker compose exec -T orders-db mysql --default-character-set=utf8mb4 -u ordersusr -pPa55w0rd ordersdb \
        -e "$1" 2> /dev/null | column -t -s $'\t' | sed 's/^/  /'
}

# queue_length <cola>: mensajes en una cola de RabbitMQ, esperando o en proceso.
# queue_consumers <cola>: consumidores conectados a la cola. Se usa rabbitmqctl
# y no la API de la consola, que actualiza sus números cada 5 segundos.
queue_info() {
    docker compose exec -T rabbitmq rabbitmqctl list_queues -q --no-table-headers name messages consumers \
        | awk -v q="$1" -v col="$2" '$1 == q { print $col }'
}
queue_length() { queue_info "$1" 2; }
queue_consumers() { queue_info "$1" 3; }

# update_product <id> <nombre> <precio>
update_product() {
    curl -s -X PUT "$GATEWAY/products/$1" -H 'Content-Type: application/json' \
        -d "{\"name\": \"$2\", \"price\": $3}" > /dev/null
}

# update_user <id> <nombre> <email>: operación SOAP UpdateUser.
update_user() {
    curl -s -X POST "$GATEWAY/users/UserService.svc" -H 'Content-Type: text/xml; charset=utf-8' \
        -H 'SOAPAction: "http://ucu.edu.uy/andis/users/IUserService/UpdateUser"' \
        -d "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body><UpdateUser xmlns=\"http://ucu.edu.uy/andis/users\"><id>$1</id><name>$2</name><email>$3</email></UpdateUser></soap:Body></soap:Envelope>" > /dev/null
}

# Al terminar, deja los servicios sin fallas simuladas y con todos los patrones
# prendidos.
trap 'reset_chaos; reset_patterns' EXIT
