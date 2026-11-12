// Strangler Fig: una carga constante a la fachada mientras se migran las rutas
// del monolito a los microservicios. Lo lanza demos/load-modifiability.sh, que
// hace la migración y marca cada paso en Grafana.
//
// Qué mirar en Grafana: las solicitudes pasan del monolito a cada servicio, una
// ruta por vez, hasta que el monolito llega a cero; las respuestas con error
// siguen en cero. La latencia del detalle sube al migrar Orders: el JOIN del
// monolito se reemplaza por llamadas HTTP, que es el costo de separar los datos.
import http from 'k6/http';
import { Counter, Trend } from 'k6/metrics';
import { GATEWAY, checkStatus } from './lib.js';

const FACADE = __ENV.FACADE || GATEWAY.replace(/:\d+$/, '') + ':8090';
const PRODUCT = '1';
const SOAP = '<soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"><soap:Body>'
    + '<GetUser xmlns="http://ucu.edu.uy/andis/users"><id>665f1c2ab4e8d9a0c1f2e301</id></GetUser></soap:Body></soap:Envelope>';

// Quién atendió cada solicitud lo sabe la fachada, que lo informa en el
// encabezado X-Served-By: k6 lo usa como etiqueta.
const requests = new Counter('strangler_requests');
const detailsDuration = new Trend('strangler_details_duration', true);

export const options = {
    scenarios: {
        reads: {
            executor: 'constant-arrival-rate',
            rate: 15,
            timeUnit: '1s',
            duration: __ENV.DURATION || '70s',
            preAllocatedVUs: 20,
        },
    },
};

function call(route, res) {
    const backend = (res.headers['X-Served-By'] || 'sin respuesta').split(':')[0];
    requests.add(1, { route, backend, status: String(res.status) });
    checkStatus(res, 200);
    return backend;
}

export default function () {
    switch (__ITER % 3) {
        case 0:
            call('users', http.post(`${FACADE}/users/UserService.svc`, SOAP, {
                headers: {
                    'Content-Type': 'text/xml; charset=utf-8',
                    SOAPAction: '"http://ucu.edu.uy/andis/users/IUserService/GetUser"',
                },
                tags: { name: 'facade users' },
            }));
            break;
        case 1:
            call('products', http.get(`${FACADE}/products/${PRODUCT}`, { tags: { name: 'facade products' } }));
            break;
        default: {
            const res = http.get(`${FACADE}/orders/user/665f1c2ab4e8d9a0c1f2e301/details`, { tags: { name: 'facade details' } });
            detailsDuration.add(res.timings.duration, { backend: call('orders', res) });
        }
    }
}
