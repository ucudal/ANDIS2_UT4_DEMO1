// Anti-corruption Layer: la misma carga a Orders y a Purchases, que lee de Orders
// a través de su capa anticorrupción. Lo lanza demos/load-modifiability.sh, que a
// mitad de camino le agrega una demora a Users y la quita.
//
// Qué mirar en Grafana: Purchases tarda más que Orders, porque la capa hace una
// llamada más y le pregunta a Users por SOAP el nombre de cada comprador. Cuando
// Users se demora, solo sube Purchases: es lo que cuesta no adoptar el modelo de
// los otros servicios.
import http from 'k6/http';
import { Trend } from 'k6/metrics';
import { GATEWAY, checkStatus } from './lib.js';

const PRODUCT = '60f7c2b5e1d3c2a1b8e4d125';
const duration = new Trend('acl_duration', true);

export const options = {
    scenarios: {
        reads: {
            executor: 'constant-arrival-rate',
            rate: 10,
            timeUnit: '1s',
            duration: __ENV.DURATION || '70s',
            preAllocatedVUs: 20,
        },
    },
};

export default function () {
    const [source, url] = __ITER % 2 === 0
        ? ['Orders', `${GATEWAY}/orders/`]
        : ['Purchases', `${GATEWAY}/purchases/product/${PRODUCT}`];
    const res = http.get(url, { tags: { name: source } });
    duration.add(res.timings.duration, { source });
    checkStatus(res, 200);
}
