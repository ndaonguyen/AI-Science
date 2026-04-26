using System.Text;
using OpenAI.Embeddings;

namespace DistributedDebugger.Web.V3;

/// <summary>
/// Wraps the IInvestigationMemory operations into the shape the analyze
/// endpoint actually uses: embed once, search, render a prompt section,
/// then later write the new investigation back.
///
/// Failures here are LOGGED BUT NOT THROWN. If memory's vec0 binary isn't
/// installed yet, or the file is locked, or anything else goes wrong, we
/// degrade gracefully: the user still gets their analysis, just without
/// memory context. The whole feature is additive — making it optional was
/// the point of having a toggle.
///
/// Why this lives separately from <see cref="LogAnalyzer"/>: keeps the
/// analyzer's signature focused on 'analyse these logs given this context'.
/// Memory is upstream context-gathering; conceptually closer to RAG than to
/// generation.
/// </summary>
public sealed class MemoryPipeline
{
    private readonly IInvestigationMemory _store;
    private readonly EmbeddingClient _embeddings;
    private readonly int _topK;
    private readonly float _minSimilarity;

    public MemoryPipeline(
        IInvestigationMemory store,
        string openaiKey,
        int topK,
        float minSimilarity,
        string embeddingModel = "text-embedding-3-small")
    {
        _store = store;
        _embeddings = new EmbeddingClient(embeddingModel, openaiKey);
        _topK = topK;
        _minSimilarity = minSimilarity;
    }

    /// <summary>
    /// Embed the description and pull related past investigations. The
    /// returned outcome carries both the hits (for prompt rendering) and the
    /// embedding (so the caller can re-use it on the write-side without
    /// paying for a second embeddings call).
    /// </summary>
    public async Task<MemoryReadOutcome> ReadAsync(string description, CancellationToken ct)
    {
        ReadOnlyMemory<float> embedding;
        try
        {
            var resp = await _embeddings.GenerateEmbeddingAsync(description, cancellationToken: ct);
            embedding = resp.Value.ToFloats();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[v3/memory] embed failed, continuing without memory: {ex.GetType().Name}: {ex.Message}");
            return MemoryReadOutcome.Skipped(reason: $"embed failed: {ex.Message}");
        }

        IReadOnlyList<InvestigationMemoryHit> hits;
        int corpusSize;
        try
        {
            corpusSize = await _store.CountAsync(ct);
            hits = corpusSize == 0
                ? Array.Empty<InvestigationMemoryHit>()
                : await _store.SearchAsync(embedding, _topK, _minSimilarity, ct);
        }
        catch (Exception ex)
        {
            // vec0 not loaded, db locked, etc. Don't take down analyse.
            Console.Error.WriteLine($"[v3/memory] search failed, continuing without memory: {ex.GetType().Name}: {ex.Message}");
            return MemoryReadOutcome.Skipped(reason: $"search failed: {ex.Message}", embedding: embedding);
        }

        return new MemoryReadOutcome(
            Used: true,
            Hits: hits,
            CorpusSize: corpusSize,
            QueryEmbedding: embedding,
            SkipReason: null);
    }

    /// <summary>
    /// Persist the just-completed investigation. Reuses the embedding from
    /// the read step so we don't pay for embedding the description twice.
    /// Failures are logged and swallowed — we already returned the analysis
    /// to the user; failing to remember it isn't worth surfacing as an error.
    /// </summary>
    public async Task WriteAsync(InvestigationMemoryEntry entry, CancellationToken ct)
    {
        try
        {
            await _store.WriteAsync(entry, ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[v3/memory] write failed, analysis was returned anyway: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Render the retrieved hits as a markdown section to prepend to the
    /// prompt. Returns null when there's nothing useful — caller can skip
    /// the section instead of emitting an empty heading.
    /// </summary>
    public static string? RenderPromptSection(IReadOnlyList<InvestigationMemoryHit> hits)
    {
        if (hits.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine("## Related past investigations");
        sb.AppendLine();
        sb.AppendLine(
            "These are bugs you've previously investigated whose descriptions are " +
            "similar to the current one. Treat them as patterns to consider, NOT as " +
            "definitive answers — the same symptoms can have different causes. If a " +
            "past investigation matches the current evidence, say so. If not, say " +
            "what's different.");
        sb.AppendLine();

        foreach (var hit in hits)
        {
            var when = hit.Entry.CreatedAt.ToString("yyyy-MM-dd");
            var ticket = string.IsNullOrWhiteSpace(hit.Entry.TicketId)
                ? ""
                : $" ({hit.Entry.TicketId})";
            sb.AppendLine($"### {when}{ticket} — similarity {hit.Similarity:F2}");
            sb.AppendLine($"**Bug:** {hit.Entry.Description.Trim()}");
            sb.AppendLine($"**Hypothesis reached:** {hit.Entry.Hypothesis.Trim()}");
            if (!string.IsNullOrWhiteSpace(hit.Entry.EvidenceSummary))
                sb.AppendLine($"**Evidence checked:** {hit.Entry.EvidenceSummary.Trim()}");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}

/// <summary>
/// What the read-side returned: either a successful retrieval (with hits and
/// the embedding to reuse on write) or a skipped state with a reason. The
/// skipped state still carries the embedding when we got that far — even if
/// search failed, the embed call didn't, so writing later is still cheap.
/// </summary>
public sealed record MemoryReadOutcome(
    bool Used,
    IReadOnlyList<InvestigationMemoryHit> Hits,
    int CorpusSize,
    ReadOnlyMemory<float> QueryEmbedding,
    string? SkipReason)
{
    public static MemoryReadOutcome Skipped(string reason, ReadOnlyMemory<float> embedding = default) =>
        new(
            Used: false,
            Hits: Array.Empty<InvestigationMemoryHit>(),
            CorpusSize: 0,
            QueryEmbedding: embedding,
            SkipReason: reason);
}
