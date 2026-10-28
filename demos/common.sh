#!/usr/bin/env bash
# Funciones comunes de los scripts de prueba. No se ejecuta directamente.

set -euo pipefail
cd "$(dirname "$0")/.."

GATEWAY=${GATEWAY:-http://localhost}
ORDERS_DIRECT=${ORDERS_DIRECT:-http://localhost:8000}

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

# table [filtro]: muestra una respuesta JSON como tabla, igual que la función
# table de commands.azcli. El filtro de jq opcional elige una parte de la
# respuesta. Las celdas vacías se muestran con un guion, porque column junta
# los separadores seguidos y correría las columnas. Si falta jq o column, o la
# respuesta no es JSON, la muestra tal cual.
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

# Al terminar, deja los servicios sin fallas simuladas.
trap reset_chaos EXIT
