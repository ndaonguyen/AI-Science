namespace DistributedDebugger.Tools.CloudWatch;

/// <summary>
/// Takes a pool of candidate log chunks and a query, returns the top-K most
/// relevant. This is the R in RAG — the part that decides what actually goes
/// into the model's context window.
///
/// Why an interface: we start with a zero-cost keyword-scoring retriever (below)
/// and will layer a semantic embedding retriever on top later. Both implement
/// the same contract so the agent code doesn't care which one is wired in.
/// </summary>
public interface ILogRetriever
{
    Task<IReadOnlyList<LogChunk>> RetrieveAsync(
        string query,
        IReadOnlyList<LogChunk> candidates,
        int topK,
        int maxTokens,
        CancellationToken ct);
}

/// <summary>
/// Pure keyword-based retriever — no API calls, no embeddings. Scores chunks
/// by how many query terms appear and how early they appear. Good baseline
/// and a useful sanity check: if this can't find the right evidence, check
/// your query before spending on embeddings.
///
/// Returns chunks in chronological order once the top-K is selected — chronology
/// matters for debugging (you want "event A happened then event B failed" in
/// the right order), and it's a nice contrast with semantic retrievers that
/// would return in score order.
/// </summary>
public sealed class KeywordLogRetriever : ILogRetriever
{
    public Task<IReadOnlyList<LogChunk>> RetrieveAsync(
        string query,
        IReadOnlyList<LogChunk> candidates,
        int topK,
        int maxTokens,
        CancellationToken ct)
    {
        // Pull out the meaningful terms from the query. Drop common stop-ish
        // words so a query like "why did act-789 not get indexed" scores on
        // the tokens that matter ("act-789", "indexed").
        var terms = query
            .Split(new[] { ' ', '\t', ',', '.', '?', '!', ';', ':' },
                   StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.ToLowerInvariant())
            .Where(t => t.Length > 2 && !StopWords.Contains(t))
            .Distinct()
            .ToList();

        if (terms.Count == 0)
        {
            // No searchable terms — fall back to newest chunks first. Better
            // than returning nothing, since the bug is usually recent.
            return Task.FromResult<IReadOnlyList<LogChunk>>(
                candidates.OrderByDescending(c => c.Timestamp).Take(topK).ToList());
        }

        var scored = candidates
            .Select(c => new { Chunk = c, Score = ScoreChunk(c, terms) })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ToList();

        // Take up to topK, but also stop if we're blowing the token budget.
        var picked = new List<LogChunk>();
        var tokensUsed = 0;
        foreach (var s in scored)
        {
            if (picked.Count >= topK) break;
            if (tokensUsed + s.Chunk.EstimatedTokens > maxTokens) break;
            picked.Add(s.Chunk);
            tokensUsed += s.Chunk.EstimatedTokens;
        }

        // Sort final set chronologically so the model sees events in order.
        var ordered = picked.OrderBy(c => c.Timestamp).ToList();
        return Task.FromResult<IReadOnlyList<LogChunk>>(ordered);
    }

    /// <summary>
    /// +2 for each query term found in the chunk text (case-insensitive).
    /// +1 extra if the chunk is an ERROR line — errors are almost always the
    /// signal we're looking for. Simple and transparent; easy to tune later.
    /// </summary>
    private static int ScoreChunk(LogChunk chunk, IReadOnlyList<string> terms)
    {
        var text = chunk.Text.ToLowerInvariant();
        var score = 0;
        foreach (var term in terms)
        {
            if (text.Contains(term)) score += 2;
        }
        if (text.Contains("error") || text.Contains("exception") || text.Contains("fail"))
        {
            score += 1;
        }
        return score;
    }

    /// <summary>
    /// Short list — log queries are already pretty terse. Better to miss a
    /// stop word than to filter out something meaningful.
    /// </summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "from", "was", "not", "but", "why", "did", "get",
        "has", "have", "are", "were", "been", "that", "this", "what", "when", "how",
    };
}
