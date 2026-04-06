// Apply moderate/warn JSON-Schema validators to kg.edges and kg.nodes.
// - moderate: only newly-matching documents are validated; existing rows are not blocked
// - warn:     violations are logged, not rejected
//
// Usage:
//   mongosh "$MDB_MCP_CONNECTION_STRING" --quiet --file eng/scripts/apply-kg-validators.mongodb.js
//   mongosh "$MDB_MCP_CONNECTION_STRING" --quiet --eval 'STARWARS_DB="starwars"' --file eng/scripts/apply-kg-validators.mongodb.js

const DB_NAME = typeof STARWARS_DB !== "undefined" ? STARWARS_DB : "starwars-dev";
print(`Using database: ${DB_NAME}`);
const db = db.getSiblingDB(DB_NAME);

const realmEnum = ["Starwars", "Real", "Unknown"];

const edgeValidator = {
    $jsonSchema: {
        bsonType: "object",
        required: [
            "fromId", "fromName", "fromType",
            "toId",   "toName",   "toType",
            "label",  "weight",   "continuity",
            "sourcePageId", "createdAt",
        ],
        properties: {
            fromId:       { bsonType: "int", minimum: 1, description: "source PageId — must be > 0" },
            fromName:     { bsonType: "string", minLength: 1 },
            fromType:     { bsonType: "string", minLength: 1 },
            fromRealm:    { enum: realmEnum, description: "denormalized realm from source node" },
            toId:         { bsonType: "int", minimum: 1, description: "target PageId — must be > 0; enforces Phase-1 filter" },
            toName:       { bsonType: "string", minLength: 1 },
            toType:       { bsonType: "string" }, // allow "" — ETL emits empty until toType-resolution pass
            toRealm:      { enum: realmEnum, description: "denormalized realm from target node" },
            label:        { bsonType: "string", minLength: 1 },
            reverseLabel: { bsonType: ["string", "null"], description: "reverse form of label from FieldSemantics; null if no registered reverse" },
            weight:       { bsonType: "double", minimum: 0, maximum: 1 },
            evidence:     { bsonType: "string" },
            sourcePageId: { bsonType: "int", minimum: 1 },
            continuity:   { enum: ["Canon", "Legends", "Both", "Unknown"] },
            createdAt:    { bsonType: "date" },
            fromYear:     { bsonType: ["int", "null"] },
            toYear:       { bsonType: ["int", "null"] },
            pairId:       { bsonType: ["objectId", "null"], description: "LLM dual-write pair link; null on deterministic edges" },
            meta: {
                bsonType: "object",
                additionalProperties: false,
                properties: {
                    qualifier: { bsonType: ["string", "null"] },
                    rawValue:  { bsonType: ["string", "null"] },
                    order:     { bsonType: ["int", "null"] },
                },
            },
        },
    },
};

const nodeValidator = {
    $jsonSchema: {
        bsonType: "object",
        required: ["_id", "name", "type", "continuity", "realm", "processedAt"],
        properties: {
            _id:           { bsonType: "int", minimum: 1 },
            name:          { bsonType: "string", minLength: 1 },
            type:          { bsonType: "string", minLength: 1 },
            continuity:    { enum: ["Canon", "Legends", "Both", "Unknown"] },
            realm:         { enum: realmEnum },
            properties:    { bsonType: "object" },
            imageUrl:      { bsonType: ["string", "null"] },
            wikiUrl:       { bsonType: "string" },
            startYear:     { bsonType: ["int", "null"] },
            endYear:       { bsonType: ["int", "null"] },
            startDateText: { bsonType: ["string", "null"] },
            endDateText:   { bsonType: ["string", "null"] },
            temporalFacets: {
                bsonType: "array",
                items: {
                    bsonType: "object",
                    required: ["field", "semantic", "calendar", "text"],
                    properties: {
                        field:    { bsonType: "string" },
                        semantic: { bsonType: "string" },
                        calendar: { enum: ["galactic", "real", "unknown"] },
                        year:     { bsonType: ["int", "null"] },
                        text:     { bsonType: "string" },
                        order:    { bsonType: ["int", "null"] },
                    },
                },
            },
            lineages:      { bsonType: "object", description: "precomputed transitive closures for tree/DAG labels" },
            contentHash:   { bsonType: ["string", "null"] },
            processedAt:   { bsonType: "date" },
        },
    },
};

function applyValidator(collName, validator) {
    const result = db.runCommand({
        collMod: collName,
        validator,
        validationLevel: "moderate",
        validationAction: "warn",
    });
    print(`collMod ${collName}: ok=${result.ok}`);

    const nonCompliant = db[collName].countDocuments({ $nor: [{ $jsonSchema: validator.$jsonSchema }] });
    const total = db[collName].estimatedDocumentCount();
    print(`  ${collName}: ${nonCompliant} / ${total} existing docs fail new validator`);
}

applyValidator("kg.edges", edgeValidator);
applyValidator("kg.nodes", nodeValidator);

print("done.");
