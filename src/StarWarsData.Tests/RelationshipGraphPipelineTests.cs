using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Services;

namespace StarWarsData.Tests;

/// <summary>
/// Robustness tests for the relationship graph batch processing pipeline.
/// Uses a fake IChatClient that returns structured JSON to exercise the full
/// single-turn extraction orchestration without hitting a real LLM.
/// </summary>
[Collection("Api")]
public class RelationshipGraphPipelineTests(ApiFixture fixture)
{
    private IMongoCollection<RelationshipCrawlState> CrawlState =>
        fixture.MongoClient
            .GetDatabase(ApiFixture.RelationshipGraphDb)
            .GetCollection<RelationshipCrawlState>("crawl_state");

    private IMongoCollection<RelationshipEdge> Edges =>
        fixture.MongoClient
            .GetDatabase(ApiFixture.RelationshipGraphDb)
            .GetCollection<RelationshipEdge>("edges");

    private RelationshipGraphBuilderService CreateService(IChatClient chatClient)
    {
        var settings = Options.Create(new SettingsOptions
        {
            PagesDb = ApiFixture.PagesDb,
            RelationshipGraphDb = ApiFixture.RelationshipGraphDb,
        });

        return new RelationshipGraphBuilderService(
            NullLogger<RelationshipGraphBuilderService>.Instance,
            settings,
            fixture.MongoClient,
            fixture.RelationshipAnalystToolkit,
            chatClient);
    }

    private async Task CleanGraphState()
    {
        await CrawlState.DeleteManyAsync(Builders<RelationshipCrawlState>.Filter.Empty);
    }

    // Helper: build a skip response JSON
    private static string SkipJson(string reason = "Test skip") =>
        JsonSerializer.Serialize(new { shouldSkip = true, skipReason = reason, edges = Array.Empty<object>() });

    // Helper: build an edge extraction response JSON
    private static string EdgeJson(int fromId, string fromName, string fromType,
        int toId, string toName, string toType, string label, string reverseLabel) =>
        JsonSerializer.Serialize(new
        {
            shouldSkip = false,
            skipReason = (string?)null,
            edges = new[]
            {
                new
                {
                    fromId, fromName, fromType,
                    toId, toName, toType,
                    label, reverseLabel,
                    weight = 0.9,
                    evidence = "Test evidence",
                    continuity = "Legends",
                },
            },
        });

    // ── Full pipeline: structured JSON extraction with edges ─────────────

    [Fact]
    public async Task ProcessBatch_ExtractsEdges_ViaStructuredOutput()
    {
        await CleanGraphState();

        var fake = new FakeChatClient((messages, options, ct) =>
        {
            // Return structured JSON with an edge for every page
            // Luke (1) -> Anakin (2) relationship
            var json = EdgeJson(1, "Luke Skywalker", "Character",
                2, "Anakin Skywalker", "Character", "child_of", "parent_of");

            return Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, json)));
        });

        var service = CreateService(fake);
        await service.ProcessBatchAsync(batchSize: 1);

        // Page should be marked as Completed
        var completed = await CrawlState
            .Find(Builders<RelationshipCrawlState>.Filter.Eq(s => s.Status, CrawlStatus.Completed))
            .ToListAsync();

        Assert.True(completed.Count >= 1, "At least one page should be marked Completed");
    }

    // ── Skip pages via structured output ────────────────────────────────

    [Fact]
    public async Task ProcessBatch_SkipsPagesViaStructuredOutput()
    {
        await CleanGraphState();

        var fake = new FakeChatClient((messages, options, ct) =>
        {
            var json = SkipJson("No meaningful relationships");
            return Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, json)));
        });

        var service = CreateService(fake);
        await service.ProcessBatchAsync(batchSize: 3);

        var skipped = await CrawlState
            .Find(Builders<RelationshipCrawlState>.Filter.Eq(s => s.Status, CrawlStatus.Skipped))
            .ToListAsync();

        Assert.True(skipped.Count >= 1, "At least one page should be skipped");
        Assert.All(skipped, s => Assert.Contains(s.PageId, new[] { 1, 2, 3, 100, 200, 400, 500, 501 }));
    }

    // ── Error recovery: LLM failure marks page as Failed, batch continues ───

    [Fact]
    public async Task ProcessBatch_LlmThrows_MarksPageAsFailedAndContinues()
    {
        await CleanGraphState();

        var callCount = 0;
        var fake = new FakeChatClient((messages, options, ct) =>
        {
            var count = Interlocked.Increment(ref callCount);

            // First page: LLM throws
            if (count == 1)
                throw new InvalidOperationException("Simulated LLM timeout");

            // Subsequent pages: return valid skip JSON
            return Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, SkipJson("Test"))));
        });

        var service = CreateService(fake);
        await service.ProcessBatchAsync(batchSize: 5);

        // At least one page should be marked as Failed
        var failed = await CrawlState
            .Find(Builders<RelationshipCrawlState>.Filter.Eq(s => s.Status, CrawlStatus.Failed))
            .ToListAsync();

        Assert.True(failed.Count >= 1, "At least one page should be marked Failed");
        Assert.Contains("Simulated LLM timeout", failed[0].Error);

        // Batch should have continued past the failure
        Assert.True(callCount > 1, "Batch should continue processing after a failure");
    }

    // ── Stale recovery: orphaned Processing entries are cleaned up ───────────

    [Fact]
    public async Task ProcessBatch_RecoversStalePagesFromPreviousRun()
    {
        await CleanGraphState();

        // Insert stale "Processing" entries simulating a crashed previous batch
        var staleTime = DateTime.UtcNow.AddMinutes(-15);
        await CrawlState.InsertManyAsync([
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
        ]);

        // Verify stale entries exist
        var preCount = await CrawlState.CountDocumentsAsync(
            Builders<RelationshipCrawlState>.Filter.Eq(s => s.Status, CrawlStatus.Processing));
        Assert.Equal(2, preCount);

        var fake = new FakeChatClient((_, _, _) =>
            Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, SkipJson("Test")))));
        var service = CreateService(fake);

        await service.ProcessBatchAsync(batchSize: 1);

        // Stale entries should have been deleted (allowing re-processing)
        var staleRemaining = await CrawlState.CountDocumentsAsync(
            Builders<RelationshipCrawlState>.Filter.And(
                Builders<RelationshipCrawlState>.Filter.Lt(s => s.ProcessedAt, DateTime.UtcNow.AddMinutes(-10)),
                Builders<RelationshipCrawlState>.Filter.Eq(s => s.Status, CrawlStatus.Processing)));

        Assert.Equal(0, staleRemaining);
    }

    // ── Cancellation: batch stops processing when token is signaled ─────────

    [Fact]
    public async Task ProcessBatch_RespectsCancellation_StopsEarly()
    {
        await CleanGraphState();

        using var cts = new CancellationTokenSource();

        var fake = new FakeChatClient((messages, options, ct) =>
        {
            cts.Cancel(); // Signal cancellation after first LLM call
            return Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, SkipJson("Test"))));
        });

        var service = CreateService(fake);
        await service.ProcessBatchAsync(batchSize: 100, ct: cts.Token);

        // Should have stopped early - batch has many candidates but only ~1 LLM call made
        Assert.True(fake.CallCount <= 2,
            $"Expected at most 2 LLM calls with early cancellation but got {fake.CallCount}");
    }

    // ── Dedup: already-processed pages are not sent to the LLM again ────────

    [Fact]
    public async Task ProcessBatch_SkipsAlreadyProcessedPages()
    {
        await CleanGraphState();

        // Pre-mark all seed pages (with infoboxes) as Completed
        var seededPageIds = new[] { 1, 2, 3, 100, 200, 400, 500, 501 };
        await CrawlState.InsertManyAsync(seededPageIds.Select(id => new RelationshipCrawlState
        {
            PageId = id,
            Name = $"Page {id}",
            Type = "Character",
            Status = CrawlStatus.Completed,
            ProcessedAt = DateTime.UtcNow,
            Version = 1,
        }));

        var fake = new FakeChatClient((_, _, _) =>
            Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, SkipJson("Test")))));
        var service = CreateService(fake);

        await service.ProcessBatchAsync(batchSize: 100);

        // LLM should not have been called - all pages already processed
        Assert.Equal(0, fake.CallCount);
    }

    // ── Fake IChatClient ────────────────────────────────────────────────

    private sealed class FakeChatClient : IChatClient
    {
        private readonly Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>> _handler;
        public int CallCount;

        public FakeChatClient(
            Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>> handler)
        {
            _handler = handler;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref CallCount);
            return _handler(chatMessages, options, cancellationToken);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
