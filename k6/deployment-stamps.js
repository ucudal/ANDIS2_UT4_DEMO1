// Deployment Stamps: dos grupos de clientes, uy y br, leen un Product. En la
// primera fase todos usan la misma copia, como con un solo despliegue; en la
// segunda, cada grupo usa la suya. Lo lanza demos/load-modifiability.sh, que
// hace fallar a Products de la primera copia una vez en cada fase.
//
// Qué mirar en Grafana: en la primera falla, los dos grupos dejan de recibir 200 OK;
// en la segunda, solo el grupo uy. El radio de explosión pasa del 100 % al 50 %
// de los clientes.
import http from 'k6/http';
import { Counter } from 'k6/metrics';
import { GATEWAY } from './lib.js';

const requests = new Counter('stamp_requests');

function read(client, region) {
    const res = http.get(`${GATEWAY}/products/60f7c2b5e1d3c2a1b8e4d123`, {
        headers: { 'X-Region': region },
        tags: { name: 'stamp product' },
    });
    requests.add(1, { client, stamp: res.headers['X-Stamp'] || 'sin respuesta', status: String(res.status) });
}

const phase = (startTime, exec) => ({
    executor: 'constant-arrival-rate',
    exec,
    rate: 10,
    timeUnit: '1s',
    duration: '45s',
    startTime,
    preAllocatedVUs: 15,
});

export const options = {
    scenarios: {
        un_despliegue: phase('0s', 'oneDeployment'),
        dos_copias: phase('55s', 'twoStamps'),
    },
};

export function oneDeployment() {
    read(__ITER % 2 === 0 ? 'uy' : 'br', 'uy');
}

export function twoStamps() {
    const client = __ITER % 2 === 0 ? 'uy' : 'br';
    read(client, client);
}
