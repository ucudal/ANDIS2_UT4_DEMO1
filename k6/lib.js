// Funciones comunes de los escenarios de k6. No se ejecuta directamente.
import http from 'k6/http';
import { check } from 'k6';
import { Rate } from 'k6/metrics';

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

export function resetChaos() {
    for (const service of ['users', 'products', 'orders'])
        http.del(`${GATEWAY}/${service}/chaos`, null, { tags: { name: 'chaos' } });
    console.log('chaos desactivado');
}

// Detalle de las Orders del User 1. El tag name agrupa las métricas por
// endpoint en lugar de por URL.
export function details(userId = 1) {
    const res = http.get(`${GATEWAY}/orders/user/${userId}/details`, { tags: { name: 'details' } });
    if (res.status === 200)
        degraded.add(res.body.includes('no disponible'));
    return res;
}

export function createOrder(baseUrl = GATEWAY) {
    const body = JSON.stringify({ userId: 2, productId: '60f7c2b5e1d3c2a1b8e4d123', quantity: 1 });
    return http.post(`${baseUrl}/orders/`, body,
        { headers: { 'Content-Type': 'application/json' }, tags: { name: 'create order' } });
}

export function checkStatus(res, ...expected) {
    check(res, { [`status ${expected.join(' o ')}`]: (r) => expected.includes(r.status) });
}
