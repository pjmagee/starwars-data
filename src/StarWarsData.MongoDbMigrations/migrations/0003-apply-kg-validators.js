// Migration 0003: Apply $jsonSchema validators to kg.edges and kg.nodes
//
// Uses moderate/warn mode:
//   moderate — only newly-written documents are validated; existing rows are not blocked
//   warn     — violations are logged, not rejected
//
// Idempotent: collMod replaces any existing validator.
// Validators are pre-loaded by the runner (globalThis.__validators)
const { edgeValidator, nodeValidator } = globalThis.__validators;

globalThis.__currentMigration = {
  id: "0003-apply-kg-validators",
  description: "Apply $jsonSchema validators to kg.edges and kg.nodes (moderate/warn)",

  up(db) {
    const results = {};

    for (const [collName, validator] of [["kg.edges", edgeValidator], ["kg.nodes", nodeValidator]]) {
      const r = db.runCommand({
        collMod: collName,
        validator,
        validationLevel: "moderate",
        validationAction: "warn",
      });
      print(`    collMod ${collName}: ok=${r.ok}`);

      const failing = db.getCollection(collName).countDocuments({
        $nor: [{ $jsonSchema: validator.$jsonSchema }],
      });
      const total = db.getCollection(collName).estimatedDocumentCount();
      print(`    ${collName}: ${failing}/${total} existing docs fail validator`);
      results[collName] = { ok: r.ok, failing, total };
    }

    return results;
  },
};
