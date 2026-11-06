// External Configuration Store: 2 POST /orders/ por segundo, con una cantidad de
// 10 unidades, para el User 2, y 20 lecturas por segundo, directamente en
// orders-service. Lo lanza demos/load-modifiability.sh, que cambia el mismo límite
// de dos formas: con una variable de entorno, que obliga a recrear el contenedor,
// y con el almacén. Las lecturas muestran cuánto dura el corte: con pocas
// solicitudes por segundo casi no se notaría.
//
// Qué mirar en Grafana: las dos formas pasan de 202 Accepted a 400 Bad Request,
// pero la variable de entorno deja solicitudes sin respuesta, y el almacén no. En
// "Valor vigente por proceso", los procesos cambian de valor sin reiniciarse.
import http from 'k6/http';
import { Counter } from 'k6/metrics';
import { ORDERS_DIRECT } from './lib.js';

const requests = new Counter('config_requests');

export const options = {
    // k6 guarda la IP de cada nombre: al recrearse, el contenedor cambia de IP.
    dns: { ttl: '1s' },
    scenarios: {
        writes: {
            executor: 'constant-arrival-rate',
            exec: 'writes',
            rate: 2,
            timeUnit: '1s',
            duration: __ENV.DURATION || '115s',
            preAllocatedVUs: 10,
        },
        reads: {
            executor: 'constant-arrival-rate',
            exec: 'reads',
            rate: 20,
            timeUnit: '1s',
            duration: __ENV.DURATION || '115s',
            preAllocatedVUs: 40,
        },
    },
};

// Sin respuesta, k6 informa el código 0.
function count(operation, res) {
    requests.add(1, { operation, status: res.status === 0 ? 'sin respuesta' : String(res.status) });
}

export function writes() {
    const body = JSON.stringify({ userId: 2, productId: '60f7c2b5e1d3c2a1b8e4d124', quantity: 10 });
    const res = http.post(`${ORDERS_DIRECT}/orders/`, body, {
        headers: { 'Content-Type': 'application/json' },
        timeout: '2s',
        tags: { name: 'create order' },
    });
    count('POST', res);
}

export function reads() {
    count('GET', http.get(`${ORDERS_DIRECT}/orders/1`, { timeout: '2s', tags: { name: 'order' } }));
}
