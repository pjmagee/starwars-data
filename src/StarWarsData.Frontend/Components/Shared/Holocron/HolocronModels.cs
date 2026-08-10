using MudBlazor;
using StarWarsData.Models.Entities;

namespace StarWarsData.Frontend.Components.Shared.Holocron;

/// <summary>
/// Shared DTOs + display helpers for the three Holocron UI surfaces
/// (jobs list, node jobs page, progress dialog).
/// </summary>
public static class HolocronUi
{
    public static readonly HolocronJobStatus[] Stages =
    [
        HolocronJobStatus.Queued,
        HolocronJobStatus.Discovering,
        HolocronJobStatus.Bundling,
        HolocronJobStatus.Extracting,
        HolocronJobStatus.Consolidating,
        HolocronJobStatus.Applying,
        HolocronJobStatus.Completed,
    ];

    public static StageState GetState(HolocronJobStatus stage, HolocronJobStatus current)
    {
        if (current == HolocronJobStatus.Failed)
            return stage == current ? StageState.Failed : StageState.Pending;

        var currentIdx = Array.IndexOf(Stages, current);
        var stageIdx = Array.IndexOf(Stages, stage);
        if (stageIdx < currentIdx)
            return StageState.Done;
        if (stageIdx == currentIdx)
            return current == HolocronJobStatus.Completed ? StageState.Done : StageState.Active;
        return StageState.Pending;
    }

    public static string StageLabel(HolocronJobStatus stage) =>
        stage switch
        {
            HolocronJobStatus.Queued => "Queued for execution",
            HolocronJobStatus.Discovering => "Discovering backlink chunks",
            HolocronJobStatus.Bundling => "Bundling chunks into batches",
            HolocronJobStatus.Extracting => "Extracting proposals (one LLM call per batch)",
            HolocronJobStatus.Consolidating => "Consolidating proposals + pre-flight checks",
            HolocronJobStatus.Applying => "Writing enrichments + audit events + ledger",
            HolocronJobStatus.Completed => "Complete",
            HolocronJobStatus.Failed => "Failed",
            _ => stage.ToString(),
        };

    public static int StageProgress(HolocronJobStatus stage) =>
        stage switch
        {
            HolocronJobStatus.Queued => 5,
            HolocronJobStatus.Discovering => 15,
            HolocronJobStatus.Bundling => 25,
            HolocronJobStatus.Extracting => 65,
            HolocronJobStatus.Consolidating => 85,
            HolocronJobStatus.Applying => 95,
            HolocronJobStatus.Completed => 100,
            HolocronJobStatus.Failed => 100,
            _ => 0,
        };

    public static int SubStepProgress(int currentStep, int totalSteps) =>
        totalSteps <= 0 ? 0 : (int)((double)currentStep / totalSteps * 100);

    public static string CategoryIcon(string category) =>
        category switch
        {
            "Discovery" => Icons.Material.Filled.TravelExplore,
            "Bundling" => Icons.Material.Filled.Inventory,
            "Extraction" => Icons.Material.Filled.AutoAwesome,
            "Consolidation" => Icons.Material.Filled.Compress,
            "Apply" => Icons.Material.Filled.Save,
            _ => Icons.Material.Filled.Circle,
        };

    public static Color CategoryColor(string category) =>
        category switch
        {
            "Discovery" => Color.Info,
            "Bundling" => Color.Secondary,
            "Extraction" => Color.Primary,
            "Consolidation" => Color.Tertiary,
            "Apply" => Color.Success,
            _ => Color.Default,
        };

    public static Color StatusColor(HolocronJobStatus status) =>
        status switch
        {
            HolocronJobStatus.Completed => Color.Success,
            HolocronJobStatus.Failed => Color.Error,
            HolocronJobStatus.Queued => Color.Default,
            _ => Color.Primary,
        };

    public static Color StageColor(HolocronJobStatus stage) =>
        stage switch
        {
            HolocronJobStatus.Queued => Color.Default,
            HolocronJobStatus.Discovering => Color.Info,
            HolocronJobStatus.Bundling => Color.Secondary,
            HolocronJobStatus.Extracting => Color.Primary,
            HolocronJobStatus.Consolidating => Color.Tertiary,
            HolocronJobStatus.Applying => Color.Warning,
            HolocronJobStatus.Completed => Color.Success,
            HolocronJobStatus.Failed => Color.Error,
            _ => Color.Default,
        };

    public static string FormatDuration(DateTime? startedAt, DateTime? completedAt)
    {
        if (startedAt is null)
            return "—";
        var end = completedAt ?? DateTime.UtcNow;
        var d = end - startedAt.Value;
        if (d.TotalSeconds < 60)
            return $"{d.TotalSeconds:0.0}s";
        if (d.TotalMinutes < 60)
            return $"{d.TotalMinutes:0.0}m";
        return $"{d.TotalHours:0.0}h";
    }

    public static string FormatDuration(HolocronJobRow job) => FormatDuration(job.StartedAt, job.CompletedAt);
}

public enum StageState
{
    Pending,
    Active,
    Done,
    Failed,
}

public sealed record HolocronStatusResponse(
    string? JobId,
    int PageId,
    string? NodeName,
    HolocronJobStatus Stage,
    string Message,
    string? Error,
    DateTime StartedAt,
    DateTime? CompletedAt,
    int CurrentStep,
    int TotalSteps,
    string? CurrentItem,
    int ProposalsExtracted,
    int EnrichmentsApplied,
    List<HolocronActivityLogEntry> ActivityLog
);

public sealed class HolocronActivityLogEntry
{
    public DateTime Timestamp { get; set; }
    public string Category { get; set; } = string.Empty;
    public string EntryType { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public object? Detail { get; set; }
}

public sealed record HolocronJobsPageResponse(List<HolocronJobRow> Items, int Total, int Page, int PageSize);

public sealed record HolocronJobRow(
    string Id,
    int PageId,
    string NodeName,
    HolocronJobStatus Status,
    string TriggeredBy,
    string AgentVersion,
    string ModelId,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    int ChunksDiscovered,
    int ChunksProcessedNew,
    int ChunksSkippedUnchanged,
    int TotalBatches,
    int CompletedBatches,
    int ProposalsExtracted,
    int EnrichmentsApplied,
    int DuplicatesDropped,
    int EvidenceFailures,
    int PreflightRejects,
    string? Error
);
