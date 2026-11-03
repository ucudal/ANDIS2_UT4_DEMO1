#!/usr/bin/env bash
# Circuit Breaker: si Products falla de forma sostenida, Orders deja de
# llamarlo durante un tiempo y responde de inmediato con una respuesta degradada.
# Se hacen las mismas consultas con el Circuit Breaker apagado y prendido, para
# comparar cuántas llamadas le llegan a Products.
# Bajo carga y en Grafana: docker compose run --rm k6 run /scripts/circuit-breaker.js
source "$(dirname "$0")/common.sh"

# consult <n>: consulta n veces el detalle y dice cuántas llamadas le llegaron
# a Products mientras tanto.
consult() {
    local before after
    before=$(products_calls)
    for i in $(seq "$1"); do details; done
    sleep 1
    after=$(products_calls)
    note "Products recibió $((after - before)) llamadas durante estas $1 consultas."
}

title "Circuit Breaker"

step "Products falla en el 100 % de las solicitudes"
chaos products 1 0

step "Se apagan todos los patrones, también el Circuit Breaker"
only_pattern ninguno

step "Se consulta 6 veces el detalle de Orders"
consult 6

step "Se deja prendido solo el Circuit Breaker: sin Retry, cada falla se cuenta una vez"
only_pattern circuitBreaker

step "Se consulta otras 6 veces el detalle de Orders"
consult 6

step "Products se recupera, pero el circuito sigue abierto: Orders no lo llama y no se entera"
reset_chaos
details

step "Se esperan 16 segundos a que el circuito pase a semiabierto..."
sleep 16
details
