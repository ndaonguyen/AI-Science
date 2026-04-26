namespace DistributedDebugger.Web.V3;

/// <summary>
/// Persistent memory of past V3 investigations. After every successful Analyze
/// the bug description, hypothesis, and a short evidence summary are stored
/// indexed by an embedding of the description. Before every Analyze we ask
/// the memory for past investigations whose descriptions are semantically
/// similar to the new one — those get prepended to the prompt as
/// "## Related past investigations".
///
/// The interface is deliberately narrow:
///
///   - WriteAsync — append-only; we don't dedupe or merge similar entries.
///     A near-duplicate description is information ('this came up again'),
///     not noise to suppress. If the corpus ever gets messy we'll clean it
///     by deleting and re-running, not by complicating the API.
///
///   - SearchAsync — top-K by cosine similarity, with a similarity floor so
///     we don't return weakly-related entries that would just dilute the
///     prompt. Returned entries already include enough context for the LLM
///     to reason about ('here's how we previously concluded a similar bug').
///
///   - CountAsync — for the UI to show 'memory: 2 related (of 47)' without
///     loading every entry.
///
/// Why an interface: today's implementation is sqlite-vec; tomorrow's might
/// be Qdrant or a hosted vector store. Keeping the boundary narrow means the
/// LogAnalyzer and the API don't need to know which one is wired in.
/// </summary>
public interface IInvestigationMemory
{
    /// <summary>
    /// Persist one completed analysis. Idempotent within a session in the sense
    /// that calling it twice with the same content produces two rows — that's
    /// fine, we'd rather over-record than miss something. Pass the embedding
    /// the caller already computed for retrieval; we don't re-embed here.
    /// </summary>
    Task WriteAsync(InvestigationMemoryEntry entry, CancellationToken ct);

    /// <summary>
    /// Top-K most similar past entries to the given query embedding, filtered
    /// by a minimum cosine similarity. Returns chronologically-newest-first
    /// among ties because more recent context is usually more useful when bug
    /// patterns repeat.
    /// </summary>
    Task<IReadOnlyList<InvestigationMemoryHit>> SearchAsync(
        ReadOnlyMemory<float> queryEmbedding,
        int topK,
        float minSimilarity,
        CancellationToken ct);

    /// <summary>
    /// Total entries — for UI 'memory: 2 related (of 47)' transparency.
    /// </summary>
    Task<int> CountAsync(CancellationToken ct);
}

/// <summary>
/// One stored investigation. <c>Embedding</c> is the embedded form of the
/// bug description — the field we search on. The other fields are payload
/// rendered into the prompt when this entry is retrieved.
/// </summary>
public sealed record InvestigationMemoryEntry(
    string Id,
    DateTimeOffset CreatedAt,
    string Description,
    string Hypothesis,
    /// <summary>
    /// Short bullet-list of evidence kinds and titles, joined into one string.
    /// E.g. "Mongo: ComponentBlockModel — _id 67abcd; Note: observed in prod 09:09 UTC".
    /// Lets the retrieved memory show 'here's what was checked' without
    /// re-storing every byte of every Mongo doc.
    /// </summary>
    string EvidenceSummary,
    string? TicketId,
    /// <summary>JSON-encoded list of schema names included in that analysis.</summary>
    IReadOnlyList<string> SchemasIncluded,
    /// <summary>1536-dim embedding of <c>Description</c>.</summary>
    ReadOnlyMemory<float> Embedding);

/// <summary>
/// One result of a similarity search. Same shape as the stored entry plus
/// the cosine similarity score the search produced — useful for both
/// debugging ('why did this match?') and for showing the user why this
/// memory was surfaced.
/// </summary>
public sealed record InvestigationMemoryHit(
    InvestigationMemoryEntry Entry,
    float Similarity);
