using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services.AI.Agents.Holocron;

/// <summary>
/// Holocron evidence verifier — Design-025 §What's actually broken — hallucinated
/// content. Catches the failure mode the structural consolidator can't see:
/// target-substitution hallucinations like "claim says 'commander' but target is
/// 'Clone Captain'" where every type/label/template/blocklist check passes but the
/// proposal doesn't match the chunk text.
///
/// <para>
/// Architecture: between the consolidator and the apply step, every surviving
/// proposal gets one structured-output verdict from the LLM. The model receives
/// a flat list of <c>(subject, predicate, target, claim, evidence excerpt)</c>
/// tuples and emits one <c>{supports: yes|no, reason}</c> per id. Proposals
/// where <c>supports = false</c> are dropped.
/// </para>
///
/// <para>
/// Scope is deliberately narrow: this is NOT a re-extraction or a re-ranking.
/// It only answers "does the cited chunk directly support the proposed fact"
/// per proposal — fast, single LLM call, structured. Cost is dominated by the
/// chunk text in the input prompt (one excerpt per proposal, ~200 chars each).
/// For an Ahsoka-scale run (~30 surviving proposals) the verifier is one
/// gpt-4o-mini call, &lt;$0.005, ~3s wall-clock.
/// </para>
/// </summary>
public sealed class HolocronVerifierService
{
    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    readonly IChatClient _chatClient;
    readonly IMongoCollection<ArticleChunk> _chunks;
    readonly IMongoCollection<GraphNode> _nodes;
    readonly ILogger<HolocronVerifierService> _logger;

    public HolocronVerifierService(IChatClient chatClient, IMongoClient mongoClient, IOptions<SettingsOptions> settings, ILogger<HolocronVerifierService> logger)
    {
        _chatClient = chatClient;
        var db = mongoClient.GetDatabase(settings.Value.DatabaseName);
        _chunks = db.GetCollection<ArticleChunk>(Collections.SearchChunks);
        _nodes = db.GetCollection<GraphNode>(Collections.KgNodes);
        _logger = logger;
    }

    /// <summary>
    /// Verify every proposal in <paramref name="consolidated"/> against its cited
    /// evidence. Returns the filtered set plus the verdict log so the activity
    /// stream can surface what was rejected and why.
    /// </summary>
    public async Task<HolocronVerifierResult> VerifyAsync(HolocronConsolidatedProposals consolidated, string targetName, CancellationToken ct = default)
    {
        // Flatten the high-risk proposal vectors (Add edges + Property Augments)
        // into the verification queue. Annotate and FillGap are deliberately
        // skipped — those are constrained to existing edges that the consolidator
        // already validated structurally, and the v1.5.0 calibration run showed
        // the verifier was too strict on year-bound inference (e.g. rejecting a
        // FillGap fromYear=-22 cited from "during the Clone Wars" because it
        // wasn't an explicit year). Add + Property are the vectors where the
        // hallucination cost is high enough to justify the false-positive risk.
        var requests = new List<VerificationRequest>();

        for (var i = 0; i < consolidated.AddEdges.Count; i++)
        {
            var p = consolidated.AddEdges[i];
            requests.Add(new VerificationRequest($"add-{i}", "add", p.FromId, p.ToId, p.Label, p.Claim, p.Evidence));
        }
        for (var i = 0; i < consolidated.NodeProposals.Count; i++)
        {
            var p = consolidated.NodeProposals[i];
            var richClaim = $"{p.Claim} (property {p.FieldPath} = [{string.Join(", ", p.Values)}])";
            requests.Add(new VerificationRequest($"prop-{i}", "property", 0, 0, p.FieldPath, richClaim, p.Evidence));
        }

        if (requests.Count == 0)
        {
            return new HolocronVerifierResult(consolidated, [], 0);
        }

        // Resolve subject/target names + evidence excerpts for the prompt.
        var nodeIds = requests.SelectMany(r => new[] { r.FromId, r.ToId }).Where(id => id > 0).Distinct().ToList();
        var nodeNames =
            nodeIds.Count == 0
                ? new Dictionary<int, string>()
                : (await _nodes.Find(Builders<GraphNode>.Filter.In(n => n.PageId, nodeIds)).Project(n => new { n.PageId, n.Name }).ToListAsync(ct)).ToDictionary(
                    n => n.PageId,
                    n => n.Name ?? string.Empty
                );

        var chunkIds = requests.SelectMany(r => r.Evidence.Where(e => !string.IsNullOrEmpty(e.ChunkId) && ObjectId.TryParse(e.ChunkId, out _)).Select(e => e.ChunkId!)).Distinct().ToList();
        var chunkText =
            chunkIds.Count == 0
                ? new Dictionary<string, string>()
                : (await _chunks.Find(Builders<ArticleChunk>.Filter.In(c => c.Id, chunkIds)).Project(c => new { c.Id, c.Text }).ToListAsync(ct)).ToDictionary(
                    c => c.Id,
                    c => c.Text ?? string.Empty,
                    StringComparer.Ordinal
                );

        var promptUser = BuildUserPrompt(targetName, requests, nodeNames, chunkText);

        var messages = new List<ChatMessage> { new(ChatRole.System, SystemPrompt), new(ChatRole.User, promptUser) };
        var options = new ChatOptions
        {
            ResponseFormat = ChatResponseFormat.ForJsonSchema<VerificationBatch>(schemaName: "holocron_verification", schemaDescription: "Per-proposal evidence-quality verdicts"),
        };

        VerificationBatch? batch;
        try
        {
            var response = await _chatClient.GetResponseAsync(messages, options, ct);
            var text = response.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.LogWarning("HolocronVerifier: empty LLM response — leaving all proposals unverified.");
                return new HolocronVerifierResult(consolidated, [], 0);
            }
            batch = JsonSerializer.Deserialize<VerificationBatch>(text, JsonOpts);
        }
        catch (Exception ex)
        {
            // Fail-open: a verifier outage must not block the run. Surface in logs
            // and emit the consolidated set unmodified — same as if every proposal
            // verified true. The job-doc telemetry will show 0 verifierRejects so
            // post-mortem can spot the silent skip.
            _logger.LogWarning(ex, "HolocronVerifier: LLM call failed — passing all proposals through unmodified.");
            return new HolocronVerifierResult(consolidated, [], 0);
        }

        if (batch is null)
        {
            return new HolocronVerifierResult(consolidated, [], 0);
        }

        var verdictById = batch.Verifications.ToDictionary(v => v.Id, v => v, StringComparer.Ordinal);

        // Filter the typed lists by their corresponding verdict. Missing verdicts
        // are treated as "supports=true" — fail-open so the verifier dropping a
        // proposal from its response never accidentally censors valid output.
        bool Keep(string id) => !verdictById.TryGetValue(id, out var v) || v.Supports;

        var keptAdds = consolidated.AddEdges.Where((_, i) => Keep($"add-{i}")).ToList();
        var keptProps = consolidated.NodeProposals.Where((_, i) => Keep($"prop-{i}")).ToList();

        var rejects = (consolidated.AddEdges.Count - keptAdds.Count) + (consolidated.NodeProposals.Count - keptProps.Count);

        // Annotate / FillGap pass through unverified (see VerifyAsync header).
        var filtered = new HolocronConsolidatedProposals(
            consolidated.AnnotateEdges,
            consolidated.FillGapEdges,
            keptAdds,
            keptProps,
            consolidated.DuplicatesDropped,
            consolidated.PreflightRejects,
            consolidated.EvidenceFailures
        );

        return new HolocronVerifierResult(filtered, batch.Verifications, rejects);
    }

    static string BuildUserPrompt(string targetName, IReadOnlyList<VerificationRequest> requests, IReadOnlyDictionary<int, string> nodeNames, IReadOnlyDictionary<string, string> chunkText)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Target node");
        sb.AppendLine(targetName);
        sb.AppendLine();
        sb.AppendLine("# Proposals to verify");
        sb.AppendLine();

        for (var i = 0; i < requests.Count; i++)
        {
            var r = requests[i];
            sb.Append("## ").Append(r.Id).Append(" — ").Append(r.Kind).AppendLine();
            if (r.Kind == "property")
            {
                sb.Append("Target: ").AppendLine(targetName);
                sb.Append("Property: ").AppendLine(r.Label);
            }
            else
            {
                var fromName = r.FromId > 0 ? nodeNames.GetValueOrDefault(r.FromId, $"PageId {r.FromId}") : targetName;
                var toName = r.ToId > 0 ? nodeNames.GetValueOrDefault(r.ToId, $"PageId {r.ToId}") : targetName;
                sb.Append("Subject: ").AppendLine(fromName);
                sb.Append("Predicate: ").AppendLine(r.Label);
                sb.Append("Target: ").AppendLine(toName);
            }
            sb.Append("Claim: ").AppendLine(r.Claim);
            sb.AppendLine("Evidence excerpts:");
            var added = 0;
            foreach (var ev in r.Evidence.Take(3))
            {
                var excerpt = ev.Excerpt;
                if (string.IsNullOrEmpty(excerpt) && !string.IsNullOrEmpty(ev.ChunkId) && chunkText.TryGetValue(ev.ChunkId, out var fullText))
                    excerpt = fullText.Length > 600 ? fullText[..600] : fullText;
                if (string.IsNullOrEmpty(excerpt))
                    continue;
                sb.Append("  - ").AppendLine(excerpt.Replace("\n", " ").Trim());
                added++;
            }
            if (added == 0)
                sb.AppendLine("  (no excerpt available)");
            sb.AppendLine();
        }

        sb.AppendLine("Output one verdict per proposal id, supports=true only when the chunk excerpt DIRECTLY states the proposed fact for the named subject and target.");
        return sb.ToString();
    }

    const string SystemPrompt = """
        You are an evidence verifier for a Star Wars knowledge graph. For each
        proposal below, decide whether the cited chunk excerpts support the proposed
        fact between the named subject and target.

        Reject (supports=false) ONLY when one of these clear failures holds:
          - The chunk merely co-mentions the entities (cast list, "see also", appearance
            index, link aggregation) — no real relationship is evidenced.
          - **Target substitution**: the chunk supports a related fact but the proposal
            targets a different specific entity. e.g. claim says "commander" but the
            proposal points at the "Clone Captain" node — that's a hallucinated target
            mismatch and MUST be rejected.
          - The chunk explicitly contradicts the proposed claim.
          - The proposal's claim or reasoning hedges ("appears alongside", "no add edge
            is warranted", "if supported by") — the agent self-flagged its own weakness.

        Accept (supports=true) when the chunk supports the fact, including reasonable
        inference from context. e.g. "during the Clone Wars" supports a -22 to -19 BBY
        bound; "she rose to Jedi Knight after the war" supports has_role → Jedi Knight
        even without a verbatim sentence pattern.

        Default to accept when borderline. The structural pre-flight already handles
        wrong-type / wrong-template / hedge-word failures; your job is to catch the
        target-substitution hallucinations and clear contradictions that pre-flight
        cannot see.
        """;

    sealed record VerificationRequest(string Id, string Kind, int FromId, int ToId, string Label, string Claim, IReadOnlyList<HolocronEvidencePayload> Evidence);

    /// <summary>Structured-output payload returned by the verifier LLM call.</summary>
    public sealed class VerificationBatch
    {
        [JsonPropertyName("verifications")]
        public List<VerificationVerdict> Verifications { get; set; } = [];
    }

    public sealed class VerificationVerdict
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("supports")]
        public bool Supports { get; set; }

        [JsonPropertyName("reason")]
        public string Reason { get; set; } = string.Empty;
    }
}

/// <summary>
/// Output of <see cref="HolocronVerifierService.VerifyAsync"/>. <see cref="Filtered"/>
/// is the input <see cref="HolocronConsolidatedProposals"/> minus rejected entries
/// (counters carried through from consolidation; the verifier-rejects total is on
/// <see cref="VerifierRejects"/> and surfaced separately in job telemetry).
/// </summary>
public sealed record HolocronVerifierResult(HolocronConsolidatedProposals Filtered, IReadOnlyList<HolocronVerifierService.VerificationVerdict> Verdicts, int VerifierRejects);
