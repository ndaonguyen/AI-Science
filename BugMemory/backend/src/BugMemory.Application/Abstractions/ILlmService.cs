namespace BugMemory.Application.Abstractions;

public sealed record ExtractedBugFields(
    string Title,
    IReadOnlyList<string> Tags,
    string Context,
    string RootCause,
    string Solution);

public sealed record RagAnswer(
    string Answer,
    IReadOnlyList<Guid> CitedEntryIds);

/// <summary>
/// Answer that may cite a mix of saved bug memories AND external sources.
/// CitedEntryIds is for saved bugs (same as <see cref="RagAnswer"/>);
/// CitedExternalIds is for external hits, matched by
/// <see cref="ExternalHit.ExternalId"/>. Both lists may be empty if the
/// LLM declined to cite anything.
/// </summary>
public sealed record MixedRagAnswer(
    string Answer,
    IReadOnlyList<Guid> CitedEntryIds,
    IReadOnlyList<string> CitedExternalIds);

/// <summary>
/// One context unit fed to the LLM during an Ask. Carries either an
/// internal saved-bug reference (Guid) OR external provenance, never
/// both. The LLM prompt formats these with their source label so it
/// knows which provenance shape to cite back.
/// </summary>
public sealed record MixedContext(
    string Content,
    float Score,
    // Exactly one of these two will be non-default per instance.
    // Choosing not to model this as a discriminated union to keep the
    // shape JSON-serializable for any future caching/logging.
    Guid? InternalEntryId,
    string? ExternalProvider,
    string? ExternalId,
    string? ExternalUrl,
    string? ExternalTitle);

public sealed record ContextReview(
    string Summary,
    IReadOnlyList<string> Suggestions,
    string RewrittenContext);

public sealed record ClarificationAnswer(
    string Answer,
    IReadOnlyList<string> Evidence);

public sealed record ConfirmedClarification(string Question, string Answer);

public interface ILlmService
{
    Task<ExtractedBugFields> ExtractBugFieldsAsync(string sourceText, CancellationToken ct);

    Task<RagAnswer> AnswerWithContextAsync(
        string question,
        IReadOnlyList<RetrievedContext> context,
        CancellationToken ct);

    /// <summary>
    /// Answer using context from MULTIPLE source types — saved bugs plus
    /// external hits (Jira tickets, GitHub commits). The prompt this
    /// method generates labels each context unit with its provenance so
    /// the LLM can cite back either a saved-bug Guid or an external
    /// provider+id.
    ///
    /// Distinct from <see cref="AnswerWithContextAsync"/> because the
    /// citation contract differs (returns <see cref="MixedRagAnswer"/>
    /// with two parallel id lists, not just one). Callers that only
    /// have saved-bug context should keep using the original method —
    /// no need to convert.
    /// </summary>
    Task<MixedRagAnswer> AnswerWithMixedContextAsync(
        string question,
        IReadOnlyList<MixedContext> context,
        CancellationToken ct);

    Task<ContextReview> ReviewContextAsync(
        string context,
        IReadOnlyList<string> tags,
        IReadOnlyList<string> affectedServices,
        string repoSnapshot,
        CancellationToken ct);

    Task<ClarificationAnswer> AnswerClarificationAsync(
        string question,
        string draftContext,
        IReadOnlyList<string> tags,
        IReadOnlyList<string> affectedServices,
        string repoSnapshot,
        CancellationToken ct);

    Task<string> RewriteContextWithAnswersAsync(
        string originalContext,
        IReadOnlyList<string> tags,
        IReadOnlyList<string> affectedServices,
        IReadOnlyList<ConfirmedClarification> clarifications,
        string repoSnapshot,
        CancellationToken ct);
}

public sealed record RetrievedContext(Guid EntryId, string Content, float Score);
