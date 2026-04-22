using OpenAI.Embeddings;

namespace DistributedDebugger.Tools.CloudWatch;

/// <summary>
/// Semantic retriever — embeds the query and every candidate chunk, returns
/// the top-K by cosine similarity. This is the classic RAG retrieval step.
///
/// Model: text-embedding-3-small is dirt cheap (≈$0.02 per 1M tokens). For a
/// typical investigation (~200 candidate chunks after keyword pre-filter)
/// embedding cost is fractions of a cent.
///
/// Why it wins over keyword retrieval: catches relevance that isn't lexical.
/// A query about "indexing failure" surfaces a chunk saying "OpenSearch wrote
/// timeout" even though the word "indexing" never appears. That's the payoff
/// of semantic search, and exactly the case where debugging gets hard.
///
/// Why it doesn't always win: it's slower, costs money, and can return
/// confidently-similar-but-actually-irrelevant chunks. For literal ID lookups
/// ("find act-789"), keyword matching is faster and safer. Real systems
/// combine the two — we do that in <see cref="HybridLogRetriever"/>.
/// </summary>
public sealed class SemanticLogRetriever : ILogRetriever
{
    private readonly EmbeddingClient _client;

    public SemanticLogRetriever(string openAiApiKey, string model = "text-embedding-3-small")
    {
        _client = new EmbeddingClient(model, openAiApiKey);
    }

    public async Task<IReadOnlyList<LogChunk>> RetrieveAsync(
        string query,
        IReadOnlyList<LogChunk> candidates,
        int topK,
        int maxTokens,
        CancellationToken ct)
    {
        if (candidates.Count == 0) return Array.Empty<LogChunk>();

        // One embeddings call for the query.
        var queryEmbedding = await EmbedAsync(query, ct);

        // One batched call for all the candidates — much cheaper than N calls.
        var texts = candidates.Select(c => c.Render()).ToList();
        var candidateEmbeddings = await EmbedBatchAsync(texts, ct);

        var scored = candidates
            .Select((chunk, i) => new
            {
                Chunk = chunk,
                Score = CosineSimilarity(queryEmbedding, candidateEmbeddings[i]),
            })
            .OrderByDescending(x => x.Score)
            .ToList();

        // Respect the token budget the same way KeywordLogRetriever does.
        var picked = new List<LogChunk>();
        var tokensUsed = 0;
        foreach (var s in scored)
        {
            if (picked.Count >= topK) break;
            if (tokensUsed + s.Chunk.EstimatedTokens > maxTokens) break;
            picked.Add(s.Chunk);
            tokensUsed += s.Chunk.EstimatedTokens;
        }

        return picked.OrderBy(c => c.Timestamp).ToList();
    }

    private async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        var result = await _client.GenerateEmbeddingAsync(text, cancellationToken: ct);
        return result.Value.ToFloats().ToArray();
    }

    private async Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken ct)
    {
        var result = await _client.GenerateEmbeddingsAsync(texts, cancellationToken: ct);
        return result.Value.Select(e => e.ToFloats().ToArray()).ToList();
    }

    /// <summary>
    /// Standard cosine similarity: dot product divided by the product of
    /// magnitudes. Identical vectors score 1.0; orthogonal 0.0; opposite -1.0.
    /// OpenAI's embeddings are already normalised so the denominator is
    /// effectively 1.0, but computing it explicitly keeps this function
    /// correct for any embedding model.
    /// </summary>
    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException("Embedding dimensions must match.");
        }

        double dot = 0, magA = 0, magB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        var denom = Math.Sqrt(magA) * Math.Sqrt(magB);
        return denom == 0 ? 0 : dot / denom;
    }
}

/// <summary>
/// Best of both retrievers: a cheap keyword pass that scores every chunk, plus
/// a semantic pass that rescues chunks the keyword pass missed. We merge the
/// top-K from each and dedupe.
///
/// The rationale is that log-debugging queries are a mix of literal anchors
/// ("act-789", "timeout") and conceptual intent ("why didn't it index") —
/// either retriever alone misses one side.
/// </summary>
public sealed class HybridLogRetriever : ILogRetriever
{
    private readonly ILogRetriever _keyword;
    private readonly ILogRetriever _semantic;

    public HybridLogRetriever(ILogRetriever keyword, ILogRetriever semantic)
    {
        _keyword = keyword;
        _semantic = semantic;
    }

    public async Task<IReadOnlyList<LogChunk>> RetrieveAsync(
        string query,
        IReadOnlyList<LogChunk> candidates,
        int topK,
        int maxTokens,
        CancellationToken ct)
    {
        // Ask each retriever for a half-share. We let each eat half the token
        // budget so we don't blow the context when we merge.
        var halfK = Math.Max(1, topK / 2);
        var halfTokens = Math.Max(200, maxTokens / 2);

        var kw = await _keyword.RetrieveAsync(query, candidates, halfK, halfTokens, ct);
        var sem = await _semantic.RetrieveAsync(query, candidates, halfK, halfTokens, ct);

        // Dedupe by (service, timestamp, text) — same chunk coming from both
        // retrievers shouldn't consume two slots.
        var merged = kw.Concat(sem)
            .GroupBy(c => (c.Service, c.Timestamp, c.Text))
            .Select(g => g.First())
            .OrderBy(c => c.Timestamp)
            .ToList();

        return merged;
    }
}
