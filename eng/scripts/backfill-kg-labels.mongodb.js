// One-shot backfill of kg.labels as a materialized view over kg.edges.
// Mirrors InfoboxGraphService.BuildLabelRegistryAsync but operates directly in MongoDB
// so we don't need to run the full Phase 5 ETL just to populate the label registry.
// The `reverse` and `description` fields stay empty from this backfill and will be
// filled from FieldSemantics on the next C# ETL run.
//
// Usage:
//   mongosh "$MDB_MCP_CONNECTION_STRING" --quiet --file eng/scripts/backfill-kg-labels.mongodb.js
//   mongosh "$MDB_MCP_CONNECTION_STRING" --quiet --eval 'STARWARS_DB="starwars"' --file eng/scripts/backfill-kg-labels.mongodb.js

const DB_NAME = typeof STARWARS_DB !== "undefined" ? STARWARS_DB : "starwars-dev";
print(`Using database: ${DB_NAME}`);
const db = db.getSiblingDB(DB_NAME);

print("Dropping existing kg.labels...");
db["kg.labels"].drop();

print("Aggregating kg.edges into kg.labels...");
const now = new Date();

db["kg.edges"].aggregate([
    {
        $group: {
            _id: "$label",
            usageCount: { $sum: 1 },
            fromTypes:  { $addToSet: "$fromType" },
            toTypes:    { $addToSet: "$toType" },
        },
    },
    {
        $project: {
            _id: 1,
            reverse:     { $literal: "" },
            description: { $literal: "" },
            fromTypes:   { $filter: { input: { $sortArray: { input: "$fromTypes", sortBy: 1 } }, cond: { $ne: ["$$this", ""] } } },
            toTypes:     { $filter: { input: { $sortArray: { input: "$toTypes",   sortBy: 1 } }, cond: { $ne: ["$$this", ""] } } },
            usageCount:  1,
            createdAt:   { $literal: now },
        },
    },
    {
        $merge: {
            into: "kg.labels",
            on: "_id",
            whenMatched: "replace",
            whenNotMatched: "insert",
        },
    },
]).toArray();

// MongoDB can't compound-index two parallel arrays — use separate indexes.
db["kg.labels"].createIndex({ usageCount: -1 }, { name: "ix_usageCount" });
db["kg.labels"].createIndex({ fromTypes: 1 },   { name: "ix_fromTypes" });
db["kg.labels"].createIndex({ toTypes: 1 },     { name: "ix_toTypes" });

const total = db["kg.labels"].countDocuments({});
const top = db["kg.labels"].find({}).sort({ usageCount: -1 }).limit(5).toArray();
print(`kg.labels now has ${total} labels`);
print("Top 5 by usage:");
top.forEach(l => print(`  ${l._id.padEnd(25)} ${l.usageCount.toString().padStart(7)}`));
print("done.");
