// Rolling Upgrade: una carga constante de GetUser por la entrada de despliegue.
// Lo lanza demos/load-deployment.sh, que reemplaza de a una las tres instancias
// de la versión 1 por réplicas de la versión 2.
//
// Qué mirar en Grafana: las solicitudes de la versión 2 suben en escalones, uno
// por lote, mientras las de la 1 bajan; las respuestas con error siguen en cero.
import { deployOptions, getUser } from './lib.js';

export const options = deployOptions(20, '120s');

export default function () {
    getUser(1);
}
