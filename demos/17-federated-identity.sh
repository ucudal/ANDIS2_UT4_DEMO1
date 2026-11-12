#!/usr/bin/env bash
# Federated Identity: los usuarios inician sesión en Keycloak, el proveedor de
# identidad, que emite un token firmado con sus claims. La aplicación no guarda
# contraseñas: confía en la firma de Keycloak y usa los claims para decidir qué
# puede hacer cada usuario.
# Este script inicia sesión con usuario y contraseña; el paso a paso de
# commands/04-security.azcli lo hace desde el navegador. Como las demás demos,
# crea Orders solo para bob: las de alice son la base de las demos
# de disponibilidad.
source "$(dirname "$0")/common.sh"

load_gateway_cert
JSON='Content-Type: application/json'

title "Federated Identity"

step "Sin identidad: por HTTP, cualquiera crea una Order a nombre de bob"
call -X POST "$GATEWAY/orders/" -H "$JSON" -d "{\"userId\": \"$BOB\", \"productId\": 1, \"quantity\": 1}"
note "Orders cree lo que dice el cuerpo: no sabe quién hizo la solicitud."

step "bob inicia sesión en Keycloak y recibe un token"
bob=$(token bob)
claims "$bob" | jq '{iss, preferred_username, user_id, roles: (.realm_access.roles | join(",")), exp: (.exp | todate)}' | table
note "Lo firmó Keycloak. La aplicación no conoce la contraseña de bob: verifica la firma y usa los claims."

step "Por HTTPS, con el token de bob, la Order es de bob"
call -X POST "$HTTPS_GATEWAY/orders/" -H "Authorization: Bearer $bob" -H "$JSON" \
    -d '{"productId": 1, "quantity": 1}'
note "Sin userId en el cuerpo: Orders lo toma del token."
call -X POST "$HTTPS_GATEWAY/orders/" -H "Authorization: Bearer $bob" -H "$JSON" \
    -d "{\"userId\": \"$ALICE\", \"productId\": 1, \"quantity\": 1}"

step "bob consulta sus Orders y las de alice"
call -H "Authorization: Bearer $bob" "$HTTPS_GATEWAY/orders/user/$BOB/details/view"
call -H "Authorization: Bearer $bob" "$HTTPS_GATEWAY/orders/user/$ALICE/details/view"

step "alice inicia sesión: la misma consulta responde según quién la hace"
alice=$(token alice)
call -H "Authorization: Bearer $alice" "$HTTPS_GATEWAY/orders/user/$ALICE/details/view"
call -H "Authorization: Bearer $alice" "$HTTPS_GATEWAY/orders/user/$BOB/details/view"

step "carol tiene el rol admin: consulta las Orders de cualquier User, pero no es cliente"
carol=$(token carol)
call -H "Authorization: Bearer $carol" "$HTTPS_GATEWAY/orders/user/$BOB/details/view"
call -X POST "$HTTPS_GATEWAY/orders/" -H "Authorization: Bearer $carol" -H "$JSON" \
    -d "{\"userId\": \"$BOB\", \"productId\": 1, \"quantity\": 1}"

step "Sin token, o con un token alterado, no se entra"
call "$HTTPS_GATEWAY/orders/user/$ALICE/details/view"
note "bob cambia el user_id de su token por el de alice, sin poder volver a firmarlo:"
call -H "Authorization: Bearer $(tamper "$bob" ".user_id = \"$ALICE\"")" "$HTTPS_GATEWAY/orders/user/$ALICE/details/view"
note "La firma ya no corresponde a los claims: el gateway responde 401."

step "Decisiones de Orders según los claims"
sleep 1
docker compose logs --no-log-prefix --since 1m orders-service | grep '\[FEDERATED IDENTITY\]' | sed 's/^ */  /' | tail -5
