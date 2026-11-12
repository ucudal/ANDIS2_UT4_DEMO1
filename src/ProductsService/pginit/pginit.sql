-- La base productsdb y el usuario productsusr los crea la imagen de Postgres a
-- partir de POSTGRES_DB y POSTGRES_USER; este script corre ya conectado a
-- productsdb.
--
-- Saga: 'stock' son las unidades disponibles. Las reservas de las Orders lo
-- descuentan, y las compensaciones lo devuelven -ver StockReservations.cs-.
-- La restricción impide que dos reservas a la vez lo dejen negativo.
CREATE TABLE products (
    id INT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name VARCHAR(100) NOT NULL,
    price DOUBLE PRECISION NOT NULL,
    stock INT NOT NULL DEFAULT 0 CHECK (stock >= 0)
);

-- Sin id explícito: la identidad los numera 1, 2 y 3, y los POST posteriores
-- siguen desde ahí. El stock alcanza para las demos y las pruebas de carga,
-- que crean cientos de Orders; la demo de la Saga lo baja para ver qué pasa
-- cuando falta.
INSERT INTO products (name, price, stock) VALUES
    ('Camisa', 1200.50, 100000),
    ('Pantalón', 2300.00, 100000),
    ('Zapatos', 3500.75, 100000);

-- Saga: una reserva por Order. 'Reserved' descontó el stock; 'Rejected' no
-- descontó nada, porque faltaba stock o el Product no existe; 'Released'
-- devolvió el stock de una Order rechazada o cancelada.
--
-- Idempotencia: la clave primaria es la Order. Si order.created llega dos
-- veces, la segunda inserción falla, y con ella se deshace el descuento del
-- stock, que está en la misma transacción. No hay clave foránea hacia
-- 'products': una reserva rechazada puede ser de un Product que no existe.
CREATE TABLE stock_reservations (
    order_id INT PRIMARY KEY,
    product_id INT NOT NULL,
    quantity INT NOT NULL,
    status VARCHAR(20) NOT NULL,
    reason VARCHAR(200) NULL,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL
);

-- Transactional Outbox: los eventos de la Saga, pendientes de publicar en
-- RabbitMQ. Se escriben en la misma transacción que la reserva y el stock;
-- OutboxRelay los publica y completa 'published_at'.
CREATE TABLE outbox_messages (
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    routing_key VARCHAR(100) NOT NULL,
    payload TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    published_at TIMESTAMPTZ NULL
);
CREATE INDEX ix_outbox_messages_published_at ON outbox_messages (published_at);
