using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services.Executors;

/// <summary>
/// Pure C# executor (no LLM) that groups discovered pages into token-budget batches.
/// Each batch fits within a target character limit so a single LLM call can process
/// all pages in the batch without exceeding context limits.
///
/// Superstep 2: Discovery → [Bundler] → Extraction → Consolidation → Review
/// </summary>
internal sealed class PageBundlerExecutor : Executor<string, string>
{
    private readonly ILogger _logger;
    private readonly CharacterTimelineTracker? _tracker;
    private readonly int _characterPageId;

    /// <summary>
    /// Target characters per batch. Each page contributes infobox + content snippet (~4000-5000 chars).
    /// At ~30K chars per batch we get ~5-6 pages per call, which keeps each LLM call
    /// responsive (~30-60s) while still being ~6x more efficient than per-page calls.
    /// </summary>
    private const int MaxBatchChars = 30_000;

    public PageBundlerExecutor(
        ILogger logger,
        CharacterTimelineTracker? tracker,
        int characterPageId)
        : base("PageBundler")
    {
        _logger = logger;
        _tracker = tracker;
        _characterPageId = characterPageId;
    }

    public override async ValueTask<string> HandleAsync(
        string message, IWorkflowContext context, CancellationToken ct = default)
    {
        var pages = await context.ReadStateAsync<List<PageContent>>("pages", "Discovery", ct)
            ?? throw new InvalidOperationException("No pages found in Discovery state");

        _tracker?.UpdateProgress(_characterPageId, GenerationStage.Bundling,
            $"Bundling {pages.Count} pages into extraction batches...",
            currentStep: 0, totalSteps: pages.Count);

        var batches = new List<PageBatch>();
        var currentBatchPages = new List<PageContent>();
        var currentBatchChars = 0;

        foreach (var page in pages)
        {
            var pageChars = EstimatePageChars(page);

            // If adding this page exceeds the budget, close the current batch
            if (currentBatchPages.Count > 0 && currentBatchChars + pageChars > MaxBatchChars)
            {
                batches.Add(new PageBatch(batches.Count, currentBatchPages.ToList()));
                currentBatchPages.Clear();
                currentBatchChars = 0;
            }

            currentBatchPages.Add(page);
            currentBatchChars += pageChars;
        }

        // Don't forget the last batch
        if (currentBatchPages.Count > 0)
        {
            batches.Add(new PageBatch(batches.Count, currentBatchPages.ToList()));
        }

        await context.QueueStateUpdateAsync("batches", batches, "Bundler", ct);

        await context.AddEventAsync(new BundlingCompleteEvent(new BundlingCompleteData(
            pages.Count, batches.Count,
            batches.Select(b => b.Pages.Count).ToList())), ct);

        _logger.LogInformation(
            "Bundled {PageCount} pages into {BatchCount} batches: [{BatchSizes}]",
            pages.Count, batches.Count,
            string.Join(", ", batches.Select(b => b.Pages.Count)));

        _tracker?.UpdateProgress(_characterPageId, GenerationStage.Bundling,
            $"Bundled {pages.Count} pages into {batches.Count} batches",
            currentStep: pages.Count, totalSteps: pages.Count);

        return $"Bundled {pages.Count} pages into {batches.Count} batches";
    }

    private static int EstimatePageChars(PageContent page)
    {
        return (page.InfoboxText?.Length ?? 0)
             + (page.ContentSnippet?.Length ?? 0)
             + (page.Title?.Length ?? 0) * 2  // title appears in headers
             + 200; // JSON framing overhead per page
    }
}

/// <summary>
/// A batch of pages to be processed in a single LLM call.
/// </summary>
internal sealed record PageBatch(
    int BatchIndex,
    List<PageContent> Pages);
