# eng/scripts

MongoDB migration and maintenance scripts, runnable via `mongosh`.

## Target database

All scripts default to `starwars-dev`. To target a different database (e.g. prod), set the `STARWARS_DB` variable before the script loads:

```bash
# Dev (default)
mongosh "$MDB_MCP_CONNECTION_STRING" --quiet --file eng/scripts/migrate-kg-edges-denorm.mongodb.js

# Prod
mongosh "$MDB_MCP_CONNECTION_STRING" --quiet --eval 'STARWARS_DB="starwars"' --file eng/scripts/migrate-kg-edges-denorm.mongodb.js
```

## KG migration scripts (ADR-003 + Design-007 + Design-008)

These scripts apply the new denormalized fields, dedup, indexes, bidirectional view, and lineage closures to an **existing** `kg.nodes` + `kg.edges` dataset without requiring a full Phase 5 ETL rerun.

**Run order matters** — each script builds on the output of the previous one:

| Order | Script | What it does | Destructive? | Runtime |
| --- | --- | --- | --- | --- |
| 1 | `migrate-kg-edges-denorm.mongodb.js` | Backfills `fromRealm`, `toRealm`, `reverseLabel` on `kg.edges` | No (additive `$merge`) | ~30s |
| 2 | `migrate-kg-edges-dedup-and-index.mongodb.js` | Removes duplicate `(fromId,toId,label)` edges, creates the authoritative named index set with unique constraint | **Yes** — replaces `kg.edges` via `$out` + `rename` if dupes found | ~30s |
| 3 | `migrate-kg-bidir-view.mongodb.js` | Creates the `kg.edges.bidir` bidirectional view (Design-007) | No (view only, drops+recreates if exists) | instant |
| 4 | `migrate-kg-lineages.mongodb.js` | Populates lineage closures on `kg.nodes` + creates `ix_lineages_wildcard` (Design-008) | No (additive `$merge`, clears lineages first) | ~20s |

### When to run these scripts

**The recommended prod rollout is just: deploy the code, then trigger Phase 5 via the Aspire dashboard.** Phase 5 is a full delete+insert of `kg.nodes` + `kg.edges` and now handles all denormalization, dedup, indexes, lineages, labels, validators, and the bidir view in one pass. Brief downtime while the rebuild runs is fine.

The migration scripts exist as an **alternative** for the case where you want to patch an existing dataset without triggering a full rebuild (e.g. to test a single improvement in isolation). They are idempotent and safe to re-run.

## Schema validation scripts

| Script | What it does |
| --- | --- |
| `apply-kg-validators.mongodb.js` | Applies/updates `$jsonSchema` validators on `kg.edges` and `kg.nodes` in `moderate/warn` mode. Matches the current C# model (realm, reverseLabel, lineages, meta, etc.). |
| `verify-kg-validators.mongodb.js` | Reports how many existing docs fail the active validator on each KG collection. |

## Label registry scripts

| Script | What it does |
| --- | --- |
| `backfill-kg-labels.mongodb.js` | One-shot materialized view of `kg.labels` from `kg.edges` via aggregation + `$merge`. Seeds `reverse`/`description` as empty — Phase 5 ETL populates those from `FieldSemantics`. |

## Production pre-flight

| Script | What it does |
| --- | --- |
| `migrate-prod-kg.mongodb.js` | All-in-one prod pre-flight: cleans stale `pairId`, drops redundant indexes, applies validators, backfills `kg.labels`. Supports `DRY_RUN=true` for preview. Hardcoded to `starwars` database. |

```bash
# Dry run (read-only preview)
mongosh "$MDB_MCP_CONNECTION_STRING" --quiet --eval 'DRY_RUN=true' --file eng/scripts/migrate-prod-kg.mongodb.js

# Execute
mongosh "$MDB_MCP_CONNECTION_STRING" --quiet --file eng/scripts/migrate-prod-kg.mongodb.js
```

## Code generation scripts

| Script | Purpose |
| --- | --- |
| `generate-template-fields.mongodb.js` | Regenerates `TemplateFields.g.cs` from live MongoDB data. See header comment for usage. |
