using BugMemory.Application.Dtos;
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
    List<string>? Links,
    ReviewHistoryDto? ReviewHistory);

public sealed record UpdateBugMemoryRequest(
    MemoryKind? Kind,
    string Title,
    List<string> Tags,
    string Context,
    string RootCause,
    string Solution,
    List<string>? AffectedServices,
    List<string>? Links,
    ReviewHistoryDto? ReviewHistory);

public sealed record SearchRequest(string Query, int? TopK);

public sealed record AskRequest(string Question, int? TopK);

/// <summary>
/// Source-aware Ask request. Sources is a list of stable source names:
/// "bugs" (the saved vector store), "jira", "github". Order doesn't
/// matter; duplicates are collapsed. Null/empty Sources defaults to
/// saved bugs only, matching the legacy /api/ask endpoint behavior.
/// </summary>
public sealed record AskWithSourcesRequest(
    string Question,
    int? TopK,
    List<string>? Sources);

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

public sealed record ClarificationPair(string Question, string Answer);

public sealed record RewriteContextRequest(
    string OriginalContext,
    List<string>? Tags,
    List<string>? AffectedServices,
    List<ClarificationPair>? Clarifications);
