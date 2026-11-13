// Eficiencia energética: tres picos de BURST Orders, cada 4 minutos -a los
// 30 segundos, a los 4:30 y a los 8:30-, directamente en orders-service, con el
// worker ocioso entre uno y otro. Lo lanza demos/load-energy.sh, con el worker
// del clúster en una de las configuraciones de las demos 35 a 37; cada corrida
// dura 10 minutos, para que la energía de las tres se pueda comparar en el
// mismo período.
//
// Los picos están separados para que el worker pase ocioso más tiempo que el
// que tarda en recuperar la energía de encender sus réplicas: en la demo 36,
// unos 100 segundos. Con picos más seguidos, el escalado a cero consume más que
// las réplicas fijas.
//
// Qué mirar en Grafana, en la fila "Eficiencia energética":
// - "Réplicas del worker en el clúster": 3 todo el tiempo con fixed; con queue,
//   0 entre picos y hasta 6 en cada uno; con window, 3 solo mientras la
//   ventana está abierta, un minuto de cada dos.
// - "Potencia estimada del worker, por pod": con fixed, un piso que no baja
//   nunca; con queue, nada entre picos y un salto al arrancar cada pod.
// - "Energía estimada del worker en el período": con el rango en 10 minutos,
//   la energía de toda la corrida.
import http from 'k6/http';
import exec from 'k6/execution';
import { ORDERS_DIRECT, createOrder, checkStatus } from './lib.js';

const BURST = Number(__ENV.BURST || 30);
const GRAFANA = __ENV.GRAFANA || 'http://grafana:3000';
const NUMBER = { first: 1, second: 2, third: 3 };

// La primera Order de cada pico lo marca en el dashboard, con la etiqueta
// energía, como las que marcan los scripts con la función annotate.
function markBurst() {
    const text = `Pico ${NUMBER[exec.scenario.name]} de 3: ${BURST} Orders`;
    http.post(`${GRAFANA}/api/annotations`,
        JSON.stringify({ dashboardUID: 'ut4-demo1', tags: ['energía'], text }),
        { headers: { 'Content-Type': 'application/json' }, tags: { name: 'annotation' } });
}

function burst(startTime) {
    return {
        executor: 'shared-iterations',
        vus: BURST,
        iterations: BURST,
        startTime,
        maxDuration: '30s',
    };
}

export const options = {
    scenarios: {
        first: burst('30s'),
        second: burst('270s'),
        third: burst('510s'),
    },
    thresholds: {
        http_req_failed: ['rate<0.01'],
    },
};

export default function () {
    if (exec.scenario.iterationInTest === 0) markBurst();
    checkStatus(createOrder(ORDERS_DIRECT), 202);
}
