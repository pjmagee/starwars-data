using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAI;
using StarWarsData.Models;
using StarWarsData.Services;

namespace StarWarsData.Tests;

/// <summary>
/// Validates the OpenAI Batch API JSONL format produced by
/// RelationshipGraphBuilderService — schema, line format,
/// encoding (no BOM), and line endings (\n not \r\n).
/// </summary>
[Collection("Api")]
public class BatchApiFormatTests(ApiFixture fixture)
{
    static readonly JsonSerializerOptions CaseInsensitiveOpts = new() { PropertyNameCaseInsensitive = true };
    private RelationshipGraphBuilderService CreateService()
    {
        var settings = Options.Create(
            new SettingsOptions
            {
                DatabaseName = ApiFixture.DatabaseName,
                RelationshipAnalystModel = "gpt-5.4-mini",
            }
        );

        var fakeChatClient = new NoOpChatClient();

        return new RelationshipGraphBuilderService(
            NullLogger<RelationshipGraphBuilderService>.Instance,
            settings,
            fixture.MongoClient,
            fixture.RelationshipAnalystToolkit,
            new OpenAIClient("sk-test-dummy"),
            new OpenAiStatusService(NullLogger<OpenAiStatusService>.Instance),
            fakeChatClient
        );
    }

    [Fact]
    public void BuildJsonSchema_ReturnsValidJsonWithRequiredFields()
    {
        var schema = RelationshipGraphBuilderService.BuildJsonSchema();

        Assert.Equal(JsonValueKind.Object, schema.ValueKind);
        Assert.True(schema.TryGetProperty("properties", out var props));
        Assert.True(props.TryGetProperty("shouldSkip", out _));
        Assert.True(props.TryGetProperty("skipReason", out _));
        Assert.True(props.TryGetProperty("edges", out _));

        // required array must list all three top-level fields
        var required = schema.GetProperty("required");
        var requiredNames = required.EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("shouldSkip", requiredNames);
        Assert.Contains("skipReason", requiredNames);
        Assert.Contains("edges", requiredNames);

        // additionalProperties must be false (strict mode)
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());

        // edges items must have all ExtractedEdge fields
        var edgeProps = props.GetProperty("edges").GetProperty("items").GetProperty("properties");
        foreach (
            var field in new[]
            {
                "fromId",
                "fromName",
                "fromType",
                "toId",
                "toName",
                "toType",
                "label",
                "reverseLabel",
                "weight",
                "evidence",
                "continuity",
            }
        )
        {
            Assert.True(edgeProps.TryGetProperty(field, out _), $"Missing edge field: {field}");
        }
    }

    [Fact]
    public void BuildBatchRequestLine_ProducesValidJsonl()
    {
        var service = CreateService();
        var schema = RelationshipGraphBuilderService.BuildJsonSchema();

        var line = service.BuildBatchRequestLine("page-42", "Some prompt content", schema);

        // Must be valid JSON (single line, no newlines in the middle)
        Assert.DoesNotContain("\n", line);
        Assert.DoesNotContain("\r", line);

        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        // Required top-level fields per OpenAI Batch API spec
        Assert.Equal("page-42", root.GetProperty("custom_id").GetString());
        Assert.Equal("POST", root.GetProperty("method").GetString());
        Assert.Equal("/v1/chat/completions", root.GetProperty("url").GetString());

        // Body must have model, messages, response_format
        var body = root.GetProperty("body");
        Assert.Equal("gpt-5.4-mini", body.GetProperty("model").GetString());

        var messages = body.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("Some prompt content", messages[1].GetProperty("content").GetString());

        // response_format must be json_schema with strict mode
        var rf = body.GetProperty("response_format");
        Assert.Equal("json_schema", rf.GetProperty("type").GetString());
        var js = rf.GetProperty("json_schema");
        Assert.Equal("relationship_extraction", js.GetProperty("name").GetString());
        Assert.True(js.GetProperty("strict").GetBoolean());
    }

    [Fact]
    public void BuildBatchRequestLine_UsesSnakeCaseNaming()
    {
        var service = CreateService();
        var schema = RelationshipGraphBuilderService.BuildJsonSchema();

        var line = service.BuildBatchRequestLine("page-1", "test", schema);

        // Verify snake_case: custom_id not customId, response_format not responseFormat
        Assert.Contains("\"custom_id\"", line);
        Assert.Contains("\"response_format\"", line);
        Assert.Contains("\"json_schema\"", line);
        Assert.DoesNotContain("\"customId\"", line);
        Assert.DoesNotContain("\"responseFormat\"", line);
        Assert.DoesNotContain("\"jsonSchema\"", line);
    }

    [Fact]
    public async Task JsonlFile_HasNoByteOrderMark()
    {
        // Simulate writing JSONL the same way SubmitSingleBatchAsync does
        var tempPath = Path.GetTempFileName();
        try
        {
            await using (var fs = new FileStream(tempPath, FileMode.Create))
            await using (
                var writer = new StreamWriter(fs, new UTF8Encoding(false)) { NewLine = "\n" }
            )
            {
                await writer.WriteLineAsync("{\"custom_id\":\"page-1\",\"test\":true}");
                await writer.WriteLineAsync("{\"custom_id\":\"page-2\",\"test\":true}");
            }

            var bytes = await File.ReadAllBytesAsync(tempPath);

            // UTF-8 BOM is 0xEF 0xBB 0xBF — must not be present
            Assert.False(
                bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                "File starts with UTF-8 BOM — OpenAI will reject this"
            );

            // First byte should be '{' (0x7B)
            Assert.Equal((byte)'{', bytes[0]);

            // Must use \n not \r\n
            var content = Encoding.UTF8.GetString(bytes);
            Assert.DoesNotContain("\r\n", content);
            Assert.Contains("\n", content);

            // Each line must be valid JSON
            var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, lines.Length);
            foreach (var line in lines)
            {
                var doc = JsonDocument.Parse(line);
                Assert.NotNull(doc);
            }
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    /// <summary>Minimal IChatClient — batch tests don't call the LLM.</summary>
    private sealed class NoOpChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    [Fact]
    public void BuildJsonSchema_MatchesRelationshipExtractionResponse()
    {
        // Verify the schema can validate a sample response
        var schema = RelationshipGraphBuilderService.BuildJsonSchema();
        var sampleResponse = JsonSerializer.Serialize(
            new
            {
                shouldSkip = false,
                skipReason = (string?)null,
                edges = new[]
                {
                    new
                    {
                        fromId = 1,
                        fromName = "Luke Skywalker",
                        fromType = "Character",
                        toId = 2,
                        toName = "Darth Vader",
                        toType = "Character",
                        label = "son_of",
                        reverseLabel = "father_of",
                        weight = 0.95,
                        evidence = "I am your father",
                        continuity = "Canon",
                    },
                },
            }
        );

        // Parse the sample — if schema and response use the same field names, this validates compatibility
        using var doc = JsonDocument.Parse(sampleResponse);
        var root = doc.RootElement;
        var schemaProps = schema.GetProperty("properties");

        // Every field in the response should be in the schema
        foreach (var prop in root.EnumerateObject())
        {
            Assert.True(
                schemaProps.TryGetProperty(prop.Name, out _),
                $"Response field '{prop.Name}' not in schema"
            );
        }
    }

    [Fact]
    public void BatchOutputLine_DeserializesSuccessResponse()
    {
        var json = """
            {
                "custom_id": "page-42",
                "response": {
                    "status_code": 200,
                    "body": {
                        "choices": [
                            {
                                "message": {
                                    "content": "{\"shouldSkip\":false,\"edges\":[]}"
                                }
                            }
                        ]
                    }
                },
                "error": null
            }
            """;

        var result = JsonSerializer.Deserialize<BatchOutputLine>(json, CaseInsensitiveOpts);

        Assert.NotNull(result);
        Assert.Equal("page-42", result.CustomId);
        Assert.Null(result.Error);
        Assert.NotNull(result.Response);
        Assert.Equal(200, result.Response.StatusCode);
        Assert.Equal(
            "{\"shouldSkip\":false,\"edges\":[]}",
            result.Response.Body?.Choices?.First().Message?.Content
        );
    }

    [Fact]
    public void BatchOutputLine_DeserializesErrorResponse()
    {
        var json = """
            {
                "custom_id": "page-99",
                "response": null,
                "error": {
                    "message": "Rate limit exceeded",
                    "code": "rate_limit_exceeded"
                }
            }
            """;

        var result = JsonSerializer.Deserialize<BatchOutputLine>(json, CaseInsensitiveOpts);

        Assert.NotNull(result);
        Assert.Equal("page-99", result.CustomId);
        Assert.NotNull(result.Error);
        Assert.Equal("Rate limit exceeded", result.Error.Message);
        Assert.Equal("rate_limit_exceeded", result.Error.Code);
        Assert.Null(result.Response);
    }

    [Fact]
    public void OpenAiBatchResponse_DeserializesFromApiJson()
    {
        var json = """
            {
                "id": "batch_abc123",
                "status": "completed",
                "output_file_id": "file-xyz",
                "error_file_id": null,
                "request_counts": {
                    "completed": 95,
                    "failed": 5,
                    "total": 100
                }
            }
            """;

        var result = JsonSerializer.Deserialize<OpenAiBatchResponse>(json, CaseInsensitiveOpts);

        Assert.NotNull(result);
        Assert.Equal("batch_abc123", result.Id);
        Assert.Equal("completed", result.Status);
        Assert.Equal("file-xyz", result.OutputFileId);
        Assert.Null(result.ErrorFileId);
        Assert.NotNull(result.RequestCounts);
        Assert.Equal(95, result.RequestCounts.Completed);
        Assert.Equal(5, result.RequestCounts.Failed);
        Assert.Equal(100, result.RequestCounts.Total);
    }
}
