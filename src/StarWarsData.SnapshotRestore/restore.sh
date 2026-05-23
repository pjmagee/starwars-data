#!/usr/bin/env bash
# ----------------------------------------------------------------------------
# restore.sh — run-once developer-onboarding snapshot restore
# ----------------------------------------------------------------------------
# Downloads the shared starwars-prod snapshot (produced by
# eng/scripts/make-snapshot.sh, hosted on OneDrive) and restores it into the
# developer's target database, renaming the namespace starwars-prod.* ->
# <TARGET_DB>.* so the app's default config just works.
#
# Idempotent and safe to re-run / restart in the Aspire dashboard:
#   - No SNAPSHOT_URL set            -> no-op (dev is using the shared server,
#                                       or hasn't been given the URL yet).
#   - Target already has raw.pages   -> skip, unless FORCE=true.
#   - Hard guard: refuses to write a database whose name contains "prod".
#
# Env:
#   MDB_CONNECTION_STRING  (required) target mongo connection string
#   TARGET_DB              (required) e.g. starwars-dev
#   SNAPSHOT_URL           (optional) HTTPS direct-download URL of the .gz
#   SNAPSHOT_SRC_DB        (default: starwars-prod) namespace in the archive
#   FORCE                  (default: false) re-restore even if populated
# ----------------------------------------------------------------------------
set -euo pipefail

: "${MDB_CONNECTION_STRING:?MDB_CONNECTION_STRING is required}"
: "${TARGET_DB:?TARGET_DB is required}"
SNAPSHOT_URL="${SNAPSHOT_URL:-}"
SRC_DB="${SNAPSHOT_SRC_DB:-starwars-prod}"
FORCE="${FORCE:-false}"

log() { echo "[snapshot-restore] $*"; }

# Hard guard — this flow only ever writes a dev database.
case "$TARGET_DB" in
  *prod*)
    log "REFUSING to restore into '$TARGET_DB' (looks like production). Aborting."
    exit 1
    ;;
esac

if [ -z "$SNAPSHOT_URL" ]; then
  log "SNAPSHOT_URL not set — nothing to do (using a pre-populated/shared database)."
  exit 0
fi

# Idempotency probe — skip if the dev DB already has content.
EXISTING=$(mongosh "$MDB_CONNECTION_STRING" --quiet --eval \
  "db.getSiblingDB('$TARGET_DB').getCollection('raw.pages').estimatedDocumentCount()" 2>/dev/null || echo 0)
EXISTING=$(printf '%s' "$EXISTING" | tr -dc '0-9'); EXISTING="${EXISTING:-0}"

if [ "$EXISTING" -gt 0 ] && [ "$FORCE" != "true" ]; then
  log "'$TARGET_DB'.raw.pages already has $EXISTING docs — skipping (set FORCE=true to override)."
  exit 0
fi

ARCHIVE="/tmp/starwars-snapshot.gz"
log "Downloading snapshot…"
# -C - resumes a partial file across retries — a multi-GB pull over a home
# uplink / Cloudflare proxy must not restart from zero on a dropped connection.
curl -fL -C - --retry 5 --retry-delay 5 --retry-connrefused -o "$ARCHIVE" "$SNAPSHOT_URL"

# Fail fast on a misconfigured URL. A "view"/share page (OneDrive, Drive, etc.)
# returns an HTML interstitial, not the archive — catch it here instead of
# letting mongorestore emit a confusing binary-parse error 6 GB later.
if ! gzip -t "$ARCHIVE" 2>/dev/null; then
  log "ERROR: downloaded file is not a valid gzip archive."
  log "SNAPSHOT_URL must be a DIRECT-download link (object storage / static"
  log "HTTPS), not a share/preview page. First bytes:"
  head -c 200 "$ARCHIVE" | tr -dc '[:print:]\n' || true
  rm -f "$ARCHIVE"
  exit 1
fi

log "Downloaded $(du -h "$ARCHIVE" | cut -f1). Restoring '$SRC_DB' -> '$TARGET_DB'…"

mongorestore \
  --uri="$MDB_CONNECTION_STRING" \
  --gzip \
  --archive="$ARCHIVE" \
  --nsFrom="${SRC_DB}.*" \
  --nsTo="${TARGET_DB}.*" \
  --drop \
  --numParallelCollections=4

rm -f "$ARCHIVE"
log "Restore complete. NOTE: recreate vector indexes via the Aspire"
log "'Ensure All Indexes' command (chains pages → chunks → vector, no OpenAI key needed)."
