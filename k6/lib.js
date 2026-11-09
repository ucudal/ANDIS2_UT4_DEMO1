// Funciones comunes de los escenarios de k6. No se ejecuta directamente.
import http from 'k6/http';
import { check } from 'k6';
import { Counter, Rate } from 'k6/metrics';

export const GATEWAY = __ENV.BASE_URL || 'http://localhost';
export const ORDERS_DIRECT = __ENV.ORDERS_DIRECT || 'http://localhost:8000';

// Fracción de respuestas de detalle que llegaron con algún dato "no disponible":
// la solicitud respondió 200, pero degradada.
export const degraded = new Rate('degraded_responses');

// chaos('products', 1, 0): Products falla siempre; chaos('orders', 0, 2000):
// Orders demora 2 segundos.
export function chaos(service, failureRate, delayMs) {
    http.put(`${GATEWAY}/${service}/chaos`, JSON.stringify({ failureRate, delayMs }),
        { headers: { 'Content-Type': 'application/json' }, tags: { name: 'chaos' } });
    console.log(`chaos en ${service}: failureRate=${failureRate}, delayMs=${delayMs}`);
}

// onlyPattern('retry'): deja prendido en Orders solo ese patrón de resiliencia
// -retry, circuitBreaker, bulkhead o throttling-, para que el escenario muestre
// un patrón por vez.
export function onlyPattern(pattern) {
    const settings = {};
    for (const p of ['retry', 'circuitBreaker', 'bulkhead', 'throttling'])
        settings[p] = p === pattern;
    http.put(`${GATEWAY}/orders/patterns`, JSON.stringify(settings),
        { headers: { 'Content-Type': 'application/json' }, tags: { name: 'chaos' } });
    console.log(`patrones de Orders: solo ${pattern}`);
}

export function resetPatterns() {
    http.del(`${GATEWAY}/orders/patterns`, null, { tags: { name: 'chaos' } });
    console.log('patrones de Orders: todos prendidos');
}

export function resetChaos() {
    for (const service of ['users', 'products', 'orders'])
        http.del(`${GATEWAY}/${service}/chaos`, null, { tags: { name: 'chaos' } });
    console.log('chaos desactivado');
}

// Detalle de las Orders del User 1. El tag name agrupa las métricas por
// endpoint en lugar de por URL. Con view = true se lee la vista materializada
// -CQRS- en lugar de armar el detalle llamando a Users y a Products.
export function details(userId = 1, view = false) {
    const path = view ? 'details/view' : 'details';
    const res = http.get(`${GATEWAY}/orders/user/${userId}/${path}`, { tags: { name: view ? 'details view' : 'details' } });
    if (res.status === 200)
        degraded.add(res.body.includes('no disponible'));
    return res;
}

// POST /orders/ responde 202 Accepted: la Order queda Pending hasta que la
// procesa un worker -Asynchronous Request-Reply-.
export function createOrder(baseUrl = GATEWAY) {
    const body = JSON.stringify({ userId: 2, productId: '60f7c2b5e1d3c2a1b8e4d123', quantity: 1 });
    return http.post(`${baseUrl}/orders/`, body,
        { headers: { 'Content-Type': 'application/json' }, tags: { name: 'create order' } });
}

export function checkStatus(res, ...expected) {
    check(res, { [`status ${expected.join(' o ')}`]: (r) => expected.includes(r.status) });
}

// Facilidad de despliegue: la entrada del gateway hacia las dos versiones de
// Users, en el puerto 8091.
export const DEPLOY = __ENV.DEPLOY || GATEWAY.replace(/:\d+$/, '') + ':8091';

// Cuántas respuestas dio cada versión de Users, y con qué código HTTP. La versión
// la informa Users en el encabezado X-Version: sin él, respondió el gateway,
// porque la solicitud no llegó a ninguna instancia.
export const deployRequests = new Counter('deploy_requests');

// getUser(1): GetUser por la entrada de despliegue, contado por versión. Con
// headers se agregan encabezados, como X-Segment para A/B Testing, y con tags,
// etiquetas a la métrica.
export function getUser(id, headers = {}, tags = {}) {
    const envelope = '<soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"><soap:Body>'
        + `<GetUser xmlns="http://ucu.edu.uy/andis/users"><id>${id}</id></GetUser></soap:Body></soap:Envelope>`;
    const res = http.post(`${DEPLOY}/users/UserService.svc`, envelope, {
        headers: Object.assign({
            'Content-Type': 'text/xml; charset=utf-8',
            SOAPAction: '"http://ucu.edu.uy/andis/users/IUserService/GetUser"',
        }, headers),
        tags: { name: 'deploy users' },
        timeout: '2s',
    });
    deployRequests.add(1, Object.assign({
        version: res.headers['X-Version'] || 'ninguna',
        // La variante del experimento de A/B Testing: solo la trae la versión 2.
        variant: res.headers['X-Variant'] || 'ninguna',
        status: String(res.status),
    }, tags));
    checkStatus(res, 200);
    return res;
}

// Una carga constante de GetUser del User 1 por la entrada de despliegue, para
// los escenarios de Blue/Green, Rolling Upgrade y Canary.
export function deployOptions(rate, duration) {
    return {
        scenarios: {
            reads: {
                executor: 'constant-arrival-rate',
                rate,
                timeUnit: '1s',
                duration: __ENV.DURATION || duration,
                preAllocatedVUs: 20,
            },
        },
    };
}
