// Canary Testing: una carga constante de GetUser por la entrada de despliegue.
// Lo lanza demos/load-deployment.sh, que manda a la versión 2 el 10% y después
// el 50% de las solicitudes, le inyecta fallas y vuelve a la versión 1.
//
// Qué mirar en Grafana: la proporción de solicitudes de la versión 2 sigue al
// porcentaje del canario. Cuando la versión 2 falla, los errores son solo los
// suyos, una fracción del total, hasta que se vuelve atrás.
import { deployOptions, getUser } from './lib.js';

export const options = deployOptions(20, '90s');

export default function () {
    getUser(1);
}
