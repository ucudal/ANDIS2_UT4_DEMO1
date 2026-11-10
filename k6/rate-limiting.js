// Rate Limiting: un cliente crea Orders a través del gateway, que acepta como
// máximo 1 POST /orders/ por segundo por IP, con una ráfaga de 5. Durante los
// primeros 30 segundos el cliente no regula su ritmo y envía 5 por segundo;
// después de una pausa de 5 segundos, durante otros 30 segundos respeta el
// límite y envía 1 por segundo. Mientras tanto se consulta la lista de Orders,
// que no tiene límite.
//
// Qué mirar en Grafana: en la primera fase, de los POST pasa uno por segundo y
// el resto recibe 429; en la segunda no hay 429. Las Orders creadas por segundo
// son las mismas en las dos fases: el cliente sin Rate Limiting no avanza más
// rápido, solo envía más solicitudes.
import http from 'k6/http';
import { GATEWAY, createOrder, checkStatus } from './lib.js';

export const options = {
    scenarios: {
        sin_rate_limiting: {
            executor: 'constant-arrival-rate',
            exec: 'writesWithoutRateLimiting',
            rate: 5,
            timeUnit: '1s',
            duration: '30s',
            preAllocatedVUs: 10,
        },
        con_rate_limiting: {
            executor: 'constant-arrival-rate',
            exec: 'writesWithRateLimiting',
            rate: 1,
            timeUnit: '1s',
            duration: '30s',
            startTime: '35s',
            preAllocatedVUs: 5,
        },
        reads: {
            executor: 'constant-arrival-rate',
            exec: 'reads',
            rate: 5,
            timeUnit: '1s',
            duration: '65s',
            preAllocatedVUs: 10,
        },
    },
};

export function writesWithoutRateLimiting() { checkStatus(createOrder(), 202, 429); }

export function writesWithRateLimiting() { checkStatus(createOrder(), 202); }

export function reads() {
    checkStatus(http.get(`${GATEWAY}/orders/`, { tags: { name: 'list orders' } }), 200);
}
