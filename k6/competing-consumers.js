// Competing Consumers: se crean 60 Orders lo más rápido posible, directamente
// en orders-service para no chocar con el límite de POST del gateway. Cada
// réplica de orders-worker demora 2 s por Order.
//
// Qué mirar en Grafana: la cola crece de golpe y baja a un ritmo de unas 1,5
// Orders por segundo -3 réplicas, 2 s cada una-, repartidas en partes iguales.
// Para ver qué pasa si se cae una réplica, durante la prueba ejecuta en otra
// terminal: docker stop $(docker compose ps -q orders-worker | head -1)
import { ORDERS_DIRECT, createOrder, checkStatus } from './lib.js';

const ORDERS = Number(__ENV.ORDERS || 60);

export const options = {
    scenarios: {
        burst: { executor: 'shared-iterations', vus: 10, iterations: ORDERS },
    },
};

export default function () { checkStatus(createOrder(ORDERS_DIRECT), 202); }
