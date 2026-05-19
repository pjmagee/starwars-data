#!/usr/bin/env bash
# ----------------------------------------------------------------------------
# unraid-snapshot-cron.sh — scheduled snapshot publisher (run ON the Unraid box)
# ----------------------------------------------------------------------------
# Drop this into the Unraid "User Scripts" plugin (or a root crontab) on the box
# that hosts prod Mongo + copyparty. It:
#
#   1. mongodumps starwars-prod (via make-snapshot.sh — same excludes) to a
#      *.partial file inside the copyparty volume,
#   2. gzip-verifies it, then **atomically** renames it to a stable name so a
#      developer's restore can never fetch a half-written archive,
#   3. keeps the last N dated copies for rollback and prunes the rest,
#   4. is flock-guarded so a slow run never overlaps the next schedule.
#
# Because it runs on the box, the dump is written straight into the copyparty
# volume — there is NO upload step and COPYPARTY_ADMIN is NOT needed. The
# developer-side `snapshot-url` points at the stable file and never changes.
#
# Required env (set these in the User Scripts editor, NOT in the repo):
#   MDB_URI         prod connection string (maintainer-held secret)
#   COPYPARTY_VOL   absolute path of the copyparty volume dir on disk
# Optional:
#   STABLE_NAME     published filename (default: starwars-snapshot-latest.gz)
#   KEEP            dated copies to retain (default: 3)
#
# Suggested schedule: weekly. Exits non-zero on failure so Unraid surfaces it.
# ----------------------------------------------------------------------------
set -euo pipefail

: "${MDB_URI:?set MDB_URI to the prod connection string}"
: "${COPYPARTY_VOL:?set COPYPARTY_VOL to the copyparty volume directory}"
STABLE_NAME="${STABLE_NAME:-starwars-snapshot-latest.gz}"
KEEP="${KEEP:-3}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LOCK="/tmp/sw-snapshot-cron.lock"
STAMP="$(date -u +%Y%m%d)"
DATED="${COPYPARTY_VOL}/starwars-snapshot-${STAMP}.gz"
PARTIAL="${DATED}.partial"
STABLE="${COPYPARTY_VOL}/${STABLE_NAME}"

log() { echo "[snapshot-cron $(date -u +%H:%M:%S)] $*"; }

# Single instance only — a dump can outrun the schedule.
exec 9>"$LOCK"
if ! flock -n 9; then
  log "another run holds the lock — exiting."
  exit 0
fi

trap 'rm -f "$PARTIAL"' EXIT  # never leave a half-written file behind

log "dumping prod -> $PARTIAL"
MDB_URI="$MDB_URI" "$SCRIPT_DIR/make-snapshot.sh" "$PARTIAL"

log "verifying gzip integrity"
gzip -t "$PARTIAL"

# Atomic within the same filesystem: readers see either the old file or the
# new one, never a partial. Publish the dated copy, then swap the stable name.
mv -f "$PARTIAL" "$DATED"
ln_target="$(basename "$DATED")"
ln -sfn "$ln_target" "${STABLE}.tmp" 2>/dev/null && mv -Tf "${STABLE}.tmp" "$STABLE" \
  || cp -f "$DATED" "$STABLE"   # fall back to a copy if symlinks aren't served
trap - EXIT

log "published: $DATED  (stable: $STABLE)"

# Retention — keep the newest $KEEP dated archives, drop older ones.
mapfile -t OLD < <(ls -1t "${COPYPARTY_VOL}"/starwars-snapshot-2*.gz 2>/dev/null | tail -n +"$((KEEP + 1))")
for f in "${OLD[@]:-}"; do
  [ -n "$f" ] || continue
  log "pruning old snapshot: $f"
  rm -f "$f"
done

log "done."
