// Blue/Green Deployment: una carga constante de GetUser por la entrada de
// despliegue. Lo lanza demos/load-deployment.sh, que a mitad de camino conmuta
// de la versión 1 a la 2, y después vuelve atrás.
//
// Qué mirar en Grafana: las solicitudes pasan de una versión a la otra de una
// vez, en cada conmutación, y las respuestas con error siguen en cero.
import { deployOptions, getUser } from './lib.js';

export const options = deployOptions(20, '60s');

export default function () {
    getUser(1);
}
