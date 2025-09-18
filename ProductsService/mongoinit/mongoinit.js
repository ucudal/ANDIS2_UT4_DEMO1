db = db.getSiblingDB('productsdb');
// db.products.insertMany([
//   { name: "Camisa", price: 1200.50 },
//   { name: "Pantalón", price: 2300.00 },
//   { name: "Zapatos", price: 3500.75 }
// ]);

db.products.insertMany([
  { _id: ObjectId("60f7c2b5e1d3c2a1b8e4d123"), name: "Camisa", price: 1200.50 },
  { _id: ObjectId("60f7c2b5e1d3c2a1b8e4d124"), name: "Pantalón", price: 2300.00 },
  { _id: ObjectId("60f7c2b5e1d3c2a1b8e4d125"), name: "Zapatos", price: 3500.75 }
]);

db.createUser({
  user: "productsusr",
  pwd: "Pa55w0rd",
  roles: [{ role: "readWrite", db: "productsdb" }]
})