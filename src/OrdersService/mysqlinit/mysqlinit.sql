-- Los datos iniciales tienen tildes: se envían en UTF-8.
SET NAMES utf8mb4;

-- Proyección del estado actual de cada orden. Se puede reconstruir a partir de
-- order_events con POST /orders/projections/rebuild.
CREATE TABLE IF NOT EXISTS orders (
    id INT AUTO_INCREMENT PRIMARY KEY,
    user_id CHAR(24) NOT NULL,
    product_id INT NOT NULL,
    quantity INT NOT NULL,
    status VARCHAR(20) NOT NULL DEFAULT 'Pending'
);

-- Event Sourcing: historial inmutable de eventos de cada orden.
--
-- Idempotencia: hoy cada tipo de evento ocurre a lo sumo una vez por orden
-- -'OrderCreated', 'OrderConfirmed', 'OrderRejected' y 'OrderInvoiced'-. El
-- índice único lo garantiza aunque dos réplicas procesen a la vez dos copias
-- del mismo mensaje: la segunda inserción falla en lugar de dejar el evento
-- dos veces en el historial. Si algún día un tipo de evento pudiera repetirse,
-- por ejemplo un cambio de cantidad, este índice dejaría de servir y haría
-- falta otro criterio, como un identificador por mensaje. El índice también
-- cubre las búsquedas por 'order_id'.
CREATE TABLE IF NOT EXISTS order_events (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    order_id INT NOT NULL,
    type VARCHAR(50) NOT NULL,
    data JSON NOT NULL,
    occurred_at DATETIME(6) NOT NULL,
    UNIQUE INDEX ux_order_events_order_id_type (order_id, type)
);

-- Transactional Outbox: eventos pendientes de publicar en RabbitMQ. Se escriben
-- en la misma transacción que orders y order_events; OutboxRelay los publica y
-- completa published_at.
CREATE TABLE IF NOT EXISTS outbox_messages (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    routing_key VARCHAR(100) NOT NULL,
    payload JSON NOT NULL,
    created_at DATETIME(6) NOT NULL,
    published_at DATETIME(6) NULL,
    INDEX ix_outbox_messages_published_at (published_at)
);

-- Materialized View: detalle de cada orden con el nombre del cliente y del
-- producto. Lo mantiene OrderDetailsProjector a partir de los eventos.
-- 'customer_name_at' y 'product_name_at' guardan cuándo cambió cada nombre:
-- con varias réplicas del proyector, un nombre más viejo no pisa a uno más
-- nuevo.
CREATE TABLE IF NOT EXISTS order_details (
    order_id INT PRIMARY KEY,
    user_id CHAR(24) NOT NULL,
    customer_name VARCHAR(100) NULL,
    customer_name_at DATETIME(6) NULL,
    product_id INT NULL,
    product_name VARCHAR(100) NULL,
    product_name_at DATETIME(6) NULL,
    quantity INT NOT NULL,
    status VARCHAR(20) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    INDEX ix_order_details_user_id (user_id)
);

-- Idempotencia: las claves del encabezado Idempotency-Key de POST /orders, con
-- la orden que creó cada una. La clave es de un User -vacío para las
-- solicitudes anónimas-: la clave primaria impide crear dos órdenes con la misma clave,
-- aunque las dos solicitudes lleguen a la vez. No se guarda en 'orders' porque
-- esa tabla es una proyección: POST /orders/projections/rebuild la borra y la
-- reconstruye a partir de 'order_events'. Las claves no vencen en la demo.
CREATE TABLE IF NOT EXISTS idempotency_keys (
    idempotency_key VARCHAR(255) NOT NULL,
    user_id VARCHAR(24) NOT NULL,
    request_hash CHAR(64) NOT NULL,
    order_id INT NOT NULL,
    created_at DATETIME(6) NOT NULL,
    PRIMARY KEY (idempotency_key, user_id)
);

INSERT INTO orders (id, user_id, product_id, quantity, status) VALUES
  (1, '665f1c2ab4e8d9a0c1f2e301', 1, 2, 'Confirmed'),
  (2, '665f1c2ab4e8d9a0c1f2e302', 2, 1, 'Confirmed'),
  (3, '665f1c2ab4e8d9a0c1f2e301', 3, 5, 'Confirmed');

INSERT INTO order_events (order_id, type, data, occurred_at) VALUES
  (1, 'OrderCreated',   '{"UserId": "665f1c2ab4e8d9a0c1f2e301", "ProductId": 1, "Quantity": 2}', NOW(6)),
  (1, 'OrderConfirmed', '{"ProcessedBy": "datos-iniciales"}', NOW(6)),
  (2, 'OrderCreated',   '{"UserId": "665f1c2ab4e8d9a0c1f2e302", "ProductId": 2, "Quantity": 1}', NOW(6)),
  (2, 'OrderConfirmed', '{"ProcessedBy": "datos-iniciales"}', NOW(6)),
  (3, 'OrderCreated',   '{"UserId": "665f1c2ab4e8d9a0c1f2e301", "ProductId": 3, "Quantity": 5}', NOW(6)),
  (3, 'OrderConfirmed', '{"ProcessedBy": "datos-iniciales"}', NOW(6));

-- La vista de las órdenes iniciales, con los nombres de mongoinit.js de Users
-- y pginit.sql de Products: esas órdenes no pasaron por RabbitMQ.
INSERT INTO order_details (order_id, user_id, customer_name, product_id, product_name, quantity, status, updated_at) VALUES
  (1, '665f1c2ab4e8d9a0c1f2e301', 'alice', 1, 'Camisa',   2, 'Confirmed', NOW(6)),
  (2, '665f1c2ab4e8d9a0c1f2e302', 'bob',   2, 'Pantalón', 1, 'Confirmed', NOW(6)),
  (3, '665f1c2ab4e8d9a0c1f2e301', 'alice', 3, 'Zapatos',  5, 'Confirmed', NOW(6));
