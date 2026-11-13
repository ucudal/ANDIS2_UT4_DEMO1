#!/usr/bin/env bash
# Power Monitor: cuánta energía consume el worker de Orders, ocioso y
# procesando un pico. El worker corre en el clúster con 3 réplicas fijas, como
# orders-worker en Docker Compose, y Prometheus estima la potencia de cada pod
# con la CPU que usa y la que reserva -ver k8s/base/prometheus.yaml-.
#
# Necesita el clúster, ./demos/k8s-up.sh, y Docker Compose levantado: el worker
# usa RabbitMQ, la base de Orders, Users y Products de Docker Compose.
source "$(dirname "$0")/common.sh"
source k8s/common.sh

ORDERS=30

cleanup() {
    echo
    note "Borrando el worker del clúster y devolviendo el de Docker Compose a 3 réplicas..."
    energy_cleanup
}
trap cleanup EXIT

title "Power Monitor: la energía del worker"

note "Aplicando el worker de k8s/energy en el clúster, con 3 réplicas..."
energy_setup
wait_replicas 3
echo "  Réplicas del worker: $(worker_replicas). Consumidores de orders.process: $(queue_consumers orders.process)"
annotate "Power Monitor: 3 réplicas ociosas" energía

step "El worker sin trabajo"
wait_rate
power_table
idle=$(worker_watts)
reserved=$(printf '%.2f' "$(promql 'sum(pod:cpu_reserved:vcpu{pod=~"orders-worker-.*"}) * 0.78')")
note "Ocioso, el worker consume unos $idle W. De esos, $reserved W son de la CPU reservada:"
note "0.25 vCPU por réplica, que la máquina tiene que tener encendidas aunque nadie las use."

step "Se crean $ORDERS Orders a la vez"
annotate "Power Monitor: pico de $ORDERS Orders" energía
started=$SECONDS
ids=$(burst "$ORDERS")
watch_worker 120 "$ids"
echo "  Procesadas en $((SECONDS - started))\""
power_table
note "La potencia sube poco: procesar una Order es casi todo esperar a Users y a"
note "Products. Lo que más pesa sigue siendo la CPU reservada."

step "El worker vuelve a estar ocioso"
annotate "Power Monitor: de nuevo ocioso" energía
sleep 30
worker_line
note "Las 3 réplicas siguen consumiendo $idle W, haya o no Orders. Medirlo es el primer"
note "paso: la demo 36 apaga las réplicas cuando la cola está vacía."
