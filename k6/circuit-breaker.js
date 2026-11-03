// Circuit Breaker bajo carga: tráfico constante de consultas de detalle; a los
// 15 s Products empieza a fallar siempre y a los 50 s se recupera.
//
// Solo el Circuit Breaker queda prendido: sin Retry, cada llamada fallida
// cuenta una vez para el circuito.
//
// Qué mirar en Grafana: "products · HTTP 503" por unos segundos y después
// "products · BrokenCircuit", cuando el circuito se abre y las llamadas a
// Products fallan de inmediato. Como la falla dura más que los 15 s de circuito
// abierto, el circuito pasa a semiabierto y se vuelve a abrir. Las respuestas
// siguen siendo 200, degradadas. Tras la recuperación, el circuito pasa a
// semiabierto y se cierra. "Estado del circuito de cada dependencia" muestra
// products en rojo de corrido mientras el circuito está abierto.
import { chaos, resetChaos, onlyPattern, resetPatterns, details, checkStatus } from './lib.js';

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

export function setup() { resetChaos(); onlyPattern('circuitBreaker'); }

export function traffic() { checkStatus(details(), 200); }

export function failure() { chaos('products', 1, 0); }

export function recovery() { resetChaos(); }

export function teardown() { resetChaos(); resetPatterns(); }
