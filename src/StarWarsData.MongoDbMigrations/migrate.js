// ============================================================================
// StarWars Data — MongoDB Migration Runner
// ============================================================================
//
// Discovers, tracks, and executes ordered migrations against a target database.
// Each migration runs at most once per environment (tracked in `migrations`).
//
// Usage (mongosh):
//   mongosh "$MDB_MCP_CONNECTION_STRING" --quiet --file src/StarWarsData.MongoDbMigrations/migrate.js
//
// Options (set via --eval before --file, or via environment variables):
//   STARWARS_DB  — target database name (default: "starwars-dev")
//   ACTION       — "migrate" (default) | "status"
//   MIGRATION    — run only migrations matching this prefix (e.g. "0003")
//   DRY_RUN      — true to preview without executing (default: false)
//
// Examples:
//   # Apply all pending migrations to dev
//   mongosh "$CONN" --quiet --file src/StarWarsData.MongoDbMigrations/migrate.js
//
//   # Show status for prod
//   mongosh "$CONN" --quiet --eval 'STARWARS_DB="starwars-prod"; ACTION="status"' \
//     --file src/StarWarsData.MongoDbMigrations/migrate.js
//
//   # Dry-run a specific migration
//   mongosh "$CONN" --quiet --eval 'MIGRATION="0003"; DRY_RUN=true' \
//     --file src/StarWarsData.MongoDbMigrations/migrate.js
//
// ============================================================================

// NOTE: This runner uses load() instead of require() to load migration files.
// In mongosh, require()-d modules lose the auto-await behavior for MongoDB
// operations (countDocuments, updateMany, etc. return Promises instead of
// values). load() runs files in the mongosh top-level context where
// auto-await works correctly.

const path = require("path");
const fs = require("fs");

// ── Resolve script directory ────────────────────────────────────────────────
const SCRIPT_DIR = (() => {
  if (typeof __dirname !== "undefined") return __dirname;
  const idx = process.argv.indexOf("--file");
  if (idx >= 0 && process.argv[idx + 1]) {
    return path.dirname(path.resolve(process.argv[idx + 1]));
  }
  return process.cwd();
})();

// ── Resolve options ─────────────────────────────────────────────────────────
function resolveOption(globalName, envName, defaultValue) {
  if (typeof globalThis[globalName] !== "undefined") return globalThis[globalName];
  if (process.env[envName]) return process.env[envName];
  return defaultValue;
}

const DB_NAME    = resolveOption("STARWARS_DB", "STARWARS_DB", "starwars-dev");
const ACTION     = resolveOption("ACTION", "ACTION", "migrate");
const TARGET     = resolveOption("MIGRATION", "MIGRATION", null);
const IS_DRY_RUN = resolveOption("DRY_RUN", "DRY_RUN", false) === true
                || resolveOption("DRY_RUN", "DRY_RUN", "false") === "true";

const TRACKING_COLL = "migrations";

// ── Database connection ─────────────────────────────────────────────────────
const database = db.getSiblingDB(DB_NAME);

// ── Pre-load shared libraries ───────────────────────────────────────────────
// Load via load() so they're available in the mongosh auto-await context.
// Sets globalThis.__validators for use by migration 0003.
load(path.resolve(SCRIPT_DIR, "lib", "validators.js"));

// ── Discover and load migrations via load() ─────────────────────────────────
// load() runs in mongosh's top-level context where auto-await works.
// Each migration file sets globalThis.__currentMigration.
const migrationsDir = path.resolve(SCRIPT_DIR, "migrations");
const migrationFiles = fs.readdirSync(migrationsDir)
  .filter(f => /^\d{4}-.*\.js$/.test(f))
  .sort();

const migrations = [];

for (const f of migrationFiles) {
  globalThis.__currentMigration = null;
  load(path.resolve(migrationsDir, f));
  if (globalThis.__currentMigration) {
    const m = globalThis.__currentMigration;
    if (!m.id || !m.up) {
      throw new Error(`Migration ${f} must set __currentMigration = { id, description, up(db) }`);
    }
    migrations.push({ ...m, file: f });
  } else {
    throw new Error(`Migration ${f} did not register via globalThis.__currentMigration`);
  }
}
globalThis.__currentMigration = null;

// ── Tracking helpers ────────────────────────────────────────────────────────
function getApplied() {
  return database.getCollection(TRACKING_COLL)
    .find({}, { projection: { _id: 1, appliedAt: 1 } })
    .sort({ appliedAt: 1 })
    .toArray();
}

function recordMigration(migration, durationMs, result) {
  database.getCollection(TRACKING_COLL).insertOne({
    _id: migration.id,
    description: migration.description,
    script: migration.file,
    appliedAt: new Date(),
    durationMs,
    result: result || {},
  });
}

// ── Actions ─────────────────────────────────────────────────────────────────

function showStatus() {
  const appliedMap = new Map(getApplied().map(d => [d._id, d]));

  print(`\n${"=".repeat(70)}`);
  print(`  Migration Status  |  ${DB_NAME}`);
  print(`${"=".repeat(70)}`);

  for (const m of migrations) {
    const applied = appliedMap.get(m.id);
    if (applied) {
      const ts = applied.appliedAt.toISOString().replace("T", " ").slice(0, 19);
      print(`  [applied ${ts}]  ${m.id}`);
    } else {
      print(`  [pending              ]  ${m.id}`);
    }
    print(`                           ${m.description}`);
  }

  const pendingCount = migrations.filter(m => !appliedMap.has(m.id)).length;
  print(`\n  ${migrations.length} total, ${migrations.length - pendingCount} applied, ${pendingCount} pending`);
  print(`${"=".repeat(70)}\n`);
}

function runMigrations() {
  const appliedSet = new Set(getApplied().map(d => d._id));
  let pending = migrations.filter(m => !appliedSet.has(m.id));

  if (TARGET) {
    pending = pending.filter(m => m.id.startsWith(TARGET) || m.file.startsWith(TARGET));
    if (pending.length === 0) {
      print(`No pending migration matching "${TARGET}" in ${DB_NAME}.`);
      return;
    }
  }

  if (pending.length === 0) {
    print(`All ${migrations.length} migrations already applied to ${DB_NAME}.`);
    return;
  }

  print(`\n${"=".repeat(70)}`);
  print(`  Running ${pending.length} migration(s) on ${DB_NAME}${IS_DRY_RUN ? "  [DRY RUN]" : ""}`);
  print(`${"=".repeat(70)}\n`);

  let applied = 0;

  for (const m of pending) {
    print(`> ${m.id}`);
    print(`  ${m.description}`);

    if (IS_DRY_RUN) {
      print(`  [DRY RUN] Skipped.\n`);
      continue;
    }

    const t0 = Date.now();
    try {
      const result = m.up(database);
      const elapsed = Date.now() - t0;
      recordMigration(m, elapsed, result);
      applied++;
      print(`  Applied in ${elapsed}ms.\n`);
    } catch (err) {
      const elapsed = Date.now() - t0;
      print(`  FAILED after ${elapsed}ms: ${err.message || err}`);
      print(`  Stopping. Fix the issue and re-run.\n`);
      throw err;
    }
  }

  print(`${"=".repeat(70)}`);
  print(`  ${applied} migration(s) applied to ${DB_NAME}.`);
  print(`${"=".repeat(70)}\n`);
}

// ── Main ────────────────────────────────────────────────────────────────────

print(`Database: ${DB_NAME}${IS_DRY_RUN ? " [DRY RUN]" : ""}`);

switch (ACTION) {
  case "status":
    showStatus();
    break;
  case "migrate":
  case "up":
    runMigrations();
    break;
  default:
    print(`Unknown action: "${ACTION}". Use "migrate" or "status".`);
    break;
}
