// Verify validator compliance on kg.edges and kg.nodes.
// Read-only diagnostic — does not modify any data.
//
// Usage:
//   mongosh "$MDB_MCP_CONNECTION_STRING" --quiet --file src/StarWarsData.MongoDbMigrations/utils/verify-validators.js
//   mongosh "$MDB_MCP_CONNECTION_STRING" --quiet --eval 'STARWARS_DB="starwars-prod"' \
//     --file src/StarWarsData.MongoDbMigrations/utils/verify-validators.js

"use strict";

const DB_NAME = typeof STARWARS_DB !== "undefined" ? STARWARS_DB
  : (process.env.STARWARS_DB || "starwars-dev");

print(`Using database: ${DB_NAME}`);
const database = db.getSiblingDB(DB_NAME);

for (const coll of ["kg.edges", "kg.nodes"]) {
  const info = database.getCollectionInfos({ name: coll })[0];
  if (!info?.options?.validator) {
    print(`${coll}: no validator configured`);
    continue;
  }
  const schema = info.options.validator.$jsonSchema;
  const failing = database.getCollection(coll).countDocuments({ $nor: [{ $jsonSchema: schema }] });
  const total = database.getCollection(coll).estimatedDocumentCount();
  print(`${coll}: ${failing}/${total} docs fail (level=${info.options.validationLevel}, action=${info.options.validationAction})`);
}
