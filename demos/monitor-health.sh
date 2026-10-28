#!/usr/bin/env bash
# Health Endpoint Monitoring: consulta el /health de cada servicio cada 2
# segundos. Ctrl+C para terminar.
cd "$(dirname "$0")/.."
GATEWAY=${GATEWAY:-http://localhost}

while true; do
    clear
    echo "Salud de los servicios ($(date +%H:%M:%S))"
    echo
    for s in users products orders; do
        response=$(curl -s -m 5 -w ' %{http_code}' "$GATEWAY/health/$s" || echo ' sin respuesta')
        printf '  %-9s %s\n' "$s" "$response"
    done
    echo
    echo "Estado según Docker"
    docker compose ps --format '  {{.Service}}: {{.Status}}'
    sleep 2
done
