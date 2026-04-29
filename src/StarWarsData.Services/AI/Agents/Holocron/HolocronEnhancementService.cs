using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Services.AI.Agents.Holocron.Workflows;

namespace StarWarsData.Services.AI.Agents.Holocron;

/// <summary>
/// Orchestrator for the async Holocron pipeline (Design-020). Mirrors
/// <c>CharacterTimelineService.GenerateTimelineAsync</c> exactly:
///
/// <list type="number">
///   <item>Construct one fresh executor per stage (state isolation per run).</item>
///   <item>Wire them with <see cref="WorkflowBuilder.AddEdge"/> into a 5-step
///         sequential workflow with the Apply executor as the output node.</item>
///   <item>Resume-if-possible from <see cref="MongoCheckpointStore"/>; otherwise
///         start a fresh run keyed by session id <c>holocron-enhance-{pageId}</c>.</item>
///   <item>Stream events from <see cref="StreamingRun.WatchStreamAsync"/> and
///         bridge each one into <see cref="HolocronEnhancementTracker"/>'s activity
///         feed for the polling UI.</item>
///   <item>Clear the checkpoint after stream consumption so a post-workflow
///         retry (e.g. parse failure) starts clean — same gotcha that bit
///         Character Timeline; preserved here.</item>
/// </list>
///
/// The job-doc lifecycle is handled inside the executors (each transitions the
/// row to its corresponding <see cref="HolocronJobStatus"/> and bumps progress
/// counters); this service is the workflow-shaped wrapper around that.
/// </summary>
public sealed class HolocronEnhancementService
{
    readonly IMongoClient _mongoClient;
    readonly SettingsOptions _settings;
    readonly ILogger<HolocronEnhancementService> _logger;
    readonly HolocronAgent _agent;
    readonly HolocronJobService _jobService;
    readonly HolocronVerifierService _verifier;
    readonly HolocronAuditService _audit;

    public HolocronEnhancementService(
        IMongoClient mongoClient,
        IOptions<SettingsOptions> settings,
        ILogger<HolocronEnhancementService> logger,
        HolocronAgent agent,
        HolocronJobService jobService,
        HolocronVerifierService verifier,
        HolocronAuditService audit
    )
    {
        _mongoClient = mongoClient;
        _settings = settings.Value;
        _logger = logger;
        _agent = agent;
        _jobService = jobService;
        _verifier = verifier;
        _audit = audit;
    }

    /// <summary>
    /// Build the 5-executor workflow for one node, run it (with resume-from-checkpoint
    /// if a prior run was interrupted), and bridge workflow events to the tracker.
    /// Throws on hard failures (node missing, unrecoverable exceptions); the API
    /// controller marks the job + tracker Failed.
    /// </summary>
    public async Task RunAsync(string jobId, int pageId, string triggeredBy, HolocronEnhancementTracker tracker, CancellationToken ct)
    {
        _logger.LogInformation("Holocron pipeline starting — JobId={JobId}, PageId={PageId}, Trigger={Trigger}", jobId, pageId, triggeredBy);

        // ── Fresh executors per run (each holds in-memory state mirrored to checkpoints) ──
        var discovery = new HolocronContextDiscoveryExecutor(_mongoClient, _settings, _logger, _jobService, pageId, jobId, tracker);
        var bundler = new HolocronBundlerExecutor(_logger, _jobService, pageId, jobId, tracker);
        var extractor = new HolocronProposalExtractorExecutor(_agent, _logger, _jobService, _mongoClient, _settings.DatabaseName, pageId, jobId, tracker);
        var consolidator = new HolocronConsolidatorExecutor(_mongoClient, _settings, _logger, _jobService, _audit, pageId, jobId, tracker);
        var verifier = new HolocronEvidenceVerifierExecutor(_verifier, _logger, _jobService, _audit, pageId, jobId, tracker);
        var apply = new HolocronApplyExecutor(_mongoClient, _settings, _logger, _jobService, _audit, pageId, jobId, triggeredBy, tracker);

        var workflow = new WorkflowBuilder(discovery)
            .AddEdge(discovery, bundler)
            .AddEdge(bundler, extractor)
            .AddEdge(extractor, consolidator)
            .AddEdge(consolidator, verifier)
            .AddEdge(verifier, apply)
            .WithOutputFrom(apply)
            .WithName($"HolocronEnhance-{pageId}")
            .Build(validateOrphans: true);

        var checkpointStore = new MongoCheckpointStore(_mongoClient, _settings.DatabaseName, Collections.GenaiHolocronCheckpoints);
        var sessionId = $"holocron-enhance-{pageId}";
        var checkpointManager = CheckpointManager.CreateJson(checkpointStore);

        var existingCheckpoints = (await checkpointStore.RetrieveIndexAsync(sessionId)).ToList();

        // `await using` — StreamingRun is IAsyncDisposable. The official Agent
        // Framework checkpoint samples use this pattern; without it the run's
        // execution resources aren't released until GC fires.
        await using StreamingRun run =
            existingCheckpoints.Count > 0
                ? await ResumeAsync(workflow, existingCheckpoints[^1], checkpointManager, jobId, pageId, existingCheckpoints.Count, tracker, ct)
                : await StartAsync(workflow, pageId, checkpointManager, sessionId, jobId, ct);

        // Track the first executor-level failure we observe so we can surface it
        // as a hard error instead of silently completing. The framework emits
        // ExecutorFailedEvent + WorkflowErrorEvent on per-stage exceptions but
        // does not always re-throw on stream completion.
        Exception? firstFailure = null;
        await foreach (var evt in run.WatchStreamAsync(ct))
        {
            switch (evt)
            {
                case ExecutorFailedEvent failed:
                    var failMsg = $"Executor '{failed.ExecutorId}' failed: {failed.Data}";
                    _logger.LogError("Holocron pipeline JobId={JobId} PageId={PageId} — {Msg}", jobId, pageId, failMsg);
                    firstFailure ??= new InvalidOperationException(failMsg);
                    break;

                case WorkflowErrorEvent error:
                    var errMsg = error.Data?.ToString() ?? "Unknown workflow error";
                    _logger.LogError("Holocron pipeline JobId={JobId} PageId={PageId} — workflow error: {Msg}", jobId, pageId, errMsg);
                    firstFailure ??= new InvalidOperationException(errMsg);
                    break;

                default:
                    BridgeEventToTracker(tracker, pageId, evt);
                    break;
            }
        }

        if (firstFailure is not null)
        {
            // Don't clear checkpoints on failure — they let a retry resume from
            // the last good superstep instead of starting from scratch.
            throw firstFailure;
        }

        // Always clear after a clean run so a future re-enhance doesn't accidentally
        // resume from a stale terminal checkpoint. Same gotcha CharacterTimelineService
        // documents; preserved exactly.
        await checkpointStore.ClearSessionAsync(sessionId);

        _logger.LogInformation("Holocron pipeline complete — JobId={JobId} PageId={PageId}", jobId, pageId);
    }

    async Task<StreamingRun> StartAsync(Workflow workflow, int pageId, CheckpointManager checkpointManager, string sessionId, string jobId, CancellationToken ct)
    {
        _logger.LogInformation("Starting fresh Holocron pipeline JobId={JobId} PageId={PageId}", jobId, pageId);
        return await InProcessExecution.RunStreamingAsync(workflow, pageId.ToString(), checkpointManager, sessionId, ct);
    }

    async Task<StreamingRun> ResumeAsync(
        Workflow workflow,
        CheckpointInfo latest,
        CheckpointManager checkpointManager,
        string jobId,
        int pageId,
        int total,
        HolocronEnhancementTracker tracker,
        CancellationToken ct
    )
    {
        _logger.LogInformation("Resuming Holocron pipeline JobId={JobId} PageId={PageId} from checkpoint {CheckpointId} ({Total} saved)", jobId, pageId, latest.CheckpointId, total);
        tracker.Update(pageId, HolocronJobStatus.Discovering, $"Resuming from checkpoint ({total} saved)...");
        return await InProcessExecution.ResumeStreamingAsync(workflow, latest, checkpointManager, ct);
    }

    /// <summary>
    /// Bridge framework + custom workflow events to the tracker's activity log.
    /// Mirrors <c>CharacterTimelineService.BridgeEventToTracker</c> in shape and
    /// intent — every interesting executor event becomes one
    /// <see cref="HolocronActivityLogEntry"/> the polling UI can render.
    /// </summary>
    static void BridgeEventToTracker(HolocronEnhancementTracker tracker, int pageId, WorkflowEvent evt)
    {
        var entry = evt switch
        {
            HolocronDiscoveryCompleteEvent e when e.Data is HolocronDiscoveryCompleteData d => new HolocronActivityLogEntry
            {
                Timestamp = DateTime.UtcNow,
                Category = "Discovery",
                EntryType = "discovery_complete",
                Summary =
                    d.NewChunks == 0
                        ? $"Discovery complete: {d.TotalChunks} chunks across {d.LinkingPages} pages — all unchanged since last run."
                        : $"Discovery complete: {d.NewChunks} new chunks (of {d.TotalChunks}) across {d.LinkingPages} linking pages; {d.SkippedUnchanged} unchanged.",
                Detail = d,
            },
            HolocronBundlingCompleteEvent e when e.Data is HolocronBundlingCompleteData d => new HolocronActivityLogEntry
            {
                Timestamp = DateTime.UtcNow,
                Category = "Bundling",
                EntryType = "bundling_complete",
                Summary = $"Bundled {d.TotalChunks} chunks into {d.BatchCount} batches [{string.Join(", ", d.BatchSizes)}]",
                Detail = d,
            },
            HolocronBatchExtractionStartedEvent e when e.Data is HolocronBatchExtractionStartedData d => new HolocronActivityLogEntry
            {
                Timestamp = DateTime.UtcNow,
                Category = "Extraction",
                EntryType = "batch_started",
                Summary = $"Extracting batch {d.BatchIndex}/{d.TotalBatches} ({d.ChunkCount} chunks: {string.Join(", ", d.SourcePageTitles.Take(3))}{(d.SourcePageTitles.Count > 3 ? "..." : "")})",
                Detail = d,
            },
            HolocronBatchExtractionEmptyEvent e when e.Data is HolocronBatchExtractionEmptyData d => new HolocronActivityLogEntry
            {
                Timestamp = DateTime.UtcNow,
                Category = "Extraction",
                EntryType = "batch_empty",
                Summary = $"No proposals from batch {d.BatchIndex} ({d.ChunkCount} chunks)",
            },
            HolocronBatchExtractionFailedEvent e when e.Data is HolocronBatchExtractionFailedData d => new HolocronActivityLogEntry
            {
                Timestamp = DateTime.UtcNow,
                Category = "Extraction",
                EntryType = "batch_failed",
                Summary = $"Batch {d.BatchIndex} failed ({d.ChunkCount} chunks): {d.Error}",
                Detail = d,
            },
            HolocronProposalsExtractedEvent e when e.Data is HolocronProposalsExtractedData d => new HolocronActivityLogEntry
            {
                Timestamp = DateTime.UtcNow,
                Category = "Extraction",
                EntryType = "proposals_extracted",
                Summary = $"Batch {d.BatchIndex} → {d.Annotates} annotates, {d.FillGaps} fillGaps, {d.AddEdges} adds, {d.NodeProposals} nodeProps",
                Detail = d,
            },
            HolocronConsolidationCompleteEvent e when e.Data is HolocronConsolidationCompleteData d => new HolocronActivityLogEntry
            {
                Timestamp = DateTime.UtcNow,
                Category = "Consolidation",
                EntryType = "consolidation_complete",
                Summary = $"Consolidated {d.RawProposals} → {d.Consolidated} (dropped {d.DuplicatesDropped} dups, {d.PreflightRejects} pre-flight rejects, {d.EvidenceFailures} no-evidence)",
                Detail = d,
            },
            HolocronVerificationCompleteEvent e when e.Data is HolocronVerificationCompleteData d => new HolocronActivityLogEntry
            {
                Timestamp = DateTime.UtcNow,
                Category = "Verification",
                EntryType = "verification_complete",
                Summary =
                    d.VerifierRejects == 0
                        ? $"Verifier passed all {d.InputProposals} proposals."
                        : $"Verifier accepted {d.Verified}/{d.InputProposals} ({d.VerifierRejects} rejected by evidence-quality check)",
                Detail = d,
            },
            HolocronApplyCompleteEvent e when e.Data is HolocronApplyCompleteData d => new HolocronActivityLogEntry
            {
                Timestamp = DateTime.UtcNow,
                Category = "Apply",
                EntryType = "apply_complete",
                Summary = $"Wrote {d.EnrichmentsWritten} node + {d.EdgeEnrichmentsWritten} edge enrichments, {d.EventsEmitted} events, recorded {d.ProcessedChunksRecorded} processed chunks",
                Detail = d,
            },
            _ => null,
        };

        if (entry is not null)
            tracker.AddActivityLog(pageId, entry);
    }
}
