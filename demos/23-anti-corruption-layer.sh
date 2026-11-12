#!/usr/bin/env bash
# Anti-corruption Layer: Purchases muestra, para un Product, todas sus compras con
# su propio modelo. Los datos vienen de Orders y de Users, que tienen otro modelo
# y otro protocolo. La capa anticorrupción los traduce, y Purchases no conoce
# ninguno de los dos.
#
# Bajo carga y en Grafana: ./demos/load-modifiability.sh acl
source "$(dirname "$0")/common.sh"

cleanup() {
    curl -s -X DELETE "$CONSUL/v1/kv/andis/Features/OrderDates" > /dev/null
}
trap cleanup EXIT

PRODUCT=3
SOAP="<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body><GetUser xmlns=\"http://ucu.edu.uy/andis/users\"><id>$ALICE</id></GetUser></soap:Body></soap:Envelope>"

title "Anti-corruption Layer"

step "El modelo de Orders: user_id, product_id y el estado en inglés"
curl -s "$GATEWAY/orders/" | table "[.[] | select(.product_id == $PRODUCT)]"

step "El modelo de Users: SOAP"
curl -s -X POST "$GATEWAY/users/UserService.svc" -H 'Content-Type: text/xml; charset=utf-8' \
    -H 'SOAPAction: "http://ucu.edu.uy/andis/users/IUserService/GetUser"' -d "$SOAP" \
    | sed 's/></>\n</g' | grep -E '<(Name|Email|Id)>' | sed 's/^ */  /'

step "El modelo de Purchases: comprador, unidades y si está confirmada"
curl -s "$GATEWAY/purchases/product/$PRODUCT" | table
note "Purchases no ve el id del User, ni el del Product, ni el estado de Orders: la"
note "capa los tradujo. Una Order rechazada no es una compra, así que no aparece."

step "Orders cambia su modelo: agrega la fecha de creación de cada Order"
before=$(curl -s "$GATEWAY/purchases/product/$PRODUCT")
config_set Features/OrderDates true
curl -s "$GATEWAY/orders/" | table "[.[] | select(.product_id == $PRODUCT)]"
after=$(curl -s "$GATEWAY/purchases/product/$PRODUCT")
[ "$before" = "$after" ] && echo "  La respuesta de Purchases es idéntica a la de antes del cambio."
note "La capa lee solo los campos que usa: un campo nuevo en Orders no la cambia."
config_delete Features/OrderDates

step "Una Order nueva de bob aparece como compra"
id=$(create_order "$BOB" "$PRODUCT")
echo "  Se crea la Order $id"
wait_processed "$id"
curl -s "$GATEWAY/purchases/product/$PRODUCT" | table "[.[] | select(.number == $id)]"

step "Solo la capa conoce los modelos de Orders y de Users"
grep -rlE 'user_id|product_id|"Rejected"|Envelope|SOAPAction' src/PurchasesService --include='*.cs' \
    | sed 's/^/  /'
note "Si Orders o Users cambian, se cambia la capa, y el resto de Purchases no se entera."
