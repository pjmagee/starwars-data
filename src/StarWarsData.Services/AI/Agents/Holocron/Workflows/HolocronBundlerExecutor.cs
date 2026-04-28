using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services.AI.Agents.Holocron.Workflows;

/// <summary>
/// Stage 2 of the Holocron workflow (Design-020). Pure C# — no LLM. Reads the
/// new-chunks set produced by Discovery and groups them into batches that fit
/// the LLM's context window.
///
/// The packing rule is the same one Timeline's <c>PageBundlerExecutor</c> uses:
/// keep adding chunks until the running char-budget would exceed the cap, then
/// close the batch and start a new one. Each batch additionally receives the
/// target's own-page chunks (handled by the extractor) for grounding context,
/// so the cap leaves headroom for that.
///
/// Empty input → zero batches → the workflow continues to the extractor which
/// short-circuits with an empty result (the same path a no-op re-run takes when
/// every chunk is unchanged).
/// </summary>
internal sealed class HolocronBundlerExecutor : Executor<string, string>
{
    public const string Scope = "HolocronBundler";
    public const string KeyBatches = "batches";

    /// <summary>
    /// Char budget per batch. Mirrors Timeline's <c>PageBundlerExecutor.MaxBatchChars</c>.
    /// At ~600 chars per chunk excerpt (the Holocron truncation default) and ~2K of
    /// own-page grounding overhead, this packs ~60 chunks per batch — a comfortable
    /// fit for gpt-5.4 reasoning calls.
    /// </summary>
    const int MaxBatchChars = 40_000;

    readonly ILogger _logger;
    readonly HolocronEnhancementTracker? _tracker;
    readonly int _pageId;
    readonly string _jobId;
    readonly HolocronJobService _jobService;

    public HolocronBundlerExecutor(ILogger logger, HolocronJobService jobService, int pageId, string jobId, HolocronEnhancementTracker? tracker)
        : base("HolocronBundler")
    {
        _logger = logger;
        _jobService = jobService;
        _pageId = pageId;
        _jobId = jobId;
        _tracker = tracker;
    }

    public override async ValueTask<string> HandleAsync(string message, IWorkflowContext context, CancellationToken ct = default)
    {
        var node = await context.ReadStateAsync<HolocronNodeSnapshot>(HolocronContextDiscoveryExecutor.KeyNode, HolocronContextDiscoveryExecutor.Scope, ct);
        var newChunks =
            await context.ReadStateAsync<List<HolocronChunkRef>>(HolocronContextDiscoveryExecutor.KeyNewChunks, HolocronContextDiscoveryExecutor.Scope, ct)
            ?? throw new InvalidOperationException("HolocronBundler: no newChunks in Discovery state");

        await _jobService.TransitionAsync(_jobId, HolocronJobStatus.Bundling, null, ct);
        _tracker?.UpdateProgress(
            _pageId,
            HolocronJobStatus.Bundling,
            $"Bundling {newChunks.Count} chunks into batches...",
            currentStep: 0,
            totalSteps: Math.Max(1, newChunks.Count),
            currentItem: node?.Name
        );

        var batches = new List<HolocronBatch>();
        var current = new List<HolocronChunkRef>();
        var currentChars = 0;
        foreach (var chunk in newChunks)
        {
            // Bundle by char-budget using the pre-computed TextLength from Discovery —
            // we no longer have the body in memory, but the length suffices for partitioning.
            var size = chunk.TextLength + (chunk.Title?.Length ?? 0) + (chunk.Heading?.Length ?? 0) + 200; // framing overhead
            if (current.Count > 0 && currentChars + size > MaxBatchChars)
            {
                batches.Add(new HolocronBatch(batches.Count, current));
                current = new List<HolocronChunkRef>();
                currentChars = 0;
            }
            current.Add(chunk);
            currentChars += size;
        }
        if (current.Count > 0)
            batches.Add(new HolocronBatch(batches.Count, current));

        await context.QueueStateUpdateAsync(KeyBatches, batches, Scope, ct);

        await _jobService.TransitionAsync(_jobId, HolocronJobStatus.Bundling, Builders<HolocronJob>.Update.Set(j => j.TotalBatches, batches.Count), ct);

        await context.AddEventAsync(new HolocronBundlingCompleteEvent(new HolocronBundlingCompleteData(newChunks.Count, batches.Count, batches.Select(b => b.Chunks.Count).ToList())), ct);

        _logger.LogInformation("HolocronBundler: PageId={PageId} ({Name}) — bundled {Total} chunks into {Batches} batches.", _pageId, node?.Name, newChunks.Count, batches.Count);

        _tracker?.UpdateProgress(
            _pageId,
            HolocronJobStatus.Bundling,
            $"Bundled {newChunks.Count} chunks into {batches.Count} batches",
            currentStep: newChunks.Count,
            totalSteps: Math.Max(1, newChunks.Count),
            currentItem: node?.Name
        );

        return $"Bundled {newChunks.Count} chunks into {batches.Count} batches";
    }
}
