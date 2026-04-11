using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using OpenAI;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Services;
using StarWarsData.Tests.Infrastructure;

namespace StarWarsData.Tests.Integration;

/// <summary>
/// Robustness tests for the relationship graph batch processing pipeline.
/// Uses a fake IChatClient that returns structured JSON to exercise the full
/// single-turn extraction orchestration without hitting a real LLM.
/// </summary>
[TestClass]
[TestCategory(TestTiers.Integration)]
[DoNotParallelize]
public class RelationshipGraphPipelineTests
{
    [ClassInitialize]
    public static async Task ClassSetup(TestContext _) => await ApiFixture.EnsureInitializedAsync();

    private static IMongoCollection<RelationshipCrawlState> CrawlState => ApiFixture.MongoClient.GetDatabase(ApiFixture.DatabaseName).GetCollection<RelationshipCrawlState>(Collections.KgCrawlState);

    private static IMongoCollection<RelationshipEdge> Edges => ApiFixture.MongoClient.GetDatabase(ApiFixture.DatabaseName).GetCollection<RelationshipEdge>(Collections.KgEdges);

    private static RelationshipGraphBuilderService CreateService(IChatClient chatClient)
    {
        var settings = Options.Create(new SettingsOptions { DatabaseName = ApiFixture.DatabaseName });

        var openAiClient = new OpenAIClient("sk-test-dummy");

        return new RelationshipGraphBuilderService(
            NullLogger<RelationshipGraphBuilderService>.Instance,
            settings,
            ApiFixture.MongoClient,
            ApiFixture.RelationshipAnalystToolkit,
            openAiClient,
            new OpenAiStatusService(NullLogger<OpenAiStatusService>.Instance),
            chatClient
        );
    }

    private static async Task CleanGraphState()
    {
        await CrawlState.DeleteManyAsync(Builders<RelationshipCrawlState>.Filter.Empty);
    }

    private static string SkipJson(string reason = "Test skip") =>
        JsonSerializer.Serialize(
            new
            {
                shouldSkip = true,
                skipReason = reason,
                edges = Array.Empty<object>(),
            }
        );

    private static string EdgeJson(int fromId, string fromName, string fromType, int toId, string toName, string toType, string label, string reverseLabel) =>
        JsonSerializer.Serialize(
            new
            {
                shouldSkip = false,
                skipReason = (string?)null,
                edges = new[]
                {
                    new
                    {
                        fromId,
                        fromName,
                        fromType,
                        toId,
                        toName,
                        toType,
                        label,
                        reverseLabel,
                        weight = 0.9,
                        evidence = "Test evidence",
                        continuity = "Legends",
                    },
                },
            }
        );

    [TestMethod]
    public async Task ProcessBatch_ExtractsEdges_ViaStructuredOutput()
    {
        await CleanGraphState();

        var fake = new FakeChatClient(
            (messages, options, ct) =>
            {
                var json = EdgeJson(1, "Luke Skywalker", "Character", 2, "Anakin Skywalker", "Character", "child_of", "parent_of");
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));
            }
        );

        var service = CreateService(fake);
        await service.ProcessBatchAsync(batchSize: 1);

        var completed = await CrawlState.Find(Builders<RelationshipCrawlState>.Filter.Eq(s => s.Status, CrawlStatus.Completed)).ToListAsync();

        Assert.IsTrue(completed.Count >= 1, "At least one page should be marked Completed");
    }

    [TestMethod]
    public async Task ProcessBatch_SkipsPagesViaStructuredOutput()
    {
        await CleanGraphState();

        var fake = new FakeChatClient(
            (messages, options, ct) =>
            {
                var json = SkipJson("No meaningful relationships");
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));
            }
        );

        var service = CreateService(fake);
        await service.ProcessBatchAsync(batchSize: 3);

        var skipped = await CrawlState.Find(Builders<RelationshipCrawlState>.Filter.Eq(s => s.Status, CrawlStatus.Skipped)).ToListAsync();

        Assert.IsTrue(skipped.Count >= 1, "At least one page should be skipped");
        var validIds = new HashSet<int> { 1, 2, 3, 100, 200, 400, 500, 501 };
        foreach (var s in skipped)
            Assert.IsTrue(validIds.Contains(s.PageId));
    }

    [TestMethod]
    public async Task ProcessBatch_LlmThrows_MarksPageAsFailedAndContinues()
    {
        await CleanGraphState();

        var callCount = 0;
        var fake = new FakeChatClient(
            (messages, options, ct) =>
            {
                var count = Interlocked.Increment(ref callCount);

                if (count == 1)
                    throw new InvalidOperationException("Simulated LLM timeout");

                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, SkipJson("Test"))));
            }
        );

        var service = CreateService(fake);
        await service.ProcessBatchAsync(batchSize: 5);

        var failed = await CrawlState.Find(Builders<RelationshipCrawlState>.Filter.Eq(s => s.Status, CrawlStatus.Failed)).ToListAsync();

        Assert.IsTrue(failed.Count >= 1, "At least one page should be marked Failed");
        Assert.IsNotNull(failed[0].Error);
        Assert.IsTrue(failed[0].Error!.Contains("Simulated LLM timeout"));

        Assert.IsTrue(callCount > 1, "Batch should continue processing after a failure");
    }

    [TestMethod]
    public async Task ProcessBatch_RecoversStalePagesFromPreviousRun()
    {
        await CleanGraphState();

        var staleTime = DateTime.UtcNow.AddMinutes(-15);
        await CrawlState.InsertManyAsync(
            [
                new RelationshipCrawlState
                {
                    PageId = 1,
                    Name = "Luke Skywalker",
                    Status = CrawlStatus.Processing,
                    ProcessedAt = staleTime,
                },
                new RelationshipCrawlState
                {
                    PageId = 2,
                    Name = "Anakin Skywalker",
                    Status = CrawlStatus.Processing,
                    ProcessedAt = staleTime,
                },
            ]
        );

        var preCount = await CrawlState.CountDocumentsAsync(Builders<RelationshipCrawlState>.Filter.Eq(s => s.Status, CrawlStatus.Processing));
        Assert.AreEqual(2, preCount);

        var fake = new FakeChatClient((_, _, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, SkipJson("Test")))));
        var service = CreateService(fake);

        await service.ProcessBatchAsync(batchSize: 1);

        var staleRemaining = await CrawlState.CountDocumentsAsync(
            Builders<RelationshipCrawlState>.Filter.And(
                Builders<RelationshipCrawlState>.Filter.Lt(s => s.ProcessedAt, DateTime.UtcNow.AddMinutes(-10)),
                Builders<RelationshipCrawlState>.Filter.Eq(s => s.Status, CrawlStatus.Processing)
            )
        );

        Assert.AreEqual(0, staleRemaining);
    }

    [TestMethod]
    public async Task ProcessBatch_RespectsCancellation_StopsEarly()
    {
        await CleanGraphState();

        using var cts = new CancellationTokenSource();

        var fake = new FakeChatClient(
            (messages, options, ct) =>
            {
                cts.Cancel();
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, SkipJson("Test"))));
            }
        );

        var service = CreateService(fake);
        await service.ProcessBatchAsync(batchSize: 100, ct: cts.Token);

        Assert.IsTrue(fake.CallCount <= 10, $"Expected cancellation to limit LLM calls but got {fake.CallCount}");
    }

    [TestMethod]
    public async Task ProcessBatch_SkipsAlreadyProcessedPages()
    {
        await CleanGraphState();

        // Query the live pages collection rather than hard-coding IDs — other integration
        // test classes (e.g. RelationshipAnalystToolkitTests) may have upserted extra
        // infobox-bearing pages into the shared ApiFixture, and any candidate left
        // unmarked here would force a real LLM call and break the assertion below.
        var pagesCollection = ApiFixture.MongoClient.GetDatabase(ApiFixture.DatabaseName).GetCollection<Page>(Collections.Pages);

        var candidatePageIds = await pagesCollection.Find(Builders<Page>.Filter.Ne(p => p.Infobox, null!)).Project(p => p.PageId).ToListAsync();

        await CrawlState.InsertManyAsync(
            candidatePageIds.Select(id => new RelationshipCrawlState
            {
                PageId = id,
                Name = $"Page {id}",
                Type = "Character",
                Status = CrawlStatus.Completed,
                ProcessedAt = DateTime.UtcNow,
                Version = 1,
            })
        );

        var fake = new FakeChatClient((_, _, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, SkipJson("Test")))));
        var service = CreateService(fake);

        await service.ProcessBatchAsync(batchSize: 100);

        Assert.AreEqual(0, fake.CallCount);
    }

    private sealed class FakeChatClient : IChatClient
    {
        private readonly Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>> _handler;
        public int CallCount;

        public FakeChatClient(Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>> handler)
        {
            _handler = handler;
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref CallCount);
            return _handler(chatMessages, options, cancellationToken);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
