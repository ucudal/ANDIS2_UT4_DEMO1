#!/usr/bin/env bash
# Las demos de eficiencia energética bajo carga y en Grafana. Prepara el worker
# del clúster en una configuración y lanza un escenario de k6 con tres picos de
# Orders, separados por períodos ociosos -ver k6/energy.js-. Cada corrida dura
# 10 minutos, aunque k6 termine antes: el script espera hasta completarlos. Con
# el rango del dashboard en 10 minutos, la energía de las tres configuraciones
# se puede comparar.
#
#   docker compose --profile observability up -d --wait
#   ./demos/load-energy.sh <fixed|queue|window>
#
# - fixed: 3 réplicas fijas, como en la demo 35.
# - queue: KEDA según la cola, como en la demo 36.
# - window: KEDA según una ventana, como en la demo 37. El script la abre un
#   minuto de cada dos.
#
# Necesita el clúster, ./demos/k8s-up.sh. Los paneles están en la fila
# "Eficiencia energética". Cada pico, cada cambio en las réplicas del worker y,
# con window, cada apertura y cierre de la ventana quedan marcados en el
# dashboard con la etiqueta energía.
source "$(dirname "$0")/common.sh"
source k8s/common.sh

DURATION=600

case "${1:-}" in
    fixed) scaledobject="" ;;
    queue|window) scaledobject=$1 ;;
    *) echo "Uso: $0 <fixed|queue|window>" >&2; exit 2 ;;
esac

# mark <texto>: marca el momento en Grafana y lo muestra en la terminal.
mark() {
    annotate "$1" energía
    note "$(date +%T)  $1"
}

# watch_events <configuración>: marca en Grafana cada cambio en las réplicas
# del worker. Con window, además abre la ventana un minuto de cada dos desde que
# empieza la corrida, y marca cada apertura y cada cierre: los tres picos de
# k6 caen dentro de la ventana. Corre en segundo plano durante toda la corrida.
watch_events() {
    local last="" replicas window="" now
    while true; do
        replicas=$(worker_replicas)
        if [ -n "$last" ] && [ "$replicas" != "$last" ]; then
            mark "Réplicas del worker: de $last a $replicas"
        fi
        last=$replicas
        if [ "$1" = window ]; then
            if [ $(((SECONDS - started) / 60 % 2)) = 0 ]; then now=open; else now=closed; fi
            if [ "$now" != "$window" ]; then
                window_set "$now" > /dev/null
                if [ "$now" = open ]; then mark "Ventana abierta"; else mark "Ventana cerrada"; fi
            fi
            window=$now
        fi
        sleep 2
    done
}

WATCHER_PID=""

cleanup() {
    [ -n "$WATCHER_PID" ] && kill "$WATCHER_PID" 2> /dev/null
    echo
    note "Borrando el worker del clúster y devolviendo el de Docker Compose a 3 réplicas..."
    energy_cleanup
}
trap cleanup EXIT

title "Eficiencia energética bajo carga: $1"

note "Preparando el worker del clúster..."
energy_setup "$scaledobject"
case "$1" in
    fixed) wait_replicas 3 ;;
    queue) wait_replicas 0; wait_no_power ;;
esac

# En macOS, la máquina no se duerme mientras dura la corrida: dormida, se
# pausan Docker y k6, y los picos llegan tarde. caffeinate -w termina solo
# cuando termina este script. En Linux y Windows no está, y no hace falta.
if command -v caffeinate > /dev/null; then
    caffeinate -i -w $$ &
fi

started=$SECONDS
mark "Empieza la corrida: $1"
watch_events "$1" &
WATCHER_PID=$!
note "La corrida dura 10 minutos: tres picos, cada 4 minutos, y el worker ocioso entre"
note "uno y otro. Hay que esperar a que termine para comparar la energía."
docker compose run --rm k6 run --quiet /scripts/energy.js
remaining=$((DURATION - (SECONDS - started)))
if [ "$remaining" -gt 0 ]; then
    note "k6 terminó: se espera $remaining\" más, hasta completar los 10 minutos..."
    sleep "$remaining"
fi
mark "Termina la corrida: $1"
echo "  Energía estimada del worker en ${DURATION}\": $(worker_joules "$DURATION") J"
