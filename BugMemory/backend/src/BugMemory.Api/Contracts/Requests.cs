namespace BugMemory.Api.Contracts;

public sealed record CreateBugMemoryRequest(
    string Title,
    List<string> Tags,
    string Context,
    string RootCause,
    string Solution);

public sealed record UpdateBugMemoryRequest(
    string Title,
    List<string> Tags,
    string Context,
    string RootCause,
    string Solution);

public sealed record SearchRequest(string Query, int? TopK);

public sealed record AskRequest(string Question, int? TopK);

public sealed record ExtractRequest(string SourceText);
