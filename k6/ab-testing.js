// A/B Testing: los dos Users consultan su perfil a la versión 2, que trae el
// experimento. La mitad de las solicitudes del User 2 llevan X-Segment: beta,
// como si ese User se hubiera anotado para probar lo nuevo. Lo lanza
// demos/load-deployment.sh, después de desplegar la versión 2.
//
// Qué mirar en Grafana: cuántas veces se mostró cada variante, según las
// métricas de Users. El User 1 cae siempre en la B, el User 2 en la A, salvo
// cuando trae el segmento beta.
import { deployOptions, getUser } from './lib.js';

export const options = deployOptions(20, '60s');

export default function () {
    const user = __ITER % 2 === 0 ? 1 : 2;
    const beta = user === 2 && __ITER % 4 === 3;
    getUser(user, beta ? { 'X-Segment': 'beta' } : {}, { segment: beta ? 'beta' : 'ninguno' });
}
