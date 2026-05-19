#!/usr/bin/env bash
# ----------------------------------------------------------------------------
# make-snapshot.sh — produce a developer-onboarding snapshot of starwars-prod
# ----------------------------------------------------------------------------
# Dumps the prod database to a single gzip archive, EXCLUDING user/operational
# data (chat.*, admin.*, hangfire.*) for privacy/GDPR and size. Everything a
# developer needs to run the app without re-running ETL or spending OpenAI
# credit is included — raw.*, kg.*, timeline.*, search.* (incl. embeddings),
# galaxy.*, genai.*, territory.*.
#
# The maintainer runs this, then uploads the resulting .gz to the snapshot
# host (object storage / static HTTPS) and sets SNAPSHOT_URL to its
# direct-download URL (see ONBOARDING.md / eng/design/038-*.md).
#
# NOTE: mongodump does NOT capture Atlas Search / vector-search index
# DEFINITIONS (only collections + regular indexes). Embeddings survive as
# document fields, so no re-embedding is needed on restore — but the dev must
# recreate vector indexes via the existing "4b. Create Index Embeddings" /
# "Ensure All Indexes" Aspire commands after restoring. This is keyless.
#
# Usage:
#   MDB_URI='mongodb://user:pass@host:port/?authSource=admin&directConnection=true' \
#     src/StarWarsData.SnapshotRestore/make-snapshot.sh [output-path]
#
# Defaults: output -> ./starwars-snapshot-YYYYMMDD.gz
# ----------------------------------------------------------------------------
set -euo pipefail

SRC_DB="${SRC_DB:-starwars-prod}"
URI="${MDB_URI:-${MDB_MCP_CONNECTION_STRING:-}}"
OUT="${1:-./starwars-snapshot-$(date -u +%Y%m%d).gz}"

if [[ -z "$URI" ]]; then
  echo "ERROR: set MDB_URI (or MDB_MCP_CONNECTION_STRING) to the prod connection string." >&2
  exit 1
fi

if ! command -v mongodump >/dev/null 2>&1; then
  echo "ERROR: mongodump not found. Install MongoDB Database Tools." >&2
  exit 1
fi

echo "Dumping '$SRC_DB' → $OUT (excluding chat.*, admin.*, hangfire.*)…"

mongodump \
  --uri="$URI" \
  --db="$SRC_DB" \
  --excludeCollectionsWithPrefix="chat." \
  --excludeCollectionsWithPrefix="admin." \
  --excludeCollectionsWithPrefix="hangfire." \
  --gzip \
  --archive="$OUT"

SIZE=$(du -h "$OUT" | cut -f1)
echo "Done. Snapshot: $OUT ($SIZE)"
echo
echo "Next: upload to the snapshot host (object storage / static HTTPS), then"
echo "set the snapshot-url parameter to its direct-download URL (see ONBOARDING.md)."
