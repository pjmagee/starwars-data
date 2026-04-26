// Migration 0009: Create the Phase 2 (Holocron) enrichment collections
//
//   kg.enrichments       — node-property-level agent additions
//   kg.edge_enrichments  — edge-level agent additions
//   kg.events            — append-only audit log
//
// Each collection is created with a $jsonSchema validator (loaded from
// lib/validators.js by the runner) and a small set of indexes that match the
// query patterns called out in eng/design/018-kg-enrichments-architecture.md.
//
// Validation level matches the kg.edges/kg.nodes pattern: moderate/warn —
// only newly-written documents are validated, violations are logged not
// rejected. We can tighten to error later once the agent's output stabilises.
//
// Idempotent: safe to re-run; createCollection is wrapped in try/catch and
// collMod replaces any existing validator. Indexes use createIndex which is
// itself idempotent.
const {
  nodeEnrichmentValidator,
  edgeEnrichmentValidator,
  holocronEventValidator,
} = globalThis.__validators;

globalThis.__currentMigration = {
  id: "0009-create-kg-enrichment-collections",
  description: "Create kg.enrichments / kg.edge_enrichments / kg.events with $jsonSchema validators (Design-018)",

  up(db) {
    const results = {};

    const collections = [
      ["kg.enrichments", nodeEnrichmentValidator],
      ["kg.edge_enrichments", edgeEnrichmentValidator],
      ["kg.events", holocronEventValidator],
    ];

    for (const [collName, validator] of collections) {
      // Create the collection if missing. createCollection throws when the
      // collection already exists; collMod handles that path.
      try {
        db.createCollection(collName, {
          validator,
          validationLevel: "moderate",
          validationAction: "warn",
        });
        print(`    Created ${collName} with validator.`);
      } catch (err) {
        if (String(err).includes("already exists")) {
          db.runCommand({
            collMod: collName,
            validator,
            validationLevel: "moderate",
            validationAction: "warn",
          });
          print(`    Updated validator on existing ${collName}.`);
        } else {
          throw err;
        }
      }

      const failing = db.getCollection(collName).countDocuments({
        $nor: [{ $jsonSchema: validator.$jsonSchema }],
      });
      const total = db.getCollection(collName).estimatedDocumentCount();
      results[collName] = { failing, total };
    }

    // ── Indexes for kg.enrichments ──────────────────────────────────────
    // Read view filters by (pageId, status="active"). Status sweep filters
    // by status alone. Changelog page (Stage D) sorts by createdAt desc.
    db.getCollection("kg.enrichments").createIndex(
      { pageId: 1, fieldPath: 1, status: 1 },
      { name: "ix_pageId_fieldPath_status" }
    );
    db.getCollection("kg.enrichments").createIndex(
      { status: 1 },
      { name: "ix_status" }
    );
    db.getCollection("kg.enrichments").createIndex(
      { createdAt: -1 },
      { name: "ix_createdAt_desc" }
    );

    // ── Indexes for kg.edge_enrichments ─────────────────────────────────
    // Read view joins on (fromId, toId, label). Status sweep filters by status.
    db.getCollection("kg.edge_enrichments").createIndex(
      { fromId: 1, toId: 1, label: 1, status: 1 },
      { name: "ix_fromId_toId_label_status" }
    );
    db.getCollection("kg.edge_enrichments").createIndex(
      { status: 1 },
      { name: "ix_status" }
    );
    db.getCollection("kg.edge_enrichments").createIndex(
      { createdAt: -1 },
      { name: "ix_createdAt_desc" }
    );

    // ── Indexes for kg.events ───────────────────────────────────────────
    // Frontend changelog paginates by occurredAt desc and filters by pageId
    // or eventType. Append-only, never updated, so no other shapes needed.
    db.getCollection("kg.events").createIndex(
      { occurredAt: -1 },
      { name: "ix_occurredAt_desc" }
    );
    db.getCollection("kg.events").createIndex(
      { pageId: 1, occurredAt: -1 },
      { name: "ix_pageId_occurredAt_desc" }
    );
    db.getCollection("kg.events").createIndex(
      { eventType: 1, occurredAt: -1 },
      { name: "ix_eventType_occurredAt_desc" }
    );

    print(`    Indexes created on all three collections.`);
    return results;
  },
};
