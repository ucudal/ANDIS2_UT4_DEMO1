CREATE DATABASE usersdb;

ALTER USER usersusr WITH ENCRYPTED PASSWORD 'Pa55w0rd';
GRANT ALL PRIVILEGES ON DATABASE usersdb TO usersusr;

\c usersdb;
CREATE TABLE users (
    id SERIAL PRIMARY KEY,
    name VARCHAR(100) NOT NULL,
    email VARCHAR(100) UNIQUE NOT NULL
);
INSERT INTO users (id, name, email) VALUES
    (1, 'alice', 'alice@example.com'),
    (2, 'bob', 'bob@example.com')
ON CONFLICT (id) DO NOTHING;