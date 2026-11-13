#!/usr/bin/env bash
# Procesamiento diferido: el worker de Orders solo está encendido dentro de una
# ventana, que simula las horas de energía más limpia. Las Orders que llegan
# fuera de la ventana esperan en la cola orders.process hasta que se abre. La
# ventana se abre y se cierra con una clave del almacén de configuración,
# Energy/WindowOpen, que KEDA consulta a través de orders-service.
#
# Necesita el clúster, ./demos/k8s-up.sh, y Docker Compose levantado: el worker
# usa RabbitMQ, la base de Orders, Users y Products de Docker Compose, y la
# ventana está en Consul.
source "$(dirname "$0")/common.sh"
source k8s/common.sh

ORDERS=10

cleanup() {
    echo
    note "Borrando el worker del clúster y devolviendo el de Docker Compose a 3 réplicas..."
    energy_cleanup
}
trap cleanup EXIT

title "Procesamiento diferido con una ventana de KEDA"

note "Aplicando el worker de k8s/energy en el clúster, con el ScaledObject de scaledobject-window.yaml..."
energy_setup window

step "Fuera de la ventana, el worker está apagado"
window_set closed
wait_replicas 0
wait_no_power
annotate "Procesamiento diferido: ventana cerrada" energía
worker_line

step "Se crean $ORDERS Orders fuera de la ventana"
ids=$(burst "$ORDERS")
created=$SECONDS
sleep 5
worker_line
echo "  Orders Pending: $(pending "$ids") de $ORDERS"
note "Las Orders se aceptaron -202 Accepted- y esperan en la cola. Nadie las procesa"
note "todavía: no hay ninguna réplica encendida."

step "Se abre la ventana"
window_set open
annotate "Procesamiento diferido: se abre la ventana" energía
watch_worker 90 "$ids"
echo "  Orders Pending: $(pending "$ids") de $ORDERS. Esperaron $((SECONDS - created))\" desde que se crearon."
note "Dentro de la ventana, KEDA encendió 3 réplicas y vaciaron la cola."

step "Se cierra la ventana: KEDA apaga el worker"
window_set closed
wait_replicas 0
wait_no_power
annotate "Procesamiento diferido: ventana cerrada" energía
worker_line
note "El trabajo no se hizo menos: se hizo en otro momento. El costo es la demora en"
note "confirmar las Orders, y que el stock queda reservado mientras tanto."
