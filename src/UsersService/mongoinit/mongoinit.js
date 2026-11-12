db = db.getSiblingDB('usersdb');

// Los ids de los Users de ejemplo son fijos, para que las demos y los usuarios
// de Keycloak los puedan nombrar. Los Users nuevos reciben un ObjectId que
// genera MongoDB.
db.users.insertMany([
  { _id: ObjectId("665f1c2ab4e8d9a0c1f2e301"), name: "alice", email: "alice@example.com" },
  { _id: ObjectId("665f1c2ab4e8d9a0c1f2e302"), name: "bob", email: "bob@example.com" }
]);

// El email es único: CreateUser y UpdateUser lo verifican antes de guardar, y
// el índice evita que dos solicitudes simultáneas lo repitan.
db.users.createIndex({ email: 1 }, { unique: true });

db.createUser({
  user: "usersusr",
  pwd: "Pa55w0rd",
  roles: [{ role: "readWrite", db: "usersdb" }]
});
