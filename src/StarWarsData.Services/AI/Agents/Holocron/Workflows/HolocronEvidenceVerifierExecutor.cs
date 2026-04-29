using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services.AI.Agents.Holocron.Workflows;

/// <summary>
/// Stage 4.5 — between consolidation and apply. Runs <see cref="HolocronVerifierService"/>
/// against every consolidated proposal so each one's cited chunk gets a direct
/// "does this support the claim?" verdict. Proposals where the model says no are
/// dropped before they reach the apply step.
///
/// <para>
/// Background: the consolidator catches structural failures (wrong target type,
/// invalid fieldPath, hedge-word self-rejection — see Design-025). It cannot
/// catch <i>target-substitution hallucinations</i> where the agent's claim text
/// describes a different role/entity than the PageId it submitted (Ahsoka v1.4.0
/// case: claim "held the role of commander", target = Clone Captain). Those are
/// what the verifier exists for.
/// </para>
///
/// <para>
/// Fail-open by design — if the verifier's LLM call fails or returns malformed
/// output, every proposal flows through unmodified. The verifier is a quality
/// filter, not a hard gate; the existing pre-flight is the hard gate.
/// </para>
/// </summary>
internal sealed class HolocronEvidenceVerifierExecutor : Executor<string, string>
{
    public const string Scope = "HolocronVerification";
    public const string KeyVerified = "verified";

    readonly HolocronVerifierService _verifier;
    readonly ILogger _logger;
    readonly HolocronEnhancementTracker? _tracker;
    readonly HolocronJobService _jobService;
    readonly HolocronAuditService _audit;
    readonly int _pageId;
    readonly string _jobId;

    public HolocronEvidenceVerifierExecutor(
        HolocronVerifierService verifier,
        ILogger logger,
        HolocronJobService jobService,
        HolocronAuditService audit,
        int pageId,
        string jobId,
        HolocronEnhancementTracker? tracker
    )
        : base("HolocronEvidenceVerifier")
    {
        _verifier = verifier;
        _logger = logger;
        _jobService = jobService;
        _audit = audit;
        _pageId = pageId;
        _jobId = jobId;
        _tracker = tracker;
    }

    public override async ValueTask<string> HandleAsync(string message, IWorkflowContext context, CancellationToken ct = default)
    {
        var consolidated =
            await context.ReadStateAsync<HolocronConsolidatedProposals>(HolocronConsolidatorExecutor.KeyConsolidated, HolocronConsolidatorExecutor.Scope, ct)
            ?? new HolocronConsolidatedProposals([], [], [], [], 0, 0, 0);
        var node =
            await context.ReadStateAsync<HolocronNodeSnapshot>(HolocronContextDiscoveryExecutor.KeyNode, HolocronContextDiscoveryExecutor.Scope, ct)
            ?? throw new InvalidOperationException("HolocronVerifier: no node in Discovery state");

        await _jobService.TransitionAsync(_jobId, HolocronJobStatus.Verifying, null, ct);

        var inputCount = consolidated.AnnotateEdges.Count + consolidated.FillGapEdges.Count + consolidated.AddEdges.Count + consolidated.NodeProposals.Count;
        if (inputCount == 0)
        {
            // No proposals to verify — write the empty consolidated set straight through.
            await context.QueueStateUpdateAsync(KeyVerified, consolidated, Scope, ct);
            await context.AddEventAsync(new HolocronVerificationCompleteEvent(new HolocronVerificationCompleteData(0, 0, 0, [])), ct);
            return $"Verifier: no proposals for {node.Name}";
        }

        _tracker?.UpdateProgress(
            _pageId,
            HolocronJobStatus.Verifying,
            $"Verifying {inputCount} consolidated proposals against cited evidence...",
            currentStep: 0,
            totalSteps: 1,
            currentItem: node.Name
        );

        var result = await _verifier.VerifyAsync(consolidated, node.Name, ct);

        var verifiedCount = result.Filtered.AnnotateEdges.Count + result.Filtered.FillGapEdges.Count + result.Filtered.AddEdges.Count + result.Filtered.NodeProposals.Count;

        // Build a sample of rejected verdicts for the activity log — cap at 5 so
        // the UI doesn't get drowned. Full verdicts are still in the LLM response
        // and re-derivable by inspecting the run's chunks.
        var rejectVerdicts = result.Verdicts.Where(v => !v.Supports).Take(5).Select(v => new HolocronVerifierRejectData(v.Id, string.Empty, string.Empty, string.Empty, v.Reason)).ToList();

        // Update each verifier-processed proposal's audit row. The verdict.Id is
        // "add-{i}" indexing into consolidated.AddEdges or "prop-{i}" indexing into
        // consolidated.NodeProposals — the same flattening order HolocronVerifierService
        // used to build its request. Annotate/FillGap proposals aren't checked by the
        // verifier; their audits stay pending_verifier until the apply step finalises
        // them as applied.
        if (consolidated.AuditIds is not null && result.Verdicts.Count > 0)
        {
            var updates = new List<(string AuditId, string Outcome, string Reason)>(result.Verdicts.Count);
            foreach (var v in result.Verdicts)
            {
                string? key = null;
                if (v.Id.StartsWith("add-", StringComparison.Ordinal) && int.TryParse(v.Id["add-".Length..], out var addIdx) && addIdx < consolidated.AddEdges.Count)
                {
                    var p = consolidated.AddEdges[addIdx];
                    key = $"add|{p.FromId}|{p.ToId}|{p.Label.ToLowerInvariant()}";
                }
                else if (v.Id.StartsWith("prop-", StringComparison.Ordinal) && int.TryParse(v.Id["prop-".Length..], out var propIdx) && propIdx < consolidated.NodeProposals.Count)
                {
                    var p = consolidated.NodeProposals[propIdx];
                    key = $"property|{p.FieldPath}";
                }
                if (key is null || !consolidated.AuditIds.TryGetValue(key, out var auditId))
                    continue;
                var outcome = v.Supports ? "verifier_accepted" : "rejected_verifier";
                updates.Add((auditId, outcome, v.Reason ?? string.Empty));
            }
            await _audit.BulkUpdateOutcomeAsync(updates, stage: "verification", ct);
        }

        await context.QueueStateUpdateAsync(KeyVerified, result.Filtered, Scope, ct);
        await context.AddEventAsync(new HolocronVerificationCompleteEvent(new HolocronVerificationCompleteData(inputCount, verifiedCount, result.VerifierRejects, rejectVerdicts)), ct);

        await _jobService.TransitionAsync(_jobId, HolocronJobStatus.Verifying, Builders<HolocronJob>.Update.Set(j => j.VerifierRejects, result.VerifierRejects), ct);

        _logger.LogInformation(
            "HolocronVerifier: PageId={PageId} ({Name}) — {Input} consolidated → {Verified} verified ({Rejects} rejected by LLM evidence-quality check)",
            _pageId,
            node.Name,
            inputCount,
            verifiedCount,
            result.VerifierRejects
        );

        _tracker?.UpdateProgress(
            _pageId,
            HolocronJobStatus.Verifying,
            $"Verified {verifiedCount}/{inputCount} ({result.VerifierRejects} rejected by evidence check)",
            currentStep: 1,
            totalSteps: 1,
            currentItem: node.Name,
            enrichmentsApplied: verifiedCount
        );

        return $"Verified {verifiedCount}/{inputCount} for {node.Name}";
    }
}
