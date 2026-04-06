# StarWarsData.MongoDbMigrations

MongoDB migration runner and utility scripts. Tracks applied migrations in a `migrations` collection to ensure each migration runs exactly once per environment.

## Prerequisites

- [mongosh](https://www.mongodb.com/docs/mongodb-shell/) 2.x+ (ships with MongoDB 7+)
- Connection string in `$MDB_MCP_CONNECTION_STRING`

## Quick start

```bash
# Apply all pending migrations to dev (default)
mongosh "$MDB_MCP_CONNECTION_STRING" --quiet --file src/StarWarsData.MongoDbMigrations/migrate.js

# Show migration status
mongosh "$MDB_MCP_CONNECTION_STRING" --quiet \
  --eval 'ACTION="status"' --file src/StarWarsData.MongoDbMigrations/migrate.js

# Apply to prod
mongosh "$MDB_MCP_CONNECTION_STRING" --quiet \
  --eval 'STARWARS_DB="starwars-prod"' --file src/StarWarsData.MongoDbMigrations/migrate.js

# Dry run (preview without executing)
mongosh "$MDB_MCP_CONNECTION_STRING" --quiet \
  --eval 'DRY_RUN=true' --file src/StarWarsData.MongoDbMigrations/migrate.js

# Run a specific migration
mongosh "$MDB_MCP_CONNECTION_STRING" --quiet \
  --eval 'MIGRATION="0003"' --file src/StarWarsData.MongoDbMigrations/migrate.js
```

## Options

Set via `--eval` (before `--file`) or environment variables:

| Option | Default | Description |
| --- | --- | --- |
| `STARWARS_DB` | `starwars-dev` | Target database name |
| `ACTION` | `migrate` | `migrate` (apply pending) or `status` (show state) |
| `MIGRATION` | *(all)* | Run only migrations matching this prefix (e.g. `"0003"`) |
| `DRY_RUN` | `false` | Preview without executing |

## Node.js support

The runner also works via Node.js (requires `mongodb` package):

```bash
npm install mongodb
MONGODB_URI="mongodb://..." STARWARS_DB=starwars-dev node src/StarWarsData.MongoDbMigrations/migrate.js
```

## Directory layout

```
src/StarWarsData.MongoDbMigrations/
├── migrate.js              Entry point — migration runner
├── lib/
│   └── validators.js       Shared $jsonSchema validator definitions
├── migrations/
│   ├── 0001-*.js           Tracked migrations (deterministic order)
│   ├── 0002-*.js
│   └── ...
└── utils/
    ├── verify-validators.js      Read-only validator compliance check
    └── generate-template-fields.js  Code generation (TemplateFields.g.cs)
```

## How migrations work

### Tracking

Each migration is recorded in the `migrations` collection on success:

```json
{
  "_id": "0003-apply-kg-validators",
  "description": "Apply $jsonSchema validators to kg.edges and kg.nodes",
  "script": "0003-apply-kg-validators.js",
  "appliedAt": "2026-04-06T14:30:00Z",
  "durationMs": 1200,
  "result": { "kg.edges": { "ok": 1 }, "kg.nodes": { "ok": 1 } }
}
```

The `_id` uniqueness constraint prevents double-application.

### Writing a new migration

1. Create `migrations/NNNN-kebab-description.js` (next sequence number):

```js
"use strict";

module.exports = {
  id: "NNNN-kebab-description",
  description: "Human-readable description of what this migration does",

  up(db) {
    // db is the mongosh database object (or Node.js Db).
    // Use db.getCollection("...") to access collections.
    // Use db.runCommand({...}) for admin commands.
    //
    // Return an object with migration-specific results (optional).
    // Throw on failure — the runner will stop and report the error.
    //
    // MUST be idempotent: safe to re-run if tracking write fails.

    return { changed: 42 };
  },
};
```

2. Test against `starwars-dev`:
```bash
mongosh "$MDB_MCP_CONNECTION_STRING" --quiet \
  --eval 'MIGRATION="NNNN"' --file src/StarWarsData.MongoDbMigrations/migrate.js
```

3. Commit the migration file alongside the code change it supports.

### Rules

- **Sequence numbers are monotonically increasing.** Never reuse a number.
- **Migrations must be idempotent.** If the tracking write fails after execution, re-running should produce the same result.
- **No down/rollback scripts.** To revert, write a new forward migration. For data issues, rerun the relevant ETL phase.
- **Target the `STARWARS_DB` variable.** Never hardcode a database name.

## Current migrations

| # | ID | Description | Destructive? |
| --- | --- | --- | --- |
| 0001 | `drop-stale-pairid` | Drop stale pairId field from kg.edges | No |
| 0002 | `drop-redundant-indexes` | Drop standalone fromId_1, toId_1 indexes | No |
| 0003 | `apply-kg-validators` | Apply $jsonSchema validators (moderate/warn) | No |
| 0004 | `backfill-kg-labels` | Materialize kg.labels from kg.edges | Yes (drops+rebuilds) |
| 0005 | `denorm-kg-edges` | Backfill fromRealm, toRealm, reverseLabel | No (additive merge) |
| 0006 | `dedup-kg-edges-and-indexes` | Dedup edges + create authoritative indexes | Yes (if dupes exist) |
| 0007 | `create-kg-bidir-view` | Create kg.edges.bidir bidirectional view | No (view only) |
| 0008 | `populate-kg-lineages` | Compute lineage closures on kg.nodes | No (clears+rebuilds) |

### Dependency order

```
0001  drop stale pairId         (cleanup)
0002  drop redundant indexes    (cleanup)
0003  apply validators          (structural)
0004  backfill kg.labels        (data — needed by 0005)
0005  denorm kg.edges           (data — needs labels, needed by 0006/0007)
0006  dedup + indexes           (data+structural — needs denorm)
0007  bidir view                (structural — needs reverseLabel from 0005)
0008  lineages                  (data — needs nodes + edges)
```

**Note:** Migrations 0004–0008 are alternatives to a full Phase 5 ETL rebuild. If you run Phase 5 via the Admin dashboard, it handles all of these atomically. These migrations exist for patching an existing dataset without triggering a full rebuild.

## Utility scripts

Utility scripts are **not tracked** in the `migrations` collection. They are standalone scripts for ad-hoc tasks.

| Script | Purpose |
| --- | --- |
| `utils/verify-validators.js` | Read-only: report how many docs fail active validators |
| `utils/generate-template-fields.js` | Code generation: regenerate TemplateFields.g.cs from live data |

Run directly:
```bash
mongosh "$MDB_MCP_CONNECTION_STRING" --quiet --file src/StarWarsData.MongoDbMigrations/utils/verify-validators.js
```

## Relationship to ETL pipeline

The ETL pipeline (Phases 1–9, triggered from the Admin dashboard) is the primary data management mechanism. It performs full delete+insert on target collections and is idempotent by design.

Migrations handle **structural changes** that the ETL depends on or that need to run exactly once:
- Validators, indexes, views (structural prerequisites)
- One-time data cleanup (stale fields, redundant indexes)
- Data enrichment patches (alternative to full ETL rebuild)

**Deploy workflow:**
1. Run pending migrations (`migrate.js`)
2. Run relevant ETL phases (Admin dashboard)

See [ADR-005](../../eng/adr/005-mongodb-migration-strategy.md) and [Design-010](../../eng/design/010-mongodb-migration-environment-promotion.md) for the full rationale.
