// Queue-Based Load Leveling: un pico de RATE POST /orders/ por segundo durante
// DURATION, directamente en orders-service: el gateway admite 1 POST por
// segundo por cliente y rechazaría el pico antes de llegar a la cola. Los
// workers procesan unas 1,5 Orders por segundo -3 réplicas, 2 s cada una-, muy
// por debajo del pico.
//
// Qué mirar en Grafana:
// - "Latencia p95 vista por k6, por endpoint": "create order" se mantiene por
//   debajo de 30 ms durante todo el pico, porque POST solo escribe en la base.
// - "Cola orders.process": "esperando" sube a unos 80 durante el pico y baja a
//   ritmo constante durante algo más de un minuto; "en proceso" queda en 3,
//   uno por réplica.
//
// Para ver Competing Consumers como patrón de rendimiento, repetir con 6
// réplicas: la cola se vacía en la mitad del tiempo.
//   docker compose up -d --scale orders-worker=6
//   docker compose run --rm k6 run /scripts/load-leveling.js
//   docker compose up -d --scale orders-worker=3
import { ORDERS_DIRECT, createOrder, checkStatus } from './lib.js';

const RATE = Number(__ENV.RATE || 10);
const DURATION = __ENV.DURATION || '10s';

export const options = {
    scenarios: {
        peak: {
            executor: 'constant-arrival-rate',
            rate: RATE,
            timeUnit: '1s',
            duration: DURATION,
            preAllocatedVUs: 20,
        },
    },
    thresholds: {
        'http_req_duration{name:create order}': ['p(95)<200'],
        http_req_failed: ['rate<0.01'],
    },
};

export default function () { checkStatus(createOrder(ORDERS_DIRECT), 202); }
