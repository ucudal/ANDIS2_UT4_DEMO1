// Messaging Bridge: 2 Orders por segundo para el User 2, directamente en
// orders-service. Lo lanza demos/load-modifiability.sh, que detiene ActiveMQ
// durante 30 segundos.
//
// Qué mirar en Grafana: la cola bridge.erp crece durante la caída y se vacía al
// volver el broker. Las Orders se siguen confirmando: la aplicación no depende
// del ERP.
import { ORDERS_DIRECT, createOrder, checkStatus } from './lib.js';

export const options = {
    scenarios: {
        writes: {
            executor: 'constant-arrival-rate',
            rate: 2,
            timeUnit: '1s',
            duration: __ENV.DURATION || '80s',
            preAllocatedVUs: 10,
        },
    },
};

export default function () {
    checkStatus(createOrder(ORDERS_DIRECT), 202);
}
