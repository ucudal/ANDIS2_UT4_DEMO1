// Backends for Frontends: la web y la app móvil consultan las Orders de alice,
// cada una por su backend. Lo lanza demos/load-modifiability.sh, que a mitad de
// camino redespliega el backend de la app, como si su equipo publicara un cambio.
//
// Qué mirar en Grafana: durante el redespliegue, la app puede tener algún error;
// la web sigue respondiendo 200 OK, porque su backend no se tocó.
import http from 'k6/http';
import { Counter } from 'k6/metrics';
import { GATEWAY, checkStatus } from './lib.js';

const requests = new Counter('bff_requests');

export const options = {
    scenarios: {
        reads: {
            executor: 'constant-arrival-rate',
            rate: 20,
            timeUnit: '1s',
            duration: __ENV.DURATION || '60s',
            preAllocatedVUs: 20,
        },
    },
};

export default function () {
    const client = __ITER % 2 === 0 ? 'web' : 'mobile';
    const res = http.get(`${GATEWAY}/${client}/orders/user/665f1c2ab4e8d9a0c1f2e301`, { tags: { name: client }, timeout: '2s' });
    requests.add(1, { client, status: String(res.status) });
    checkStatus(res, 200);
}
