#!/usr/bin/env bash
# Valet Key: Products no recibe las imágenes de los Products. Le entrega al
# cliente una URL firmada -una SAS de Azure Storage- que permite subir una sola
# imagen, por un tiempo corto, directamente al almacenamiento. Se compara con la
# forma sin el patrón, en la que la imagen pasa por el gateway y por Products.
source "$(dirname "$0")/common.sh"

PRODUCT=60f7c2b5e1d3c2a1b8e4d124
IMAGE=$(mktemp)

cleanup() {
    rm -f "$IMAGE"
    reset_chaos
}
trap cleanup EXIT

load_gateway_cert
carol=$(token carol)

# upload <argumentos de curl>: sube la imagen y muestra el código HTTP, los
# bytes enviados y cuánto tardó.
upload() {
    curl -s --cacert "$GATEWAY_CERT" -o /dev/null -X PUT --data-binary @"$IMAGE" \
        -w '  HTTP %{http_code}, %{size_upload} bytes enviados en %{time_total} s\n' "$@"
}

# storage <argumentos de curl>: una solicitud a Azurite; muestra el código HTTP
# y el código de error de Azure Storage, si lo hay.
storage() {
    local out
    out=$(curl -s -w '\n%{http_code}' "$@")
    printf '  HTTP %s  %s\n' "${out##*$'\n'}" "$(grep -o '<Code>[^<]*' <<< "$out" | sed 's/<Code>//')"
}

# Una "imagen" de 5 MB: bytes al azar, para que no se pueda comprimir.
head -c 5000000 /dev/urandom > "$IMAGE"

title "Valet Key"

sleep 1
since=$(date +%s)

step "Sin Valet Key: la imagen de 5 MB pasa por el gateway y por Products"
upload -H 'Content-Type: image/jpeg' "$GATEWAY/products/$PRODUCT/image"
note "Por HTTP, el gateway acepta hasta 20 MB en esta ruta para que la imagen pueda pasar."
echo "  Por HTTPS, el Gatekeeper no la deja pasar:"
upload -H "Authorization: Bearer $carol" -H 'Content-Type: image/jpeg' "$HTTPS_GATEWAY/products/$PRODUCT/image"

step "Con Valet Key: carol pide a Products una URL para subir la imagen"
response=$(curl -s --cacert "$GATEWAY_CERT" -X POST -H "Authorization: Bearer $carol" \
    "$HTTPS_GATEWAY/products/$PRODUCT/image/upload-url")
url=$(jq -r .uploadUrl <<< "$response")
jq '{expiresOn, method}' <<< "$response" | table
echo "  Parámetros de la firma:"
tr '?&' '\n\n' <<< "$url" | tail -n +2 | grep -E '^(sr|sp|se)=' \
    | sed -e 's/^sr=b/  sr=b   un solo blob, el de este Product/' \
          -e 's/^sp=cw/  sp=cw  crear y escribir, no leer/' \
          -e 's/%3A/:/g' -e 's/^se=\(.*\)/  se=\1  vencimiento/'

step "carol sube la imagen directamente a Azurite, sin pasar por el gateway ni por Products"
upload -H 'x-ms-blob-type: BlockBlob' -H 'Content-Type: image/jpeg' "$url"

step "La clave sirve solo para eso"
echo "  Leer la imagen con la clave de escritura:"
storage "$url"
echo "  Escribir la imagen de otro Product con la misma clave:"
storage -X PUT -H 'x-ms-blob-type: BlockBlob' --data-binary 'x' "${url/$PRODUCT/60f7c2b5e1d3c2a1b8e4d125}"
echo "  La misma URL sin la firma:"
storage "${url%%\?*}"

step "Una clave que vence a los 5 segundos"
short=$(curl -s --cacert "$GATEWAY_CERT" -X POST -H "Authorization: Bearer $carol" \
    "$HTTPS_GATEWAY/products/$PRODUCT/image/upload-url?seconds=5" | jq -r .uploadUrl)
echo "  Enseguida, funciona:"
upload -H 'x-ms-blob-type: BlockBlob' -H 'Content-Type: image/jpeg' "$short"
note "Se esperan 7 segundos..."
sleep 7
echo "  Vencida, ya no:"
storage -X PUT -H 'x-ms-blob-type: BlockBlob' --data-binary 'x' "$short"
note "Si alguien obtiene la URL, puede usarla solo por poco tiempo, y solo para esa imagen."

step "Para bajar la imagen, Products redirige a una URL de lectura"
curl -s --cacert "$GATEWAY_CERT" -o /dev/null -w '  HTTP %{http_code}  Location: %{redirect_url}\n' \
    -H "Authorization: Bearer $carol" "$HTTPS_GATEWAY/products/$PRODUCT/image" | sed -E 's/sig=[^&]*/sig=.../'
curl -s -L --cacert "$GATEWAY_CERT" -o /dev/null -w '  Con -L, curl la baja de Azurite: HTTP %{http_code}, %{size_download} bytes\n' \
    -H "Authorization: Bearer $carol" "$HTTPS_GATEWAY/products/$PRODUCT/image"

step "Lo que hizo Products"
sleep 1
docker compose logs --no-log-prefix --since "$since" products-service | grep '\[VALET KEY\]' | sed 's/^ */  /'
note "Sin Valet Key, Products recibió los 5 MB; con Valet Key, solo firmó claves."
