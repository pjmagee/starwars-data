# Design: MongoDB Migration & Environment Promotion

**Status:** Implemented
**Date:** 2026-04-06
**Author:** Patrick Magee + Claude
**Companion docs:** [ADR-005](../adr/005-mongodb-migration-strategy.md), [ADR-003](../adr/003-kg-query-architecture.md)

## Problem

The project has two MongoDB environments (`starwars-dev`, `starwars` [prod]) that have diverged structurally. Development introduced new indexes, validators, views, field additions, and collection schema changes that were tracked in ad-hoc `eng/scripts/*.mongodb.js` files but had no formal application path to production. The deployment required the operator to manually piece together which scripts to run, in what order, against which database — with no record of what had already been applied.

Additionally, the production database is named `starwars` while the dev database is `starwars-dev`. The asymmetric naming creates ambiguity — code that defaults to `starwars` silently targets production.

## Goals

1. **Explicit environment naming** — both databases have unambiguous names.
2. **Tracked migrations** — a `migrations` collection records what has been applied.
3. **Idempotent scripts** — every migration is safe to re-run.
4. **Deterministic ordering** — sequence numbers enforce execution order.
5. **CI/CD compatible** — works with `mongosh --file` and Node.js.

## Database Rename: `starwars` → `starwars-prod`

### Procedure

MongoDB does not support renaming a database. The canonical approach is:

1. **Stop writes** — disable Hangfire jobs and drain active ETL phases via the Admin dashboard.
2. **Copy database** — use `mongodump` / `mongorestore`.
3. **Verify** — compare collection counts and spot-check documents.
4. **Update config** — change `DatabaseName` default in `SettingsOptions` and `appsettings.json` overrides.
5. **Deploy** — restart services pointing at `starwars-prod`.
6. **Drop old** — once verified, drop the `starwars` database.

```bash
# Step 2: Copy
mongodump --uri="$MDB_MCP_CONNECTION_STRING" --db=starwars --archive | \
mongorestore --uri="$MDB_MCP_CONNECTION_STRING" --db=starwars-prod --archive --nsFrom="starwars.*" --nsTo="starwars-prod.*"

# Same for Hangfire
mongodump --uri="$MDB_MCP_CONNECTION_STRING" --db=starwars-hangfire --archive | \
mongorestore --uri="$MDB_MCP_CONNECTION_STRING" --db=starwars-prod-hangfire --archive --nsFrom="starwars-hangfire.*" --nsTo="starwars-prod-hangfire.*"
```

### Code changes

| File | Change |
| --- | --- |
| `Settings.cs` | `DatabaseName` default: `"starwars"` → `"starwars-dev"` (dev-safe default) |
| `appsettings.Production.json` (or env var) | Set `Settings:DatabaseName = "starwars-prod"` |
| Docker compose / Aspire env | Update any `STARWARS_DB` or connection string overrides |

## Migration Infrastructure

### Implementation: `src/StarWarsData.MongoDbMigrations/`

All migration scripts and utilities have been consolidated from `eng/scripts/` into a standalone mongosh project:

```text
src/StarWarsData.MongoDbMigrations/
├── migrate.js              Entry point — runner logic
├── lib/
│   └── validators.js       Shared $jsonSchema validator definitions
├── migrations/
│   ├── 0001-drop-stale-pairid.js
│   ├── 0002-drop-redundant-indexes.js
│   ├── 0003-apply-kg-validators.js
│   ├── 0004-backfill-kg-labels.js
│   ├── 0005-denorm-kg-edges.js
│   ├── 0006-dedup-kg-edges-and-indexes.js
│   ├── 0007-create-kg-bidir-view.js
│   └── 0008-populate-kg-lineages.js
└── utils/
    ├── verify-validators.js          Read-only validator compliance check
    └── generate-template-fields.js   Code generation (TemplateFields.g.cs)
```

### Migration module format

Each migration is a CommonJS module exporting `{ id, description, up(db) }`:

```js
module.exports = {
  id: "0001-drop-stale-pairid",
  description: "Drop stale pairId field from kg.edges",
  up(db) {
    // db is the mongosh database object. Must be idempotent.
    const result = db.getCollection("kg.edges").updateMany(
      { pairId: { $exists: true } },
      { $unset: { pairId: "" } }
    );
    return { modifiedCount: result.modifiedCount };
  },
};
```

The runner handles discovery, ordering, tracking, and error handling. Individual migrations do not need guard logic — the runner checks the `migrations` collection before executing each one.

### Tracking collection: `migrations`

```json
{
  "_id": "0001-drop-stale-pairid",
  "description": "Drop stale pairId field from kg.edges",
  "script": "0001-drop-stale-pairid.js",
  "appliedAt": "2026-04-06T14:30:00Z",
  "durationMs": 450,
  "result": { "modifiedCount": 584000 }
}
```

`_id` uniqueness prevents double-application. Each environment tracks its own migration state independently.

### Runner usage

```bash
# Apply all pending migrations to dev
mongosh "$CONN" --quiet --file src/StarWarsData.MongoDbMigrations/migrate.js

# Show status
mongosh "$CONN" --quiet --eval 'ACTION="status"' --file src/StarWarsData.MongoDbMigrations/migrate.js

# Target prod
mongosh "$CONN" --quiet --eval 'STARWARS_DB="starwars-prod"' --file src/StarWarsData.MongoDbMigrations/migrate.js

# Dry run
mongosh "$CONN" --quiet --eval 'DRY_RUN=true' --file src/StarWarsData.MongoDbMigrations/migrate.js

# Run specific migration
mongosh "$CONN" --quiet --eval 'MIGRATION="0003"' --file src/StarWarsData.MongoDbMigrations/migrate.js

# Node.js (CI/CD)
MONGODB_URI="..." STARWARS_DB=starwars-dev node src/StarWarsData.MongoDbMigrations/migrate.js
```

## Initial Migration Set

These 8 migrations capture all structural changes from `eng/scripts/` decomposed into tracked, ordered, idempotent units:

| # | ID | Source | Description | Destructive? |
| --- | --- | --- | --- | --- |
| 0001 | `drop-stale-pairid` | `migrate-prod-kg.mongodb.js` (step 1) | Drop stale pairId field | No |
| 0002 | `drop-redundant-indexes` | `migrate-prod-kg.mongodb.js` (step 2) | Drop standalone fromId_1, toId_1 | No |
| 0003 | `apply-kg-validators` | `apply-kg-validators.mongodb.js` | Apply $jsonSchema (moderate/warn) | No |
| 0004 | `backfill-kg-labels` | `backfill-kg-labels.mongodb.js` | Materialize label registry | Yes (drops+rebuilds) |
| 0005 | `denorm-kg-edges` | `migrate-kg-edges-denorm.mongodb.js` | Backfill fromRealm, toRealm, reverseLabel | No |
| 0006 | `dedup-kg-edges-and-indexes` | `migrate-kg-edges-dedup-and-index.mongodb.js` | Dedup + authoritative indexes | Yes (if dupes) |
| 0007 | `create-kg-bidir-view` | `migrate-kg-bidir-view.mongodb.js` | Bidirectional view (Design-007) | No |
| 0008 | `populate-kg-lineages` | `migrate-kg-lineages.mongodb.js` | Lineage closures (Design-008) | No |

### Dependency order

```text
0001  cleanup: drop stale pairId
0002  cleanup: drop redundant indexes
0003  structural: apply validators
0004  data: backfill kg.labels        (needed by 0005)
0005  data: denorm kg.edges           (needs 0004, needed by 0006/0007)
0006  data+structural: dedup + indexes (needs 0005)
0007  structural: bidir view           (needs reverseLabel from 0005)
0008  data: lineage closures
```

**Note:** Migrations 0004–0008 are alternatives to a full Phase 5 ETL rebuild. Phase 5 handles all of these atomically. These migrations exist for patching existing data without triggering a full rebuild.

## Promotion Workflow: dev → prod

```text
                    starwars-dev                         starwars-prod
                    ────────────                         ─────────────
1. Develop          Write migration in migrations/
                    Test: mongosh --file migrate.js
                    ↓
2. Code review      PR includes migration + code changes
                    ↓
3. Deploy           ──────── deploy code ────────────→   New code running
                    ↓
4. Migrate          ──────── mongosh --file ─────────→   migrate.js applies pending
                    ↓
5. Rebuild          ──────── trigger ETL phases ─────→   Phase 5a → 3a → 8 → ...
                                                         (via Admin dashboard)
```

**Step 4 before Step 5** — migrations establish structural prerequisites (indexes, validators, views) that the ETL phases depend on.

**Step 5 is selective** — not every deployment requires a full ETL rebuild. The PR description should note which phases are required.

## What Moved from `eng/scripts/`

| Old location | New location | Type |
| --- | --- | --- |
| `apply-kg-validators.mongodb.js` | `migrations/0003-apply-kg-validators.js` | Migration |
| `backfill-kg-labels.mongodb.js` | `migrations/0004-backfill-kg-labels.js` | Migration |
| `migrate-kg-edges-denorm.mongodb.js` | `migrations/0005-denorm-kg-edges.js` | Migration |
| `migrate-kg-edges-dedup-and-index.mongodb.js` | `migrations/0006-dedup-kg-edges-and-indexes.js` | Migration |
| `migrate-kg-bidir-view.mongodb.js` | `migrations/0007-create-kg-bidir-view.js` | Migration |
| `migrate-kg-lineages.mongodb.js` | `migrations/0008-populate-kg-lineages.js` | Migration |
| `migrate-prod-kg.mongodb.js` | Decomposed into 0001 + 0002 + 0003 + 0004 | Retired |
| `verify-kg-validators.mongodb.js` | `utils/verify-validators.js` | Utility |
| `generate-template-fields.mongodb.js` | `utils/generate-template-fields.js` | Utility |

## Future Work

- **Admin dashboard integration** — Add `GET/POST /api/admin/migrations` endpoints to the Admin project so migrations can be triggered from the Aspire dashboard alongside ETL phases.
- **Diff report** — Compare migration state across environments to show what's deployed where.
- **Pre-deploy CI check** — Warn if a PR references new collections or indexes without a corresponding migration.
