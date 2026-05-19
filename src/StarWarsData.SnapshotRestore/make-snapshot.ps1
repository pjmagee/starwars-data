# ----------------------------------------------------------------------------
# make-snapshot.ps1 — produce a developer-onboarding snapshot of starwars-prod
# ----------------------------------------------------------------------------
# Windows/PowerShell twin of make-snapshot.sh. Dumps the prod database to a
# single gzip archive, EXCLUDING user/operational data (chat.*, admin.*,
# hangfire.*) for privacy/GDPR and size. Includes everything needed to run the
# app without re-running ETL or spending OpenAI credit (raw.*, kg.*, timeline.*,
# search.* incl. embeddings, galaxy.*, genai.*, territory.*).
#
# mongodump does NOT capture Atlas Search / vector-search index DEFINITIONS.
# Embeddings survive as document fields (no re-embedding on restore); the dev
# recreates vector indexes via the existing Aspire index commands (keyless).
#
# Usage:
#   $env:MDB_URI='mongodb://user:pass@host:port/?authSource=admin&directConnection=true'
#   src/StarWarsData.SnapshotRestore/make-snapshot.ps1 [-Out <path>]
# ----------------------------------------------------------------------------
[CmdletBinding()]
param(
    [string]$Out = "./starwars-snapshot-$(Get-Date -Format yyyyMMdd).gz",
    [string]$SrcDb = $(if ($env:SRC_DB) { $env:SRC_DB } else { "starwars-prod" })
)
$ErrorActionPreference = "Stop"

$uri = if ($env:MDB_URI) { $env:MDB_URI } else { $env:MDB_MCP_CONNECTION_STRING }
if (-not $uri) {
    throw "Set `$env:MDB_URI (or MDB_MCP_CONNECTION_STRING) to the prod connection string."
}
if (-not (Get-Command mongodump -ErrorAction SilentlyContinue)) {
    throw "mongodump not found. Install MongoDB Database Tools."
}

Write-Host "Dumping '$SrcDb' -> $Out (excluding chat.*, admin.*, hangfire.*)..."

& mongodump `
    --uri="$uri" `
    --db="$SrcDb" `
    --excludeCollectionsWithPrefix="chat." `
    --excludeCollectionsWithPrefix="admin." `
    --excludeCollectionsWithPrefix="hangfire." `
    --gzip `
    --archive="$Out"
if ($LASTEXITCODE -ne 0) { throw "mongodump failed with exit code $LASTEXITCODE." }

$sizeMb = [math]::Round((Get-Item $Out).Length / 1MB, 1)
Write-Host "Done. Snapshot: $Out ($sizeMb MB)"
Write-Host ""
Write-Host "Next: upload to the snapshot host (object storage / static HTTPS), then"
Write-Host "set the snapshot-url parameter to its direct-download URL (see ONBOARDING.md)."
