using BugMemory.Domain.Entities;

namespace BugMemory.Api.Contracts;

public sealed record CreateBugMemoryRequest(
    MemoryKind? Kind,
    string Title,
    List<string> Tags,
    string Context,
    string RootCause,
    string Solution,
    List<string>? AffectedServices,
    List<string>? Links);

public sealed record UpdateBugMemoryRequest(
    MemoryKind? Kind,
    string Title,
    List<string> Tags,
    string Context,
    string RootCause,
    string Solution,
    List<string>? AffectedServices,
    List<string>? Links);

public sealed record SearchRequest(string Query, int? TopK);

public sealed record AskRequest(string Question, int? TopK);

public sealed record ExtractRequest(string SourceText);

public sealed record ReviewRequest(
    string Context,
    List<string>? Tags,
    List<string>? AffectedServices);

public sealed record ClarificationRequest(
    string Question,
    string? DraftContext,
    List<string>? Tags,
    List<string>? AffectedServices);
