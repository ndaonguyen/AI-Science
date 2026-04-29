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

public interface ILlmService
{
    Task<ExtractedBugFields> ExtractBugFieldsAsync(string sourceText, CancellationToken ct);

    Task<RagAnswer> AnswerWithContextAsync(
        string question,
        IReadOnlyList<RetrievedContext> context,
        CancellationToken ct);
}

public sealed record RetrievedContext(Guid EntryId, string Content, float Score);
