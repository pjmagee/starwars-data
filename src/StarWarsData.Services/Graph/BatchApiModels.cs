using System.Text.Json.Serialization;

namespace StarWarsData.Services;

// ── OpenAI Batch API request line (JSONL input) ─────────────────────────────

/// <summary>
/// A single line in the OpenAI Batch API JSONL input file.
/// See: https://platform.openai.com/docs/api-reference/batch
/// </summary>
internal sealed class BatchRequestLine
{
    [JsonPropertyName("custom_id")]
    public string CustomId { get; init; } = string.Empty;

    [JsonPropertyName("method")]
    public string Method { get; init; } = "POST";

    [JsonPropertyName("url")]
    public string Url { get; init; } = "/v1/chat/completions";

    [JsonPropertyName("body")]
    public BatchRequestBody Body { get; init; } = new();
}

internal sealed class BatchRequestBody
{
    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;

    [JsonPropertyName("messages")]
    public List<BatchChatMessage> Messages { get; init; } = [];

    [JsonPropertyName("response_format")]
    public BatchResponseFormat ResponseFormat { get; init; } = new();
}

internal sealed class BatchChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;
}

internal sealed class BatchResponseFormat
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "json_schema";

    [JsonPropertyName("json_schema")]
    public BatchJsonSchemaRef JsonSchema { get; init; } = new();
}

internal sealed class BatchJsonSchemaRef
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("strict")]
    public bool Strict { get; init; }

    [JsonPropertyName("schema")]
    public object? Schema { get; init; }
}

// ── OpenAI Batch API creation request ────────────────────────────────────────

/// <summary>
/// Request body for POST /v1/batches (creating a new batch).
/// </summary>
internal sealed class CreateBatchRequest
{
    [JsonPropertyName("input_file_id")]
    public string InputFileId { get; init; } = string.Empty;

    [JsonPropertyName("endpoint")]
    public string Endpoint { get; init; } = "/v1/chat/completions";

    [JsonPropertyName("completion_window")]
    public string CompletionWindow { get; init; } = "24h";

    [JsonPropertyName("metadata")]
    public Dictionary<string, string>? Metadata { get; init; }
}

// ── OpenAI Batch API status response ─────────────────────────────────────────

/// <summary>
/// Deserialization target for the OpenAI Batch object returned by GET /v1/batches/{id}.
/// The OpenAI SDK v2.9.1 marks Status/RequestCounts as internal, so we deserialize the raw JSON.
/// </summary>
internal sealed class OpenAiBatchResponse
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("output_file_id")]
    public string? OutputFileId { get; init; }

    [JsonPropertyName("error_file_id")]
    public string? ErrorFileId { get; init; }

    [JsonPropertyName("request_counts")]
    public OpenAiBatchRequestCounts? RequestCounts { get; init; }
}

internal sealed class OpenAiBatchRequestCounts
{
    [JsonPropertyName("completed")]
    public int Completed { get; init; }

    [JsonPropertyName("failed")]
    public int Failed { get; init; }

    [JsonPropertyName("total")]
    public int Total { get; init; }
}

// ── OpenAI Batch API output line (JSONL output) ─────────────────────────────

/// <summary>
/// A single line in the OpenAI Batch API JSONL output file.
/// </summary>
internal sealed class BatchOutputLine
{
    [JsonPropertyName("custom_id")]
    public string CustomId { get; init; } = string.Empty;

    [JsonPropertyName("error")]
    public BatchOutputError? Error { get; init; }

    [JsonPropertyName("response")]
    public BatchOutputResponse? Response { get; init; }
}

internal sealed class BatchOutputError
{
    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [JsonPropertyName("code")]
    public string? Code { get; init; }
}

internal sealed class BatchOutputResponse
{
    [JsonPropertyName("status_code")]
    public int StatusCode { get; init; }

    [JsonPropertyName("body")]
    public BatchOutputResponseBody? Body { get; init; }
}

internal sealed class BatchOutputResponseBody
{
    [JsonPropertyName("choices")]
    public List<BatchOutputChoice>? Choices { get; init; }
}

internal sealed class BatchOutputChoice
{
    [JsonPropertyName("message")]
    public BatchOutputMessage? Message { get; init; }
}

internal sealed class BatchOutputMessage
{
    [JsonPropertyName("content")]
    public string? Content { get; init; }
}
