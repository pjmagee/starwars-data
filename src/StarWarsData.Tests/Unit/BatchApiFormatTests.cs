using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using OpenAI;
using StarWarsData.Models;
using StarWarsData.Services;

namespace StarWarsData.Tests.Unit;

/// <summary>
/// Validates the OpenAI Batch API JSONL format produced by
/// <see cref="RelationshipGraphBuilderService"/> — schema, line format,
/// encoding (no BOM), and line endings (\n not \r\n).
///
/// These tests construct the service with a non-connecting placeholder
/// <see cref="MongoClient"/> because the methods under test do no I/O.
/// </summary>
[TestClass]
[TestCategory(TestTiers.Unit)]
public class BatchApiFormatTests
{
    private static readonly JsonSerializerOptions CaseInsensitiveOpts = new() { PropertyNameCaseInsensitive = true };

    private static RelationshipGraphBuilderService CreateService()
    {
        var settings = Options.Create(new SettingsOptions { DatabaseName = "test-batch-format", RelationshipAnalystModel = "gpt-5.4-mini" });

        // MongoClient construction is lazy — no connection is made until a real
        // operation runs. None of the methods exercised below touch the DB.
        var placeholderClient = new MongoClient("mongodb://localhost:27017");
        var toolkit = new RelationshipAnalystToolkit(placeholderClient, "test-batch-format");

        return new RelationshipGraphBuilderService(
            NullLogger<RelationshipGraphBuilderService>.Instance,
            settings,
            placeholderClient,
            toolkit,
            new OpenAIClient("sk-test-dummy"),
            new OpenAiStatusService(NullLogger<OpenAiStatusService>.Instance),
            new NoOpChatClient()
        );
    }

    [TestMethod]
    public void BuildJsonSchema_ReturnsValidJsonWithRequiredFields()
    {
        var schema = RelationshipGraphBuilderService.BuildJsonSchema();

        Assert.AreEqual(JsonValueKind.Object, schema.ValueKind);
        Assert.IsTrue(schema.TryGetProperty("properties", out var props));
        Assert.IsTrue(props.TryGetProperty("shouldSkip", out _));
        Assert.IsTrue(props.TryGetProperty("skipReason", out _));
        Assert.IsTrue(props.TryGetProperty("edges", out _));

        var required = schema.GetProperty("required");
        var requiredNames = required.EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.IsTrue(requiredNames.Contains("shouldSkip"));
        Assert.IsTrue(requiredNames.Contains("skipReason"));
        Assert.IsTrue(requiredNames.Contains("edges"));

        Assert.IsFalse(schema.GetProperty("additionalProperties").GetBoolean());

        var edgeProps = props.GetProperty("edges").GetProperty("items").GetProperty("properties");
        foreach (var field in new[] { "fromId", "fromName", "fromType", "toId", "toName", "toType", "label", "reverseLabel", "weight", "evidence", "continuity" })
        {
            Assert.IsTrue(edgeProps.TryGetProperty(field, out _), $"Missing edge field: {field}");
        }
    }

    [TestMethod]
    public void BuildBatchRequestLine_ProducesValidJsonl()
    {
        var service = CreateService();
        var schema = RelationshipGraphBuilderService.BuildJsonSchema();

        var line = service.BuildBatchRequestLine("page-42", "Some prompt content", schema);

        Assert.IsFalse(line.Contains('\n'));
        Assert.IsFalse(line.Contains('\r'));

        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        Assert.AreEqual("page-42", root.GetProperty("custom_id").GetString());
        Assert.AreEqual("POST", root.GetProperty("method").GetString());
        Assert.AreEqual("/v1/chat/completions", root.GetProperty("url").GetString());

        var body = root.GetProperty("body");
        Assert.AreEqual("gpt-5.4-mini", body.GetProperty("model").GetString());

        var messages = body.GetProperty("messages");
        Assert.AreEqual(2, messages.GetArrayLength());
        Assert.AreEqual("system", messages[0].GetProperty("role").GetString());
        Assert.AreEqual("user", messages[1].GetProperty("role").GetString());
        Assert.AreEqual("Some prompt content", messages[1].GetProperty("content").GetString());

        var rf = body.GetProperty("response_format");
        Assert.AreEqual("json_schema", rf.GetProperty("type").GetString());
        var js = rf.GetProperty("json_schema");
        Assert.AreEqual("relationship_extraction", js.GetProperty("name").GetString());
        Assert.IsTrue(js.GetProperty("strict").GetBoolean());
    }

    [TestMethod]
    public void BuildBatchRequestLine_UsesSnakeCaseNaming()
    {
        var service = CreateService();
        var schema = RelationshipGraphBuilderService.BuildJsonSchema();

        var line = service.BuildBatchRequestLine("page-1", "test", schema);

        Assert.IsTrue(line.Contains("\"custom_id\""));
        Assert.IsTrue(line.Contains("\"response_format\""));
        Assert.IsTrue(line.Contains("\"json_schema\""));
        Assert.IsFalse(line.Contains("\"customId\""));
        Assert.IsFalse(line.Contains("\"responseFormat\""));
        Assert.IsFalse(line.Contains("\"jsonSchema\""));
    }

    [TestMethod]
    public async Task JsonlFile_HasNoByteOrderMark()
    {
        var tempPath = Path.GetTempFileName();
        try
        {
            await using (var fs = new FileStream(tempPath, FileMode.Create))
            await using (var writer = new StreamWriter(fs, new UTF8Encoding(false)) { NewLine = "\n" })
            {
                await writer.WriteLineAsync("{\"custom_id\":\"page-1\",\"test\":true}");
                await writer.WriteLineAsync("{\"custom_id\":\"page-2\",\"test\":true}");
            }

            var bytes = await File.ReadAllBytesAsync(tempPath);

            Assert.IsFalse(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "File starts with UTF-8 BOM — OpenAI will reject this");

            Assert.AreEqual((byte)'{', bytes[0]);

            var content = Encoding.UTF8.GetString(bytes);
            Assert.IsFalse(content.Contains("\r\n"));
            Assert.IsTrue(content.Contains('\n'));

            var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.AreEqual(2, lines.Length);
            foreach (var line in lines)
            {
                var doc = JsonDocument.Parse(line);
                Assert.IsNotNull(doc);
            }
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    private sealed class NoOpChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    [TestMethod]
    public void BuildJsonSchema_MatchesRelationshipExtractionResponse()
    {
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

        using var doc = JsonDocument.Parse(sampleResponse);
        var root = doc.RootElement;
        var schemaProps = schema.GetProperty("properties");

        foreach (var prop in root.EnumerateObject())
        {
            Assert.IsTrue(schemaProps.TryGetProperty(prop.Name, out _), $"Response field '{prop.Name}' not in schema");
        }
    }

    [TestMethod]
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

        Assert.IsNotNull(result);
        Assert.AreEqual("page-42", result.CustomId);
        Assert.IsNull(result.Error);
        Assert.IsNotNull(result.Response);
        Assert.AreEqual(200, result.Response.StatusCode);
        Assert.AreEqual("{\"shouldSkip\":false,\"edges\":[]}", result.Response.Body?.Choices?.First().Message?.Content);
    }

    [TestMethod]
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

        Assert.IsNotNull(result);
        Assert.AreEqual("page-99", result.CustomId);
        Assert.IsNotNull(result.Error);
        Assert.AreEqual("Rate limit exceeded", result.Error.Message);
        Assert.AreEqual("rate_limit_exceeded", result.Error.Code);
        Assert.IsNull(result.Response);
    }

    [TestMethod]
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

        Assert.IsNotNull(result);
        Assert.AreEqual("batch_abc123", result.Id);
        Assert.AreEqual("completed", result.Status);
        Assert.AreEqual("file-xyz", result.OutputFileId);
        Assert.IsNull(result.ErrorFileId);
        Assert.IsNotNull(result.RequestCounts);
        Assert.AreEqual(95, result.RequestCounts.Completed);
        Assert.AreEqual(5, result.RequestCounts.Failed);
        Assert.AreEqual(100, result.RequestCounts.Total);
    }
}
