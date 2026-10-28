// Bulkhead bajo carga: Products responde lento -1,5 s- y llegan 6 consultas
// de detalle por segundo. El compartimento de Products admite 5 llamadas en
// curso y 5 en espera; las demás se rechazan de inmediato.
//
// Qué mirar en Grafana: rechazos del bulkhead solo en el pipeline de Products;
// el de Users no tiene rechazos y su latencia no cambia.
import { chaos, resetChaos, details, checkStatus } from './lib.js';

export const options = {
    scenarios: {
        traffic: {
            executor: 'constant-arrival-rate',
            rate: 6,
            timeUnit: '1s',
            duration: '45s',
            preAllocatedVUs: 30,
            maxVUs: 60,
        },
    },
};

export function setup() { chaos('products', 0, 1500); }

export default function () { checkStatus(details(), 200); }

export function teardown() { resetChaos(); }
