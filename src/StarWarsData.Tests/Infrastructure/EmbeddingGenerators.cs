using Microsoft.Extensions.AI;

namespace StarWarsData.Tests.Infrastructure;

/// <summary>
/// No-op embedding generator for tests that don't need real vectors.
/// </summary>
public sealed class NoOpEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public EmbeddingGeneratorMetadata Metadata { get; } = new("no-op");

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var embeddings = values.Select(_ => new Embedding<float>(new float[] { 0f })).ToList();
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>
/// Deterministic fake embedding generator that returns hash-derived 1536-dim vectors.
/// Optionally throws a token-limit error for inputs containing a chosen substring.
/// </summary>
public sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public EmbeddingGeneratorMetadata Metadata { get; } = new("fake-embedder");

    public string? FailOnSubstring { get; set; }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var embeddings = new List<Embedding<float>>();
        foreach (var value in values)
        {
            if (FailOnSubstring is not null && value.Contains(FailOnSubstring))
                throw new InvalidOperationException(
                    "This model's maximum context length is 8192 tokens, however you requested 99999 tokens (99999 in your prompt; 0 for the completion). Please reduce your prompt; or completion length."
                );

            var hash = value.GetHashCode();
            var vector = new float[1536];
            var rng = new Random(hash);
            for (int i = 0; i < vector.Length; i++)
                vector[i] = (float)(rng.NextDouble() * 2 - 1);
            embeddings.Add(new Embedding<float>(vector));
        }
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
