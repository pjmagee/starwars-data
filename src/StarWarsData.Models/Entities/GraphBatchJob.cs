using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.Entities;

/// <summary>
/// Tracks an OpenAI Batch API job for relationship extraction.
/// Each batch can contain up to 50,000 page requests.
/// </summary>
public class GraphBatchJob
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = ObjectId.GenerateNewId().ToString();

    /// <summary>OpenAI Batch API batch ID (e.g. "batch_abc123").</summary>
    [BsonElement("openAiBatchId")]
    public string OpenAiBatchId { get; set; } = string.Empty;

    /// <summary>OpenAI File ID for the uploaded JSONL input.</summary>
    [BsonElement("inputFileId")]
    public string InputFileId { get; set; } = string.Empty;

    /// <summary>OpenAI File ID for the output JSONL (set when batch completes).</summary>
    [BsonElement("outputFileId")]
    public string? OutputFileId { get; set; }

    /// <summary>OpenAI File ID for the error JSONL (set when batch completes).</summary>
    [BsonElement("errorFileId")]
    public string? ErrorFileId { get; set; }

    [BsonElement("status")]
    [BsonRepresentation(BsonType.String)]
    public GraphBatchStatus Status { get; set; } = GraphBatchStatus.Preparing;

    /// <summary>Number of requests in this batch.</summary>
    [BsonElement("totalRequests")]
    public int TotalRequests { get; set; }

    /// <summary>Number of successfully completed requests (set after processing).</summary>
    [BsonElement("completedRequests")]
    public int CompletedRequests { get; set; }

    /// <summary>Number of failed requests (set after processing).</summary>
    [BsonElement("failedRequests")]
    public int FailedRequests { get; set; }

    /// <summary>Number of pages skipped by the LLM (no meaningful relationships).</summary>
    [BsonElement("skippedRequests")]
    public int SkippedRequests { get; set; }

    /// <summary>Total edges stored from this batch.</summary>
    [BsonElement("totalEdgesStored")]
    public int TotalEdgesStored { get; set; }

    /// <summary>Model used for this batch.</summary>
    [BsonElement("model")]
    public string Model { get; set; } = string.Empty;

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [BsonElement("submittedAt")]
    public DateTime? SubmittedAt { get; set; }

    [BsonElement("completedAt")]
    public DateTime? CompletedAt { get; set; }

    [BsonElement("processedAt")]
    public DateTime? ProcessedAt { get; set; }

    [BsonElement("error")]
    public string? Error { get; set; }

    /// <summary>PageIds included in this batch, for tracking.</summary>
    [BsonElement("pageIds")]
    public List<int> PageIds { get; set; } = [];
}

public enum GraphBatchStatus
{
    Preparing,
    Submitted,
    InProgress,
    Completed,
    Processing,
    Processed,
    Failed,
}
