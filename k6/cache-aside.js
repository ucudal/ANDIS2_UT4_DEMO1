// Cache-Aside: 50 lecturas de Products por segundo, repartidas entre los 3
// Products de los datos iniciales. Cada lectura de MongoDB demora 100 ms más,
// para simular una base más lenta. Durante los primeros 30 s la caché está
// apagada y toda lectura va a MongoDB; después de una pausa de 5 s se prende,
// y durante otros 30 s casi todas las lecturas salen de Redis.
//
// Qué mirar en Grafana:
// - "Latencia p95 por servicio": products-service baja de más de 100 ms a unos
//   5 ms cuando se prende la caché.
// - "Latencia p95 vista por k6, por endpoint": "product sin caché" por encima
//   de 100 ms; "product con caché" empieza alto, por los primeros miss, y baja
//   a unos 5 ms.
// - "Lecturas de la caché de Products, por resultado": "disabled" en la primera
//   fase; casi todo "hit" en la segunda, con algún "miss" al empezar.
import http from 'k6/http';
import exec from 'k6/execution';
import { GATEWAY, checkStatus } from './lib.js';

const PRODUCTS = ['60f7c2b5e1d3c2a1b8e4d123', '60f7c2b5e1d3c2a1b8e4d124', '60f7c2b5e1d3c2a1b8e4d125'];

export const options = {
    scenarios: {
        sin_cache: {
            executor: 'constant-arrival-rate',
            rate: 50,
            timeUnit: '1s',
            duration: '30s',
            preAllocatedVUs: 20,
            tags: { fase: 'sin caché' },
        },
        enable_cache: {
            executor: 'per-vu-iterations',
            exec: 'enableCache',
            vus: 1,
            iterations: 1,
            startTime: '32s',
        },
        con_cache: {
            executor: 'constant-arrival-rate',
            rate: 50,
            timeUnit: '1s',
            duration: '30s',
            startTime: '35s',
            preAllocatedVUs: 20,
            tags: { fase: 'con caché' },
        },
    },
    thresholds: {
        'http_req_duration{fase:con caché}': ['p(95)<50'],
    },
};

function cache(settings) {
    const res = http.put(`${GATEWAY}/products/cache`, JSON.stringify(settings),
        { headers: { 'Content-Type': 'application/json' }, tags: { name: 'cache settings' } });
    console.log(`caché: ${res.body}`);
}

export function setup() { cache({ enabled: false, dbDelayMs: 100 }); }

export function enableCache() { cache({ enabled: true, dbDelayMs: 100 }); }

export function teardown() {
    http.del(`${GATEWAY}/products/cache`, null, { tags: { name: 'cache settings' } });
    console.log('caché con los valores por defecto');
}

export default function () {
    // Un nombre por fase: k6 acumula el p95 desde que empieza el escenario, y
    // con un solo nombre la fase con caché no bajaría la línea.
    const id = PRODUCTS[Math.floor(Math.random() * PRODUCTS.length)];
    const name = exec.scenario.name === 'con_cache' ? 'product con caché' : 'product sin caché';
    checkStatus(http.get(`${GATEWAY}/products/${id}`, { tags: { name } }), 200);
}
