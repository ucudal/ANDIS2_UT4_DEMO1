#!/usr/bin/env bash
# Escalado a cero: KEDA cambia las réplicas del worker de Orders según el largo
# de la cola orders.process. Con la cola vacía no hay ninguna réplica encendida,
# y durante un pico hay hasta 6. Es lo que hacen los servicios serverless, como
# Azure Container Apps o AWS Lambda.
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

title "Escalado a cero con KEDA"

note "Aplicando el worker de k8s/energy en el clúster, con 3 réplicas fijas, como en la demo 35..."
energy_setup
wait_replicas 3
wait_rate
worker_line
idle=$(worker_watts)

step "Se aplica el ScaledObject de KEDA: scaledobject-queue.yaml"
kubectl apply -f k8s/energy/scaledobject-queue.yaml > /dev/null
wait_replicas 0
wait_no_power
annotate "Escalado a cero: 0 réplicas" energía
worker_line
note "La cola está vacía: KEDA bajó el worker a 0 réplicas. Sin réplicas no hay CPU"
note "reservada: 0 W, cuando las 3 réplicas fijas consumían $idle W todo el tiempo."

step "Se crean $ORDERS Orders a la vez"
annotate "Escalado a cero: pico de $ORDERS Orders" energía
started=$SECONDS
ids=$(burst "$ORDERS")
watch_worker 180 "$ids"
echo "  Procesadas en $((SECONDS - started))\""
note "KEDA vio la cola con mensajes y encendió el worker; Kubernetes sumó réplicas, una"
note "cada 5 mensajes, y llegó a $MAX_REPLICAS de un máximo de 6. Las primeras Orders"
note "esperaron a que arrancaran los pods: es el costo de no tener nada encendido."

step "La cola se vació: KEDA vuelve a apagar el worker"
note "Espera 30 segundos sin mensajes -cooldownPeriod- antes de apagarlo."
wait_replicas 0
wait_no_power
annotate "Escalado a cero: de nuevo 0 réplicas" energía
worker_line
elapsed=$((SECONDS - started))
joules=$(worker_joules "$elapsed")
echo "  Energía del worker desde el pico: $joules J en ${elapsed}\""
echo "  Las 3 réplicas fijas, ociosas, consumen eso en $(jq -n "$joules / $idle | round")\""
note "Durante el pico, el escalado a cero consume más que las réplicas fijas: llega a"
note "$MAX_REPLICAS réplicas, y cada pod nuevo usa mucha CPU al arrancar. Pero entre un pico"
note "y otro no consume nada. Conviene si el worker pasa ocioso más tiempo que ese;"
note "si los picos son más seguidos, conviene dejar réplicas encendidas."
note "Las réplicas se pueden apagar sin perder nada porque son stateless: las Orders"
note "están en MySQL y los mensajes, en RabbitMQ."
