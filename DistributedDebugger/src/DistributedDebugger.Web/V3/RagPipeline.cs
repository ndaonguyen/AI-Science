using DistributedDebugger.Tools.CloudWatch;

namespace DistributedDebugger.Web.V3;

/// <summary>
/// Wraps the keyword + semantic + hybrid retrieval pipeline as a one-call
/// "narrow this gathered log set down" step. V3's Analyze endpoint calls
/// this BEFORE handing logs to LogAnalyzer — when the set is small the
/// pipeline is a no-op and the LLM sees everything; once the set crosses a
/// threshold (configurable, default 100) we hybrid-retrieve top-K so the
/// model only reads the most relevant lines.
///
/// Why this lives in V3 and not V2: V2 is "the human is the retriever" by
/// design, so adding an automatic narrow there would defeat the point.
/// V3 is the experimental "what if we layer RAG on top of curation" path,
/// so retrieval kicks in only when the curated set is too big to read in
/// full.
///
/// The query is constructed from bug description + evidence titles. Including
/// evidence titles is deliberate: when the user pastes "ComponentBlockModel —
/// _id 67abcd" as evidence, that title is exactly the kind of thing that should
/// pull in matching log lines.
/// </summary>
public sealed class RagPipeline
{
    private readonly string _openaiKey;
    private readonly int _threshold;
    private readonly int _topK;

    public RagPipeline(string openaiKey, int threshold, int topK)
    {
        _openaiKey = openaiKey;
        _threshold = threshold;
        _topK = topK;
    }

    /// <summary>
    /// Decide whether to retrieve, run hybrid retrieval if so, return both the
    /// final logs and bookkeeping the API surfaces back to the UI ("retrieving
    /// top 25 of 240"). Below threshold this is a pass-through and Used is
    /// false — caller can render "RAG: skipped (under threshold)" so the
    /// behaviour is transparent.
    /// </summary>
    public async Task<RagOutcome> ApplyAsync(
        string query,
        IReadOnlyList<LogRecord> gathered,
        CancellationToken ct)
    {
        if (gathered.Count <= _threshold)
        {
            return new RagOutcome(
                Logs: gathered,
                Used: false,
                Threshold: _threshold,
                FromCount: gathered.Count,
                KeptCount: gathered.Count);
        }

        // Adapt LogRecord → LogChunk; the retrievers operate on chunks because
        // V1 chunks consecutive log lines. For V3 we treat one log per chunk
        // — the user already pre-curated, so further chunking would obscure
        // the boundaries the user picked.
        var chunks = gathered
            .Select(l => new LogChunk(
                Service: l.Service,
                LogGroup: l.LogGroup,
                Timestamp: l.Timestamp,
                Text: l.Message))
            .ToList();

        // Hybrid: keyword catches literal id/keyword matches, semantic catches
        // paraphrased relevance. Each gets half the budget; output is deduped
        // and re-ordered chronologically by HybridLogRetriever.
        var keyword = new KeywordLogRetriever();
        var semantic = new SemanticLogRetriever(_openaiKey);
        var hybrid = new HybridLogRetriever(keyword, semantic);

        var top = await hybrid.RetrieveAsync(
            query: query,
            candidates: chunks,
            topK: _topK,
            // Token budget: bound so the prompt stays under typical context
            // limits even with schemas + evidence. 8000 tokens of logs leaves
            // room for ~4K of schemas, ~2K of evidence, and the system prompt.
            maxTokens: 8000,
            ct);

        // Map the kept chunks back to LogRecords by (service, timestamp, text)
        // — that triple is unique enough for our gathered set.
        var keptKeys = top
            .Select(c => (c.Service, c.Timestamp, c.Text))
            .ToHashSet();
        var keptLogs = gathered
            .Where(l => keptKeys.Contains((l.Service, l.Timestamp, l.Message)))
            .ToList();

        return new RagOutcome(
            Logs: keptLogs,
            Used: true,
            Threshold: _threshold,
            FromCount: gathered.Count,
            KeptCount: keptLogs.Count);
    }
}

/// <summary>
/// The outcome of a RAG pass. <c>Logs</c> is what the analyzer should send to
/// the LLM; <c>Used</c> says whether retrieval actually fired (false when
/// under threshold); the count fields let the UI render transparency about
/// what RAG did.
/// </summary>
public sealed record RagOutcome(
    IReadOnlyList<LogRecord> Logs,
    bool Used,
    int Threshold,
    int FromCount,
    int KeptCount);
