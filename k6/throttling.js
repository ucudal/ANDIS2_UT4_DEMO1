// Throttling: Orders demora 1 segundo en cada consulta de detalle y la cantidad
// de usuarios virtuales sube de 0 a 40. Orders atiende como máximo 20
// consultas de detalle a la vez; las demás reciben 503 con Retry-After.
//
// Solo el Throttling queda prendido: sin Bulkhead, las consultas aceptadas
// traen todos los nombres.
//
// Qué mirar en Grafana: mientras hay menos de 20 consultas en curso todo es
// 200; cuando "en curso" llega a 20 en "Throttling en Orders", aparecen los 503
// en rojo y las aceptadas se estabilizan en unas 100 por intervalo de 5
// segundos: 20 lugares, cada uno ocupado 1 segundo por consulta.
import { sleep } from 'k6';
import { chaos, resetChaos, onlyPattern, resetPatterns, details, checkStatus } from './lib.js';

export const options = {
    scenarios: {
        ramp: {
            executor: 'ramping-vus',
            stages: [
                { duration: '20s', target: 40 },
                { duration: '20s', target: 40 },
                { duration: '20s', target: 0 },
            ],
        },
    },
};

export function setup() { onlyPattern('throttling'); chaos('orders', 0, 1000); }

export default function () {
    const res = details();
    checkStatus(res, 200, 503);
    // Un cliente bien portado respeta Retry-After antes de volver a intentar.
    if (res.status === 503)
        sleep(Number(res.headers['Retry-After'] || 1));
}

export function teardown() { resetChaos(); resetPatterns(); }
