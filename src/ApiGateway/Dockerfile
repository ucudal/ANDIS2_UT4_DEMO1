FROM nginx:1.25-alpine

# Gateway Offloading: el certificado TLS solo lo tiene el gateway. Es
# autofirmado y se genera al construir la imagen, para no guardar una clave
# privada en el repositorio; en producción lo emitiría una autoridad de
# certificación, por ejemplo Let's Encrypt.
RUN apk add --no-cache openssl \
    && mkdir -p /etc/nginx/tls \
    && openssl req -x509 -newkey rsa:2048 -nodes -days 365 \
        -subj "/CN=localhost" \
        -addext "subjectAltName=DNS:localhost,IP:127.0.0.1" \
        -keyout /etc/nginx/tls/gateway.key -out /etc/nginx/tls/gateway.crt \
    && apk del openssl

COPY ApiGateway/nginxconf/nginx.conf /etc/nginx/nginx.conf
COPY ApiGateway/nginxconf/identity.js /etc/nginx/identity.js
COPY ApiGateway/nginxconf/strangler-routes.conf /etc/nginx/strangler-routes.conf
COPY ApiGateway/nginxconf/deployment-routes.conf /etc/nginx/deployment-routes.conf
