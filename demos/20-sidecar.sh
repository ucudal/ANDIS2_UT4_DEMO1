#!/usr/bin/env bash
# Sidecar: un proxy Envoy corre junto a products-service, en su mismo espacio de
# red, y le agrega autenticación y autorización sin cambiar su código: cualquiera
# puede leer los Products, pero para cambiarlos hace falta el rol admin. La
# entrada HTTPS del gateway le envía /products/ al sidecar; la HTTP,
# directamente a Products.
source "$(dirname "$0")/common.sh"

PRODUCT=3

# Al terminar, el Product recupera su nombre.
cleanup() {
    update_product "$PRODUCT" "Zapatos" 3500.75
    reset_chaos
}
trap cleanup EXIT

load_gateway_cert
alice=$(token alice)
carol=$(token carol)
JSON='Content-Type: application/json'

title "Sidecar"

sleep 1
since=$(date +%s)

step "Sin sidecar: por HTTP, cualquiera cambia el nombre del Product $PRODUCT a \"Zapatillas\""
call -X PUT -H "$JSON" -d '{"name": "Zapatillas", "price": 3500.75}' "$GATEWAY/products/$PRODUCT"

step "Con sidecar: por HTTPS, alice -rol customer- lee el Product, pero no lo puede cambiar"
call -H "Authorization: Bearer $alice" "$HTTPS_GATEWAY/products/$PRODUCT"
call -X PUT -H "Authorization: Bearer $alice" -H "$JSON" -d '{"name": "Zapatos", "price": 3500.75}' \
    "$HTTPS_GATEWAY/products/$PRODUCT"
note "El 403 lo responde Envoy -RBAC: access denied-, sin llegar a Products."

step "carol -rol admin- le devuelve el nombre \"Zapatos\""
call -X PUT -H "Authorization: Bearer $carol" -H "$JSON" -d '{"name": "Zapatos", "price": 3500.75}' \
    "$HTTPS_GATEWAY/products/$PRODUCT"

step "Desde adentro de la red, el gateway no protege: un contenedor llama a Products"
echo "  Directamente a products-service:8000, sin sidecar:"
call_inside -X PUT -H "$JSON" -d '{"name": "Zapatos", "price": 3500.75}' \
    "http://products-service:8000/products/$PRODUCT"
echo "  A través del sidecar, products-service:9000, sin token y con un token alterado:"
call_inside -X PUT -H "$JSON" -d '{"name": "Zapatos", "price": 3500.75}' \
    "http://products-service:9000/products/$PRODUCT"
call_inside -X PUT -H "$JSON" -d '{"name": "Zapatos", "price": 3500.75}' \
    -H "Authorization: Bearer $(tamper "$alice" '.realm_access.roles = ["admin"]')" \
    "http://products-service:9000/products/$PRODUCT"
note "alice se agregó el rol admin en el token, sin poder volver a firmarlo: Envoy verifica la firma."
note "En producción, Products escucharía solo en 127.0.0.1 y el sidecar sería su única entrada."

step "Registro de accesos del sidecar"
sleep 1
docker compose logs --no-log-prefix --since "$since" products-sidecar | grep '\[SIDECAR\]' | sed 's/^/  /'
note "La lectura de alice y el cambio de carol llegaron a Products -via_upstream-; los demás, no."
note "Por HTTP y por products-service:8000, el sidecar no registra nada: no pasan por él."
