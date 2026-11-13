# Funciones comunes de las demos con Kubernetes: las de despliegue, de la 32 a
# la 34, y las de eficiencia energética, de la 35 a la 37.
# Se carga después de demos/common.sh, del que se usan get_user, user_summary,
# versions, tally y deploy_probe. Acá apuntan al Gateway del clúster, en el
# puerto 8092, en lugar de la entrada de despliegue de Docker Compose. No se
# ejecuta directamente.

DEPLOY=http://localhost:8092
PREVIEW=http://preview.localhost:8092

# demos/common.sh, al terminar, quita las fallas simuladas de Docker Compose:
# acá no corresponde.
trap - EXIT

kubectl config use-context kind-andis > /dev/null 2>&1 \
    || { echo "No está el clúster: hay que crearlo con ./demos/k8s-up.sh"; exit 1; }

# rollouts <argumentos>: el plugin de Argo Rollouts, en el namespace andis.
rollouts() { kubectl argo rollouts -n andis "$@"; }

# strategy_apply <carpeta>: aplica los manifiestos de una estrategia, de
# k8s/<carpeta>, y espera a que la versión v1 esté sana y responda por el
# Gateway. Las tres estrategias usan el mismo Rollout, users: hay una sola a la
# vez.
strategy_apply() {
    kubectl apply -f "k8s/$1" > /dev/null
    rollouts status users --timeout 180s > /dev/null
    until [ "$(get_user "$ALICE" -o /dev/null -w '%{http_code}')" = 200 ]; do sleep 1; done
}

# strategy_delete <carpeta>: borra los manifiestos de una estrategia, sin
# esperar a que terminen los pods.
strategy_delete() {
    kubectl delete -f "k8s/$1" --ignore-not-found --wait=false > /dev/null 2>&1 || true
}

# rollout_tree: el estado del Rollout como lo muestra Argo Rollouts: cada
# versión, con su ReplicaSet y sus pods, y para qué está, por ejemplo stable,
# active o preview.
rollout_tree() {
    rollouts get rollout users --no-color | sed -n '/^NAME/,$p' | sed 's/^/  /'
}

# wait_phase <fase>: espera a que el Rollout llegue a una fase, por ejemplo
# Paused o Healthy.
wait_phase() {
    until [ "$(kubectl -n andis get rollout users -o jsonpath='{.status.phase}')" = "$1" ]; do sleep 1; done
}

# while_probing <comando>: ejecuta un comando mientras una sonda pide el User de alice
# cada 0,1 segundos, y muestra qué respondió cada versión durante el cambio.
while_probing() {
    local probes running
    probes=$(mktemp)
    running=$(mktemp)
    deploy_probe "$probes" "$running" &
    sleep 2
    "$@" > /dev/null
    sleep 4
    rm -f "$running"
    wait
    tally "$probes"
    rm -f "$probes"
}

# Funciones de las demos de eficiencia energética, de la 35 a la 37. En ellas,
# el worker de Orders corre en el clúster y usa RabbitMQ, la base de Orders,
# Users y Products de Docker Compose.

PROMETHEUS_K8S=http://localhost:30090

# compose_network: conecta el nodo de kind a la red de Docker Compose. Así los
# pods llegan a rabbitmq, orders-db, users-service y los demás por su nombre.
compose_network() {
    docker network connect "$(app_network)" andis-control-plane 2> /dev/null || true
}

# compose_workers <réplicas>: las réplicas de orders-worker en Docker Compose.
# Mientras corre el worker del clúster, quedan en 0, para que no compitan por
# la cola orders.process.
compose_workers() {
    docker compose up -d --no-deps --scale orders-worker="$1" orders-worker > /dev/null 2>&1
}

# worker_apply [ScaledObject]: aplica el worker de k8s/energy y, si se indica,
# el ScaledObject de KEDA que decide sus réplicas: queue o window.
worker_apply() {
    kubectl apply -f k8s/energy/worker-config.yaml -f k8s/energy/rabbitmq-auth.yaml \
        -f k8s/energy/worker.yaml > /dev/null
    if [ -n "${1:-}" ]; then
        kubectl apply -f "k8s/energy/scaledobject-$1.yaml" > /dev/null
    fi
}

# worker_delete: borra el worker y su ScaledObject, sin esperar a los pods.
worker_delete() {
    kubectl -n andis delete scaledobject orders-worker --ignore-not-found > /dev/null 2>&1 || true
    kubectl -n andis delete deployment orders-worker --ignore-not-found --wait=false > /dev/null 2>&1 || true
}

# worker_replicas: las réplicas del worker que están listas.
worker_replicas() {
    local ready
    ready=$(kubectl -n andis get deployment orders-worker -o jsonpath='{.status.readyReplicas}' 2> /dev/null)
    echo "${ready:-0}"
}

# worker_pods: los pods del worker que existen, listos o no.
worker_pods() {
    kubectl -n andis get pods -l app=orders-worker --no-headers 2> /dev/null | grep -c . || true
}

# wait_replicas <n>: espera a que el worker tenga n réplicas listas y ningún pod
# de más, por ejemplo uno que se está deteniendo.
wait_replicas() {
    until [ "$(worker_replicas)" = "$1" ] && [ "$(worker_pods)" = "$1" ]; do sleep 1; done
}

# promql <consulta>: el valor de una consulta en el Prometheus del clúster.
promql() {
    curl -s "$PROMETHEUS_K8S/api/v1/query" --data-urlencode "query=$1" \
        | jq -r '.data.result[0].value[1] // "0"'
}

# worker_watts: la potencia estimada de todos los pods del worker, en watts,
# con dos decimales. El modelo está en k8s/base/prometheus.yaml.
worker_watts() {
    printf '%.2f' "$(promql 'sum(pod:power:watts{pod=~"orders-worker-.*"})')"
}

# worker_cpu: las vCPU que usan y que reservan los pods del worker.
worker_cpu() {
    printf 'usadas %.3f, reservadas %.2f' \
        "$(promql 'sum(pod:cpu_used:vcpu{pod=~"orders-worker-.*"})')" \
        "$(promql 'sum(pod:cpu_reserved:vcpu{pod=~"orders-worker-.*"})')"
}

# energy_setup [ScaledObject]: prepara una demo de eficiencia energética.
# Conecta el nodo a la red de Docker Compose, deja en 0 el orders-worker de
# Compose y aplica el del clúster.
energy_setup() {
    compose_network
    compose_workers 0
    worker_apply "${1:-}"
}

# energy_cleanup: borra el worker del clúster, devuelve el de Docker Compose a
# 3 réplicas y borra la ventana de la demo 37 del almacén de configuración: sin
# valor, la ventana está cerrada.
energy_cleanup() {
    worker_delete
    compose_workers 3
    curl -s -X DELETE "$CONSUL/v1/kv/andis/Energy/WindowOpen" > /dev/null
}

# burst <n>: crea n Orders de bob a la vez, directamente en orders-service, y
# devuelve sus ids.
burst() {
    seq "$1" | xargs -P "$1" -I{} curl -s -X POST "$ORDERS_DIRECT/orders/" \
        -H 'Content-Type: application/json' \
        -d "{\"userId\": \"$BOB\", \"productId\": 3, \"quantity\": 1}" | jq -r .id
}

# pending <ids>: cuántas de esas Orders siguen Pending.
pending() {
    local n=0
    for id in $1; do
        curl -s "$ORDERS_DIRECT/orders/$id" | grep -q '"status":"Pending"' && n=$((n + 1))
    done
    echo "$n"
}

# worker_line: una línea con la hora, las réplicas del worker, los mensajes de
# orders.process y la potencia estimada.
worker_line() {
    printf '  %s  réplicas %s  cola %3s  %5s W\n' "$(date +%T)" "$(worker_replicas)" \
        "$(queue_length orders.process)" "$(worker_watts)"
}

# watch_worker <segundos> [ids]: muestra worker_line cada 5 segundos durante
# ese tiempo, o hasta que no quede ninguna de esas Orders Pending. Deja en
# MAX_REPLICAS el máximo de réplicas que vio.
MAX_REPLICAS=0
watch_worker() {
    local until=$((SECONDS + $1)) replicas
    MAX_REPLICAS=0
    while [ "$SECONDS" -lt "$until" ]; do
        replicas=$(worker_replicas)
        [ "$replicas" -gt "$MAX_REPLICAS" ] && MAX_REPLICAS=$replicas
        worker_line
        if [ -n "${2:-}" ] && [ "$(pending "$2")" = 0 ]; then return; fi
        sleep 5
    done
}

# wait_no_power: espera a que Prometheus deje de contar los pods del worker que
# se borraron. Tarda unos segundos en notar que ya no están.
wait_no_power() {
    until [ "$(worker_watts)" = "0.00" ]; do sleep 1; done
}

# worker_joules <segundos>: la energía estimada del worker en los últimos
# segundos, en joules: la potencia de cada muestra, cada 5 segundos, por 5.
worker_joules() {
    printf '%.0f' "$(promql "sum_over_time(sum(pod:power:watts{pod=~\"orders-worker-.*\"})[$1s:5s]) * 5")"
}

# power_table: la CPU y la potencia de cada pod del worker, según el modelo de
# k8s/base/prometheus.yaml.
power_table() {
    local query='label_join(pod:cpu_used:vcpu{pod=~"orders-worker-.*"}, "kind", "", "__name__")
        or label_join(pod:cpu_reserved:vcpu{pod=~"orders-worker-.*"}, "kind", "", "__name__")
        or label_join(pod:power:watts{pod=~"orders-worker-.*"}, "kind", "", "__name__")'
    {
        printf 'Pod\tvCPU usadas\tvCPU reservadas\tW\n'
        curl -s "$PROMETHEUS_K8S/api/v1/query" --data-urlencode "query=$query" | jq -r '
            [.data.result[] | {pod: .metric.pod, kind: .metric.kind, v: (.value[1] | tonumber)}]
            | group_by(.pod)[]
            | (map({(.kind): .v}) | add) as $m
            | [.[0].pod,
               ($m["pod:cpu_used:vcpu"] // 0 | . * 1000 | round / 1000),
               ($m["pod:cpu_reserved:vcpu"] // 0),
               ($m["pod:power:watts"] // 0 | . * 100 | round / 100)]
            | @tsv'
    } | column -t -s $'\t' | sed 's/^/  /'
}

# wait_rate: espera a que Prometheus tenga al menos dos muestras de la CPU de
# cada pod del worker, las que necesita rate() para calcular la CPU usada.
# cAdvisor las actualiza cada 10 a 15 segundos.
wait_rate() {
    note "Esperando 35 segundos a que Prometheus tenga muestras de los pods..."
    sleep 35
}

# wait_idle: espera a que los pods del worker terminen de arrancar. Al arrancar,
# cada uno usa más CPU, y la CPU usada se calcula sobre un minuto: tarda 2 o 3
# minutos en bajar a la de un pod ocioso, menos de 0.02 vCPU por pod.
wait_idle() {
    note "Esperando a que los pods terminen de arrancar, unos 2 o 3 minutos..."
    until [ "$(promql 'sum(pod:cpu_used:vcpu{pod=~"orders-worker-.*"}) < bool 0.05')" = 1 ]; do
        worker_line
        sleep 10
    done
}

# window_open: si la ventana de la demo 37 está abierta, según el endpoint que
# consulta KEDA -ver k8s/energy/scaledobject-window.yaml-.
window_open() { [ "$(curl -s "$ORDERS_DIRECT/energy/window" | jq -r .open)" = true ]; }

# window_set <open|closed>: abre o cierra la ventana, con la clave
# Energy/WindowOpen del almacén de configuración. Espera a que orders-service
# la lea.
window_set() {
    case "$1" in
        open) config_set Energy/WindowOpen true ;;
        closed) config_set Energy/WindowOpen false ;;
    esac
}
