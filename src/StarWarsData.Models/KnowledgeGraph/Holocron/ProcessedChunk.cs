using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.Entities;

/// <summary>
/// Per-(node, chunk) processing ledger entry. Stored in <c>kg.node_processed_chunks</c>
/// per Design-020. Records that a given chunk's content (identified by
/// <see cref="ChunkContentHash"/>) was fed to the agent during enhancement of the
/// given <see cref="NodeId"/>.
///
/// On re-run, Discovery consults this collection to skip chunks whose
/// <c>(NodeId, ChunkId)</c> is recorded AND whose <see cref="ChunkContentHash"/>
/// matches the chunk's current hash. Unchanged content → no LLM call → zero cost.
///
/// Indexes (created at service-init time, see <c>HolocronJobService</c>):
///   - <c>{ nodeId: 1, chunkId: 1 }</c> unique — primary lookup
///   - <c>{ nodeId: 1 }</c> — bulk fetch for one node
/// </summary>
public sealed class ProcessedChunk
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = ObjectId.GenerateNewId().ToString();

    /// <summary>The node whose enhancement consumed this chunk. Matches <c>kg.nodes._id</c>.</summary>
    [BsonElement("nodeId")]
    public int NodeId { get; set; }

    /// <summary>The chunk that was processed. Matches <c>search.chunks._id</c>.</summary>
    [BsonElement("chunkId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string ChunkId { get; set; } = string.Empty;

    /// <summary>
    /// SHA256 hex of the chunk's text at processing time. Compared against the chunk's
    /// current <c>contentHash</c> on re-run — equality means the chunk hasn't changed
    /// since this record was written, so we can skip re-processing.
    /// </summary>
    [BsonElement("chunkContentHash")]
    public string ChunkContentHash { get; set; } = string.Empty;

    [BsonElement("processedAt")]
    public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The job that consumed this chunk. Forensic link to <c>kg.enrichment_jobs._id</c>.</summary>
    [BsonElement("jobId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string JobId { get; set; } = string.Empty;

    /// <summary>Stamp from <c>HolocronAgent.AgentVersion</c> at processing time. If the agent's prompt or schema changes meaningfully, bumping the version invalidates the ledger and forces re-processing.</summary>
    [BsonElement("agentVersion")]
    public string AgentVersion { get; set; } = string.Empty;
}
