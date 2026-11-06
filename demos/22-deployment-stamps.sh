#!/usr/bin/env bash
# Deployment Stamps: una segunda copia completa de la aplicación, con sus propios
# servicios, bases de datos, broker, caché y almacenamiento, a partir de la misma
# plantilla. El gateway elige la copia según el encabezado X-Region. Los datos y
# las fallas de una copia no llegan a la otra.
#
# Bajo carga y en Grafana: ./demos/load-modifiability.sh stamps
source "$(dirname "$0")/common.sh"

STAMPS="-f docker-compose.yaml -f docker-compose.stamps.yaml"
PRODUCT=60f7c2b5e1d3c2a1b8e4d125

# Al terminar se elimina la copia, que ocupa memoria, con sus datos.
cleanup() {
    echo
    note "Eliminando la segunda copia..."
    curl -s -X DELETE -H 'X-Region: br' "$GATEWAY/products/chaos" > /dev/null || true
    # shellcheck disable=SC2046
    docker compose $STAMPS rm -sfv $(docker compose $STAMPS config --services | grep '^stamp-b-') > /dev/null 2>&1
    reset_chaos
}
trap cleanup EXIT

# request <región> <argumentos de curl>: muestra qué copia atendió y la respuesta.
request() {
    local region=$1 out
    shift
    out=$(curl -s -D - -H "X-Region: $region" "$@" | tr -d '\r')
    printf '  X-Region: %-3s %s %s  %s\n' "$region" \
        "$(head -1 <<< "$out" | awk '{ print "HTTP " $2 }')" \
        "$(grep -i '^x-stamp:' <<< "$out" | awk '{ print "-> " $2 }')" \
        "$(tail -1 <<< "$out" | head -c 90)"
}

# orders_count <región>: cuántas Orders tiene la copia de esa región.
orders_count() {
    curl -s -H "X-Region: $1" "$GATEWAY/orders/" | jq length
}

title "Deployment Stamps"

step "Se despliega la segunda copia con la misma plantilla -puede tardar hasta un minuto-"
docker compose $STAMPS up -d --wait > /dev/null 2>&1
docker compose $STAMPS ps --format '{{.Service}}\t{{.Image}}' | grep -E '^stamp-b-.*-service' | column -t | sed 's/^/  /'
note "Las mismas imágenes que la primera copia; solo cambian los nombres de sus piezas."

step "La misma solicitud; el gateway elige la copia por la región"
request uy "$GATEWAY/products/60f7c2b5e1d3c2a1b8e4d123"
request br "$GATEWAY/products/60f7c2b5e1d3c2a1b8e4d123"
note "Un cliente sin el encabezado va a la primera copia, como en las demás demos."

step "Los datos de una copia no están en la otra"
echo "  Orders antes: stamp-a $(orders_count uy), stamp-b $(orders_count br)"
id=$(curl -s -X POST -H 'X-Region: br' -H 'Content-Type: application/json' \
    -d '{"userId": 2, "productId": "60f7c2b5e1d3c2a1b8e4d124", "quantity": 1}' "$GATEWAY/orders/" | jq -r .id)
echo "  Se crea la Order $id en la región br"
until [ "$(curl -s -H 'X-Region: br' "$GATEWAY/orders/$id" | jq -r .status)" != Pending ]; do sleep 1; done
echo "  Orders después: stamp-a $(orders_count uy), stamp-b $(orders_count br)"
request br "$GATEWAY/orders/$id"
note "La procesó el worker de la segunda copia, con su propio broker y su propia base."

step "Las imágenes tampoco: cada copia tiene su almacenamiento"
url=$(curl -s -X POST -H 'X-Region: br' "$GATEWAY/products/$PRODUCT/image/upload-url" | jq -r .uploadUrl)
echo "  URL firmada de la región br: ${url%%\?*}"
printf 'imagen de la region br' | curl -s -o /dev/null -X PUT -H 'x-ms-blob-type: BlockBlob' \
    -H 'Content-Type: image/jpeg' --data-binary @- "$url"
for region in br uy; do
    printf '  imagen del Product en la región %s: HTTP %s\n' "$region" \
        "$(curl -s -o /dev/null -w '%{http_code}' -H "X-Region: $region" "$GATEWAY/products/$PRODUCT/image")"
done
note "302 Found redirige al almacenamiento de la copia; 404 Not Found es que esa copia no tiene la imagen."

step "Una falla en una copia no llega a la otra"
annotate "Falla en Products de la región br"
curl -s -o /dev/null -X PUT -H 'X-Region: br' -H 'Content-Type: application/json' \
    -d '{"failureRate": 1, "delayMs": 0}' "$GATEWAY/products/chaos"
echo "  Products de la región br falla en todas las solicitudes:"
request br "$GATEWAY/products/60f7c2b5e1d3c2a1b8e4d123"
request uy "$GATEWAY/products/60f7c2b5e1d3c2a1b8e4d123"
note "El radio de explosión de una falla, o de un mal despliegue, es una copia."

step "Lo que queda fuera de las copias es global"
note "El gateway, Keycloak y Consul son uno solo: si el gateway se cae, se caen todas."
