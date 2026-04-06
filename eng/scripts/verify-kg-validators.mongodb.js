// Verify validator compliance on kg.edges and kg.nodes.
//
// Usage:
//   mongosh "$MDB_MCP_CONNECTION_STRING" --quiet --file eng/scripts/verify-kg-validators.mongodb.js
//   mongosh "$MDB_MCP_CONNECTION_STRING" --quiet --eval 'STARWARS_DB="starwars"' --file eng/scripts/verify-kg-validators.mongodb.js

const DB_NAME = typeof STARWARS_DB !== "undefined" ? STARWARS_DB : "starwars-dev";
print(`Using database: ${DB_NAME}`);
const db = db.getSiblingDB(DB_NAME);

for (const coll of ["kg.edges", "kg.nodes"]) {
    const info = db.getCollectionInfos({ name: coll })[0];
    if (!info?.options?.validator) {
        print(`${coll}: no validator configured`);
        continue;
    }
    const schema = info.options.validator.$jsonSchema;
    const failing = db[coll].countDocuments({ $nor: [{ $jsonSchema: schema }] });
    const total = db[coll].estimatedDocumentCount();
    print(`${coll}: ${failing}/${total} docs fail (level=${info.options.validationLevel}, action=${info.options.validationAction})`);
}
