// Circuit Breaker bajo carga: tráfico constante de consultas de detalle; a los
// 15 s Products empieza a fallar siempre y a los 50 s se recupera.
//
// Qué mirar en Grafana: al empezar la falla la latencia sube por los
// reintentos; cuando el circuito se abre vuelve a bajar, porque las llamadas a
// Products fallan de inmediato. Las respuestas siguen siendo 200, degradadas.
// Tras la recuperación, el circuito pasa a semiabierto y se cierra.
import { chaos, resetChaos, details, checkStatus } from './lib.js';

export const options = {
    scenarios: {
        traffic: {
            executor: 'constant-arrival-rate',
            exec: 'traffic',
            rate: 5,
            timeUnit: '1s',
            duration: '80s',
            preAllocatedVUs: 20,
        },
        failure: { executor: 'per-vu-iterations', exec: 'failure', vus: 1, iterations: 1, startTime: '15s' },
        recovery: { executor: 'per-vu-iterations', exec: 'recovery', vus: 1, iterations: 1, startTime: '50s' },
    },
};

export function setup() { resetChaos(); }

export function traffic() { checkStatus(details(), 200); }

export function failure() { chaos('products', 1, 0); }

export function recovery() { resetChaos(); }

export function teardown() { resetChaos(); }
