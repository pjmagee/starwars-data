#!/bin/bash
# Star Wars Data — Unraid User Script
# Downloads the latest release artifact from GitHub Actions and deploys via docker compose.
#
# Setup:
#   1. Install the "User Scripts" plugin on Unraid
#   2. Add this as a new script
#   3. Set schedule (e.g., daily or on demand)
#   4. Configure the variables below
#
# Prerequisites:
#   - GitHub Personal Access Token with "actions:read" scope (for artifact download)
#   - .env file already configured with secrets in DEPLOY_DIR

set -euo pipefail

# ── Configuration ──────────────────────────────────────────────────────────────
GITHUB_REPO="pjmagee/starwars-data"
GITHUB_TOKEN="ghp_your_token_here"        # PAT with actions:read scope
DEPLOY_DIR="/boot/config/plugins/compose.manager/projects/starwars"
COMPOSE_PROJECT="swdata"
# ───────────────────────────────────────────────────────────────────────────────

WORK_DIR=$(mktemp -d)
trap 'rm -rf "$WORK_DIR"' EXIT

log() { echo "[$(date '+%Y-%m-%d %H:%M:%S')] $*"; }

# Get the latest successful workflow run's artifact download URL
log "Fetching latest release artifact from $GITHUB_REPO..."

ARTIFACT_URL=$(curl -sf \
  -H "Authorization: Bearer $GITHUB_TOKEN" \
  -H "Accept: application/vnd.github+json" \
  "https://api.github.com/repos/$GITHUB_REPO/actions/artifacts?name=release&per_page=1" \
  | jq -r '.artifacts[0].archive_download_url')

if [ -z "$ARTIFACT_URL" ] || [ "$ARTIFACT_URL" = "null" ]; then
  log "ERROR: No release artifact found."
  exit 1
fi

# Download and extract artifact
log "Downloading artifact..."
curl -sfL \
  -H "Authorization: Bearer $GITHUB_TOKEN" \
  -H "Accept: application/vnd.github+json" \
  -o "$WORK_DIR/release.zip" \
  "$ARTIFACT_URL"

unzip -qo "$WORK_DIR/release.zip" -d "$WORK_DIR/release"

# Ensure deploy directory exists
mkdir -p "$DEPLOY_DIR"

# Copy compose file (always overwrite — it's generated)
# compose.manager uses .yml
cp "$WORK_DIR/release/docker-compose.yaml" "$DEPLOY_DIR/docker-compose.yml"
log "Updated docker-compose.yaml"

# Initialize .env from template if it doesn't exist yet
if [ ! -f "$DEPLOY_DIR/.env" ]; then
  if [ -f "$WORK_DIR/release/.env.template" ]; then
    cp "$WORK_DIR/release/.env.template" "$DEPLOY_DIR/.env"
    log "Created .env from template — EDIT $DEPLOY_DIR/.env WITH YOUR SECRETS before next run!"
    exit 0
  fi
fi

# Merge image versions into existing .env (preserves all other vars/secrets)
if [ -f "$WORK_DIR/release/.env.versions" ]; then
  while IFS='=' read -r key value; do
    # Skip empty lines and comments
    [[ -z "$key" || "$key" =~ ^[[:space:]]*# ]] && continue
    # Trim whitespace
    key=$(echo "$key" | xargs)
    value=$(echo "$value" | xargs)

    if grep -q "^${key}=" "$DEPLOY_DIR/.env" 2>/dev/null; then
      # Update existing key
      sed -i "s|^${key}=.*|${key}=${value}|" "$DEPLOY_DIR/.env"
    else
      # Append new key
      echo "${key}=${value}" >> "$DEPLOY_DIR/.env"
    fi
    log "Set ${key}=${value}"
  done < "$WORK_DIR/release/.env.versions"
fi

# Pull new images and restart
log "Pulling images..."
cd "$DEPLOY_DIR"
docker compose -p "$COMPOSE_PROJECT" pull

log "Restarting stack..."
docker compose -p "$COMPOSE_PROJECT" up -d --remove-orphans

log "Deploy complete."
docker compose -p "$COMPOSE_PROJECT" ps
