CREATE TABLE IF NOT EXISTS orders (
    id INT AUTO_INCREMENT PRIMARY KEY,
    user_id INT NOT NULL,
    product_id VARCHAR(64) NOT NULL,
    quantity INT NOT NULL
);

INSERT INTO orders (user_id, product_id, quantity) VALUES
  (1, '60f7c2b5e1d3c2a1b8e4d123', 2),
  (2, '60f7c2b5e1d3c2a1b8e4d124', 1),
  (1, '60f7c2b5e1d3c2a1b8e4d125', 5);