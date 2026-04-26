// Migration 0011: Add the "Annotate" operation to enrichment-collection validators
//
// Holocron's v1 originally permitted Add / Augment / FillGap. Annotate was added
// as the compromise to the "no synonym edges between connected pairs" rule —
// when an edge already exists with a correct canonical label but the agent has
// richer context to attach (role, qualifier, description), Annotate lets it
// supplement the existing edge instead of proposing a parallel one.
//
// This migration reapplies the validators (loaded from lib/validators.js with
// the updated enum) so newly-written enrichments with operation: "Annotate"
// pass validation cleanly. Validators stay in moderate/warn mode — this is a
// schema expansion, not a tightening, so no existing data is affected.
//
// Idempotent: collMod replaces the validator definition each run.
const { nodeEnrichmentValidator, edgeEnrichmentValidator } = globalThis.__validators;

globalThis.__currentMigration = {
  id: "0011-add-annotate-operation",
  description: "Reapply kg.enrichments + kg.edge_enrichments validators with Annotate operation",

  up(db) {
    const results = {};

    for (const [collName, validator] of [["kg.enrichments", nodeEnrichmentValidator], ["kg.edge_enrichments", edgeEnrichmentValidator]]) {
      const r = db.runCommand({
        collMod: collName,
        validator,
        validationLevel: "moderate",
        validationAction: "warn",
      });
      print(`    collMod ${collName}: ok=${r.ok}`);
      results[collName] = { ok: r.ok };
    }

    return results;
  },
};
