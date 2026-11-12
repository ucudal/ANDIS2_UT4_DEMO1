-- La base productsdb y el usuario productsusr los crea la imagen de Postgres a
-- partir de POSTGRES_DB y POSTGRES_USER; este script corre ya conectado a
-- productsdb.
CREATE TABLE products (
    id INT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name VARCHAR(100) NOT NULL,
    price DOUBLE PRECISION NOT NULL
);

-- Sin id explícito: la identidad los numera 1, 2 y 3, y los POST posteriores
-- siguen desde ahí.
INSERT INTO products (name, price) VALUES
    ('Camisa', 1200.50),
    ('Pantalón', 2300.00),
    ('Zapatos', 3500.75);
