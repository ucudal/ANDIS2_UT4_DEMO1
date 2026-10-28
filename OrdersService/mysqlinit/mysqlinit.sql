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
