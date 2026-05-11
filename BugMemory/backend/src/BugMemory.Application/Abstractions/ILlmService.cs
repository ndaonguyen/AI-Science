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
