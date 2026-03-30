// Run with: dotnet script tools/migrate-collections.csx
// Or just use the MCP tool approach below

// This documents the migration mapping for reference.
// The actual migration is run via MongoDB MCP tools or mongosh.

// Mapping:
// starwars-raw-pages/Pages           → starwars/raw.pages ✓
// starwars-raw-pages/JobState        → starwars/raw.job_state ✓
// starwars-timeline-events/*         → starwars/timeline.*
// starwars-relationship-graph/edges  → starwars/kg.edges
// starwars-relationship-graph/labels → starwars/kg.labels
// starwars-relationship-graph/crawl_state → starwars/kg.crawl_state
// starwars-relationship-graph/batch_jobs  → starwars/kg.batch_jobs
// starwars-relationship-graph/chunks      → starwars/kg.chunks
// starwars-character-timelines/Timelines  → starwars/genai.character_timelines
// starwars-character-timelines/WorkflowCheckpoints → starwars/genai.character_checkpoints
// starwars-character-timelines/ExtractionProgress  → starwars/genai.character_progress
// starwars-chat-sessions/sessions    → starwars/chat.sessions
// starwars-territory-control/territory_snapshots → starwars/territory.snapshots
// starwars-hangfire-jobs → starwars-hangfire (separate DB, Hangfire manages schema)
