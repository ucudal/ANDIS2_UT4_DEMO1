#!/usr/bin/env bash
# Gateway Offloading: el gateway se hace cargo de funciones que de otro modo
# tendría que implementar cada servicio. En la entrada HTTPS termina TLS, valida
# los tokens con Keycloak y comprime las respuestas; los servicios no tienen
# certificados ni código de autenticación.
source "$(dirname "$0")/common.sh"

load_gateway_cert
carol=$(token carol)

title "Gateway Offloading"

step "TLS: los servicios solo hablan HTTP"
call "https://localhost:8000/orders/"
call "http://localhost:8000/orders/" -o /dev/null
note "Orders no tiene certificado ni configuración de TLS."

step "TLS: el gateway lo termina con su certificado"
curl -sv --cacert "$GATEWAY_CERT" -o /dev/null "$HTTPS_GATEWAY/health/orders" 2>&1 \
    | grep -E 'SSL connection|subject:|issuer:|expire date:' | sed 's/^[* ]*/  /'
note "Renovar el certificado es cambiarlo en un solo lugar: el gateway."

step "Autenticación: el gateway valida el token antes de reenviar la solicitud"
sleep 1
since=$(date +%s)
call "$HTTPS_GATEWAY/orders/user/1/details/view"
call -H "Authorization: Bearer $carol" "$HTTPS_GATEWAY/orders/user/1/details/view" -o /dev/null
sleep 1
echo "  Logs del gateway:"
docker compose logs --no-log-prefix --since "$since" api-gateway | grep '\[HTTPS\]' | sed 's/^/    /'
note "Sin token, el gateway responde 401 y la solicitud no llega a Orders: servicio=- en el log."
note "Con token, el gateway le pasa a Orders el usuario en encabezados: Orders no valida tokens."

step "Compresión: la lista de Orders, por HTTP y por HTTPS"
printf '  %-28s %s bytes\n' "HTTP, sin comprimir" \
    "$(curl -s --compressed -o /dev/null -w '%{size_download}' "$GATEWAY/orders/")"
printf '  %-28s %s bytes\n' "HTTPS, comprimida con gzip" \
    "$(curl -s --compressed --cacert "$GATEWAY_CERT" -o /dev/null -w '%{size_download}' \
        -H "Authorization: Bearer $carol" "$HTTPS_GATEWAY/orders/")"
note "Orders responde igual en los dos casos: comprime el gateway, si el cliente acepta gzip."
