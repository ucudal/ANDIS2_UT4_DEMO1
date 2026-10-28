// Prueba de carga base, para las demos de rendimiento: la tasa de solicitudes
// sube hasta RATE por segundo, se mantiene y baja. La mezcla de solicitudes
// imita a un cliente que consulta mucho más de lo que escribe.
//
// Los thresholds definen el objetivo de rendimiento: si no se cumplen, k6
// termina con error. Se pueden cambiar sin tocar el script:
//   docker compose run --rm k6 run -e RATE=100 -e DURATION=2m /scripts/load.js
import http from 'k6/http';
import { GATEWAY, details, checkStatus } from './lib.js';

const RATE = Number(__ENV.RATE || 30);
const DURATION = __ENV.DURATION || '1m';

export const options = {
    scenarios: {
        load: {
            executor: 'ramping-arrival-rate',
            startRate: 0,
            timeUnit: '1s',
            preAllocatedVUs: 50,
            maxVUs: 200,
            stages: [
                { duration: '15s', target: RATE },
                { duration: DURATION, target: RATE },
                { duration: '15s', target: 0 },
            ],
        },
    },
    thresholds: {
        http_req_failed: ['rate<0.01'],
        http_req_duration: ['p(95)<500'],
    },
};

export default function () {
    const r = Math.random();
    // Siempre el User 1: los demás escenarios crean Orders para el User 2, y su
    // detalle crece con cada prueba, lo que haría incomparables las corridas.
    if (r < 0.5)
        checkStatus(details(1), 200);
    else if (r < 0.8)
        checkStatus(http.get(`${GATEWAY}/products/`, { tags: { name: 'list products' } }), 200);
    else
        checkStatus(http.get(`${GATEWAY}/users/`, { tags: { name: 'list users' } }), 200);
}
