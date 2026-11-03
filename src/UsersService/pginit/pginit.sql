-- La base usersdb y el usuario usersusr los crea la imagen de Postgres a partir
-- de POSTGRES_DB y POSTGRES_USER; este script corre ya conectado a usersdb.
CREATE TABLE users (
    id SERIAL PRIMARY KEY,
    name VARCHAR(100) NOT NULL,
    email VARCHAR(100) UNIQUE NOT NULL
);

-- Sin id explícito, para que la secuencia del SERIAL avance y los POST
-- posteriores no choquen con los ids existentes.
INSERT INTO users (name, email) VALUES
    ('alice', 'alice@example.com'),
    ('bob', 'bob@example.com');
