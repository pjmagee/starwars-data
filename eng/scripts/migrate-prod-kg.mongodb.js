// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
// Production KG migration — PRE-FLIGHT checks and cleanup.
//
// Run BEFORE triggering Phase 5 ETL on production.
// Phase 5 (`InfoboxGraphService.BuildGraphAsync`) is a full delete+insert that will
// rebuild nodes, edges, indexes, kg.labels, and kg.edges.bidir from scratch.
// This script handles the things that need to happen BEFORE or ALONGSIDE that:
//   1. Drop stale pairId field from pre-Phase-5 edges (optional cleanup)
//   2. Drop stale indexes that Phase 5 will NOT re-create
//   3. Apply updated $jsonSchema validators in moderate/warn mode
//   4. Backfill kg.labels as a stopgap (Phase 5 will overwrite with richer data)
//
// Usage:
//   mongosh "$MDB_MCP_CONNECTION_STRING" --quiet --file eng/scripts/migrate-prod-kg.mongodb.js
//   mongosh "$MDB_MCP_CONNECTION_STRING" --quiet --eval 'DRY_RUN=true' --file eng/scripts/migrate-prod-kg.mongodb.js
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

const isDryRun = typeof DRY_RUN !== "undefined" && DRY_RUN;
const db = db.getSiblingDB("starwars");

function run(label, fn) {
    if (isDryRun) {
        print(`[DRY RUN] ${label}`);
    } else {
        print(`[EXEC] ${label}`);
        fn();
    }
}

print("━━━ Production KG Migration Pre-Flight ━━━");
print(`Mode: ${isDryRun ? "DRY RUN (no writes)" : "LIVE"}`);
print(`Database: starwars`);
print(`kg.edges: ${db["kg.edges"].estimatedDocumentCount()} docs`);
print(`kg.nodes: ${db["kg.nodes"].estimatedDocumentCount()} docs`);
print();

// ── Step 1: Drop stale fields on old edges ──────────────────────────────────
// Prod edges still carry pairId: null on all 584K rows. Phase 5 full-insert
// won't carry this field (BsonIgnoreIfNull), but if we want to run validators
// before Phase 5 completes, the field will cause noise. This is optional —
// Phase 5's DeleteMany+InsertMany will wipe it anyway.

const pairIdCount = db["kg.edges"].countDocuments({ pairId: { $exists: true } });
print(`Step 1: ${pairIdCount} edges have stale pairId field`);
run("Unset pairId on all edges", () => {
    const r = db["kg.edges"].updateMany(
        { pairId: { $exists: true } },
        { $unset: { pairId: "" } }
    );
    print(`  Modified: ${r.modifiedCount}`);
});
print();

// ── Step 2: Drop stale indexes ──────────────────────────────────────────────
// Prod has old-shape indexes (fromId_1, toId_1 as standalone) that Phase 5
// will NOT re-create (they are now covered by compound prefixes). These are
// harmless but waste write overhead. Drop them so Phase 5 starts clean.

const staleIndexes = ["fromId_1", "toId_1"];
const currentIndexes = db["kg.edges"].getIndexes().map(i => i.name);
for (const idx of staleIndexes) {
    if (currentIndexes.includes(idx)) {
        run(`Drop stale index kg.edges.${idx}`, () => {
            db["kg.edges"].dropIndex(idx);
            print(`  Dropped ${idx}`);
        });
    } else {
        print(`Step 2: Index ${idx} already absent — skip`);
    }
}
print();

// ── Step 3: Apply updated $jsonSchema validators ────────────────────────────
// These are in moderate/warn mode. They won't block the old-schema documents
// that are currently in prod, but will validate new Phase-5-written documents
// against the current model schema (fromRealm, toRealm, reverseLabel, realm,
// lineages, meta). This means once Phase 5 runs, all new docs will be validated.
//
// NOTE: If you prefer to apply validators AFTER Phase 5, skip this step and
// run `scratch/apply-kg-validators.mongodb.js` with `--eval 'var dbName="starwars"'` instead.

run("Apply kg.edges validator (moderate/warn)", () => {
    const realmEnum = ["Starwars", "Real", "Unknown"];
    db.runCommand({
        collMod: "kg.edges",
        validator: { $jsonSchema: {
            bsonType: "object",
            required: ["fromId","fromName","fromType","toId","toName","toType","label","weight","continuity","sourcePageId","createdAt"],
            properties: {
                fromId:       { bsonType: "int", minimum: 1 },
                fromName:     { bsonType: "string", minLength: 1 },
                fromType:     { bsonType: "string", minLength: 1 },
                fromRealm:    { enum: realmEnum },
                toId:         { bsonType: "int", minimum: 1 },
                toName:       { bsonType: "string", minLength: 1 },
                toType:       { bsonType: "string" },
                toRealm:      { enum: realmEnum },
                label:        { bsonType: "string", minLength: 1 },
                reverseLabel: { bsonType: ["string", "null"] },
                weight:       { bsonType: "double", minimum: 0, maximum: 1 },
                evidence:     { bsonType: "string" },
                sourcePageId: { bsonType: "int", minimum: 1 },
                continuity:   { enum: ["Canon","Legends","Both","Unknown"] },
                createdAt:    { bsonType: "date" },
                fromYear:     { bsonType: ["int","null"] },
                toYear:       { bsonType: ["int","null"] },
                pairId:       { bsonType: ["objectId","null"] },
                meta:         { bsonType: "object", additionalProperties: false, properties: {
                    qualifier: { bsonType: ["string","null"] },
                    rawValue:  { bsonType: ["string","null"] },
                    order:     { bsonType: ["int","null"] },
                }},
            },
        }},
        validationLevel: "moderate",
        validationAction: "warn",
    });
    print("  Applied.");
});

run("Apply kg.nodes validator (moderate/warn)", () => {
    const realmEnum = ["Starwars", "Real", "Unknown"];
    db.runCommand({
        collMod: "kg.nodes",
        validator: { $jsonSchema: {
            bsonType: "object",
            required: ["_id","name","type","continuity","realm","processedAt"],
            properties: {
                _id:           { bsonType: "int", minimum: 1 },
                name:          { bsonType: "string", minLength: 1 },
                type:          { bsonType: "string", minLength: 1 },
                continuity:    { enum: ["Canon","Legends","Both","Unknown"] },
                realm:         { enum: realmEnum },
                properties:    { bsonType: "object" },
                imageUrl:      { bsonType: ["string","null"] },
                wikiUrl:       { bsonType: "string" },
                startYear:     { bsonType: ["int","null"] },
                endYear:       { bsonType: ["int","null"] },
                startDateText: { bsonType: ["string","null"] },
                endDateText:   { bsonType: ["string","null"] },
                temporalFacets: { bsonType: "array", items: { bsonType: "object",
                    required: ["field","semantic","calendar","text"],
                    properties: {
                        field:    { bsonType: "string" },
                        semantic: { bsonType: "string" },
                        calendar: { enum: ["galactic","real","unknown"] },
                        year:     { bsonType: ["int","null"] },
                        text:     { bsonType: "string" },
                        order:    { bsonType: ["int","null"] },
                    },
                }},
                lineages:      { bsonType: "object" },
                contentHash:   { bsonType: ["string","null"] },
                processedAt:   { bsonType: "date" },
            },
        }},
        validationLevel: "moderate",
        validationAction: "warn",
    });
    print("  Applied.");
});
print();

// ── Step 4: Backfill kg.labels (stopgap) ────────────────────────────────────
// Creates the label registry from the current (old-schema) edges. Phase 5 will
// overwrite this with richer data (reverse/description from FieldSemantics).

const labelsExist = db["kg.labels"].estimatedDocumentCount();
print(`Step 4: kg.labels has ${labelsExist} docs currently`);

run("Backfill kg.labels from kg.edges", () => {
    db["kg.labels"].drop();
    db["kg.edges"].aggregate([
        { $group: { _id: "$label", usageCount: { $sum: 1 }, fromTypes: { $addToSet: "$fromType" }, toTypes: { $addToSet: "$toType" } } },
        { $project: {
            _id: 1,
            reverse: { $literal: "" },
            description: { $literal: "" },
            fromTypes: { $filter: { input: { $sortArray: { input: "$fromTypes", sortBy: 1 } }, cond: { $ne: ["$$this",""] } } },
            toTypes:   { $filter: { input: { $sortArray: { input: "$toTypes",   sortBy: 1 } }, cond: { $ne: ["$$this",""] } } },
            usageCount: 1,
            createdAt: { $literal: new Date() },
        }},
        { $merge: { into: "kg.labels", on: "_id", whenMatched: "replace", whenNotMatched: "insert" } },
    ]).toArray();
    db["kg.labels"].createIndex({ usageCount: -1 }, { name: "ix_usageCount" });
    db["kg.labels"].createIndex({ fromTypes: 1 },   { name: "ix_fromTypes" });
    db["kg.labels"].createIndex({ toTypes: 1 },     { name: "ix_toTypes" });
    const n = db["kg.labels"].countDocuments({});
    print(`  kg.labels now has ${n} labels`);
});
print();

// ── Summary ─────────────────────────────────────────────────────────────────
print("━━━ Pre-Flight Complete ━━━");
print();
print("Next steps:");
print("  1. Deploy the updated code (Phase 5 ETL: InfoboxGraphService.BuildGraphAsync)");
print("  2. Trigger Phase 5 via Aspire HTTP command or Admin dashboard");
print("     Phase 5 will:");
print("       - Full delete+insert of kg.nodes and kg.edges");
print("       - Populate all new fields: realm, lineages, fromRealm, toRealm, reverseLabel, fromYear, toYear, meta");
print("       - Create named indexes (ix_fromId_toId_label unique, ix_fromId_label, ix_toId_label, ix_pairId sparse, etc.)");
print("       - Rebuild kg.labels with reverse/description from FieldSemantics");
print("       - Create/replace kg.edges.bidir view");
print("  3. After Phase 5 completes, verify:");
print("     mongosh \"<conn>\" --eval 'var dbName=\"starwars\"' eng/scripts/verify-kg-validators.mongodb.js");
print("  4. When satisfied, switch validators to strict/error:");
print("     Change validationLevel to \"strict\" and validationAction to \"error\" in apply-kg-validators.mongodb.js");
