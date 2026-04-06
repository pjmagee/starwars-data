# ADR-005: MongoDB Migration Strategy — Convention-Based with Tracking

**Status:** Accepted
**Date:** 2026-04-06
**Decision maker:** Patrick Magee

## Context

The application uses MongoDB with a single unified database (`starwars`) containing ~20 namespaced collections (`raw.*`, `kg.*`, `timeline.*`, `search.*`, `genai.*`, `chat.*`, `territory.*`, `galaxy.*`, `admin.*`, `suggestions.*`). There are two environments:

| Environment | Current DB name | Purpose |
| --- | --- | --- |
| Development | `starwars-dev` | Safe experimentation, schema iteration |
| Production | `starwars` (to be renamed `starwars-prod`) | Live data serving the public site |

The ETL pipeline (Phases 1–9) is **idempotent by design** — every phase does a full delete+insert of its target collections. This means data content is always reproducible by rerunning the pipeline. However, **structural changes** — indexes, validators, views, collection renames, field removals — are not covered by the ETL and must be applied separately.

Today, structural migrations live as ad-hoc `mongosh` scripts in `eng/scripts/`. They work well, but the current approach has gaps:

1. **No tracking** — There is no record of which scripts have been applied to which environment. The operator must remember or check manually.
2. **No ordering enforcement** — The README documents run order, but nothing enforces it.
3. **Naming ambiguity** — `starwars` vs `starwars-dev` is convention-dependent; the prod database name doesn't signal that it's production.

We need a migration strategy that closes these gaps without over-engineering for a single-developer project with infrequent structural changes.

## Options Considered

### Option A: Formal migration framework (Mongock / custom .NET migration runner)

Versioned up/down migrations with a dedicated .NET project. Each migration is a C# class with `Up()` and `Down()` methods. A runner executes pending migrations on startup or via CLI.

**Pros:** Strong ordering guarantees, rollback support, compile-time safety, familiar pattern from EF Migrations.

**Rejected because:**

- The ETL pipeline already handles data migrations (full rebuild). Structural changes happen ~2-4 times per quarter — a framework is disproportionate overhead.
- Mongock requires Java or Spring; .NET alternatives (Mongo.Migration, custom) are either unmaintained or require building from scratch.
- Down/rollback migrations are misleading for MongoDB structural changes — dropping an index is trivial, but "un-dropping" a collection or "un-renaming" a field requires the data to exist. The ETL rebuild is the real rollback.

### Option B: C# migrations in the Admin project

Write migrations as C# classes using `MongoDB.Driver`, discovered by convention, executed via Admin endpoints. No `mongosh` dependency.

**Rejected because:**

- Translating all existing `mongosh` scripts to C# duplicates work — the scripts already exist and are tested.
- Loses the ability to test migrations standalone in `mongosh` against any database without running the full .NET application.
- The Admin container doesn't need `mongosh` bundled, but neither does it need to reimplement `collMod`, `createView`, `$graphLookup` closures, and `$out` + `rename` dedup pipelines in C#.

### Option C: Keep current approach (ad-hoc scripts, no tracking)

Continue with `eng/scripts/*.mongodb.js` and the README as the source of truth.

**Rejected because:**

- The deployment plan for this release already exposed the gap: the operator needed to manually piece together which scripts to run, in what order, against which database. This is error-prone and doesn't scale even for a single developer across two environments.

### Option D: mongosh migration runner with tracking collection (chosen)

Restructure scripts into `src/StarWarsData.MongoDbMigrations/` as a standalone migration project. Each migration is a CommonJS module exporting `{ id, description, up(db) }`. A `migrate.js` runner discovers, orders, and executes pending migrations, tracking results in a `migrations` collection. Works with both `mongosh` and Node.js.

## Decision

**Option D — mongosh-native migration runner in `src/StarWarsData.MongoDbMigrations/`.**

### 1. Project structure

```text
src/StarWarsData.MongoDbMigrations/
├── migrate.js              Entry point — runner logic
├── lib/
│   └── validators.js       Shared $jsonSchema definitions
├── migrations/
│   ├── 0001-*.js            Tracked migrations (deterministic order)
│   └── ...
└── utils/
    ├── verify-validators.js        Read-only diagnostics
    └── generate-template-fields.js Code generation
```

### 2. Migration module contract

Each migration file exports a CommonJS module:

```js
module.exports = {
  id: "0001-drop-stale-pairid",
  description: "Drop stale pairId field from kg.edges",
  up(db) {
    // db is the mongosh database object. Must be idempotent.
    return { modifiedCount: 123 }; // optional result
  },
};
```

Rules:

- `NNNN` sequence numbers are monotonically increasing. Never reuse.
- Must be **idempotent** — safe to re-run if tracking write fails.
- Must use the `db` parameter (resolved by the runner from `STARWARS_DB`), never hardcode a database name.
- No down/rollback scripts. Write a new forward migration to revert. The ETL rebuild is the real rollback.

### 3. Tracking collection: `migrations`

```json
{
  "_id": "0001-drop-stale-pairid",
  "description": "Drop stale pairId field from kg.edges",
  "script": "0001-drop-stale-pairid.js",
  "appliedAt": ISODate("2026-04-06T14:30:00Z"),
  "durationMs": 450,
  "result": { "modifiedCount": 584000 }
}
```

`_id` uniqueness prevents double-application. Per-database tracking (each environment tracks its own state independently).

### 4. Runner capabilities

```bash
# Apply all pending
mongosh "$CONN" --quiet --file src/StarWarsData.MongoDbMigrations/migrate.js

# Show status
mongosh "$CONN" --quiet --eval 'ACTION="status"' --file src/StarWarsData.MongoDbMigrations/migrate.js

# Dry run
mongosh "$CONN" --quiet --eval 'DRY_RUN=true' --file src/StarWarsData.MongoDbMigrations/migrate.js

# Target specific migration
mongosh "$CONN" --quiet --eval 'MIGRATION="0003"' --file src/StarWarsData.MongoDbMigrations/migrate.js

# Target prod
mongosh "$CONN" --quiet --eval 'STARWARS_DB="starwars-prod"' --file src/StarWarsData.MongoDbMigrations/migrate.js
```

Also works via Node.js with the `mongodb` driver for CI/CD pipelines.

### 5. Database naming

Rename the production database from `starwars` to `starwars-prod`. The `DatabaseName` default in `SettingsOptions` becomes environment-driven:

| Environment | Database | Hangfire DB |
| --- | --- | --- |
| Development | `starwars-dev` | `starwars-dev-hangfire` |
| Production | `starwars-prod` | `starwars-prod-hangfire` |

### 6. Migration categories

| Category | Where it lives | When to use |
| --- | --- | --- |
| **Migration** | `src/StarWarsData.MongoDbMigrations/migrations/` | Structural changes applied exactly once per environment |
| **ETL phase** | Admin dashboard | Idempotent data rebuilds |
| **Utility script** | `src/StarWarsData.MongoDbMigrations/utils/` | Ad-hoc diagnostics and code generation |

## Rationale

- **mongosh scripts stay** because they are the right tool for MongoDB structural operations. They can be tested standalone against any database.
- **CommonJS modules** give clean separation per migration while remaining runnable in `mongosh` (which uses Node.js under the hood).
- **Tracking collection** is the minimum viable coordination mechanism. It answers "has this been applied?" without external state.
- **No rollback** because the ETL pipeline is the rollback mechanism.
- **No C# translation** because the scripts already work, are tested, and the operations (`collMod`, `createView`, `$graphLookup` closures, `$out` + `rename`) are more naturally expressed in the MongoDB shell API than the .NET driver.

## Consequences

**Positive:**

- Deterministic ordering enforced by sequence numbers and the runner.
- Migration history tracked per environment in the `migrations` collection.
- Safe re-runs — idempotent migrations + `_id` uniqueness guard.
- CI/CD compatible — works via `mongosh --file` or `node migrate.js`.
- Clear separation between tracked migrations and ad-hoc utilities.

**Negative / Trade-offs:**

- Requires `mongosh` 2.x+ (for `require()` support). Not available in all environments by default.
- Discipline-based (naming contract, idempotency) rather than framework-enforced.
- Per-database tracking means no single view of "what's deployed where" without querying both environments.

## References

- [Design-010](../design/010-mongodb-migration-environment-promotion.md) — implementation plan and deployment workflow
- [ADR-003](003-kg-query-architecture.md) — KG query architecture (denormalization, indexes, bidir view)
- [src/StarWarsData.MongoDbMigrations/README.md](../../src/StarWarsData.MongoDbMigrations/README.md) — runner usage and migration inventory
