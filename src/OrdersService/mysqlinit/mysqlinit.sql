-- Los datos iniciales tienen tildes: se envían en UTF-8.
SET NAMES utf8mb4;

-- Proyección del estado actual de cada orden. Se puede reconstruir a partir de
-- order_events con POST /orders/projections/rebuild.
CREATE TABLE IF NOT EXISTS orders (
    id INT AUTO_INCREMENT PRIMARY KEY,
    user_id INT NOT NULL,
    product_id VARCHAR(64) NOT NULL,
    quantity INT NOT NULL,
    status VARCHAR(20) NOT NULL DEFAULT 'Pending'
);

-- Event Sourcing: historial inmutable de eventos de cada orden.
CREATE TABLE IF NOT EXISTS order_events (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    order_id INT NOT NULL,
    type VARCHAR(50) NOT NULL,
    data JSON NOT NULL,
    occurred_at DATETIME(6) NOT NULL,
    INDEX ix_order_events_order_id (order_id)
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
CREATE TABLE IF NOT EXISTS order_details (
    order_id INT PRIMARY KEY,
    user_id INT NOT NULL,
    customer_name VARCHAR(100) NULL,
    product_id VARCHAR(64) NULL,
    product_name VARCHAR(100) NULL,
    quantity INT NOT NULL,
    status VARCHAR(20) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    INDEX ix_order_details_user_id (user_id)
);

INSERT INTO orders (id, user_id, product_id, quantity, status) VALUES
  (1, 1, '60f7c2b5e1d3c2a1b8e4d123', 2, 'Confirmed'),
  (2, 2, '60f7c2b5e1d3c2a1b8e4d124', 1, 'Confirmed'),
  (3, 1, '60f7c2b5e1d3c2a1b8e4d125', 5, 'Confirmed');

INSERT INTO order_events (order_id, type, data, occurred_at) VALUES
  (1, 'OrderCreated',   '{"UserId": 1, "ProductId": "60f7c2b5e1d3c2a1b8e4d123", "Quantity": 2}', NOW(6)),
  (1, 'OrderConfirmed', '{"ProcessedBy": "datos-iniciales"}', NOW(6)),
  (2, 'OrderCreated',   '{"UserId": 2, "ProductId": "60f7c2b5e1d3c2a1b8e4d124", "Quantity": 1}', NOW(6)),
  (2, 'OrderConfirmed', '{"ProcessedBy": "datos-iniciales"}', NOW(6)),
  (3, 'OrderCreated',   '{"UserId": 1, "ProductId": "60f7c2b5e1d3c2a1b8e4d125", "Quantity": 5}', NOW(6)),
  (3, 'OrderConfirmed', '{"ProcessedBy": "datos-iniciales"}', NOW(6));

-- La vista de las órdenes iniciales, con los nombres de pginit.sql y
-- mongoinit.js: esas órdenes no pasaron por RabbitMQ.
INSERT INTO order_details (order_id, user_id, customer_name, product_id, product_name, quantity, status, updated_at) VALUES
  (1, 1, 'alice', '60f7c2b5e1d3c2a1b8e4d123', 'Camisa',   2, 'Confirmed', NOW(6)),
  (2, 2, 'bob',   '60f7c2b5e1d3c2a1b8e4d124', 'Pantalón', 1, 'Confirmed', NOW(6)),
  (3, 1, 'alice', '60f7c2b5e1d3c2a1b8e4d125', 'Zapatos',  5, 'Confirmed', NOW(6));
