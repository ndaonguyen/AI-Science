using BugMemory.Domain.Entities;

namespace BugMemory.Application.Dtos;

public sealed record BugMemoryDto(
    Guid Id,
    MemoryKind Kind,
    string Title,
    IReadOnlyList<string> Tags,
    string Context,
    string RootCause,
    string Solution,
    IReadOnlyList<string> AffectedServices,
    IReadOnlyList<string> Links,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    ReviewHistoryDto? ReviewHistory);

public sealed record ReviewHistoryDto(
    string Summary,
    IReadOnlyList<ReviewClarificationDto> Clarifications,
    IReadOnlyList<string> ScannedRepos,
    IReadOnlyList<string> UnconfiguredServices,
    string RewrittenContext,
    DateTimeOffset ReviewedAt);

public sealed record ReviewClarificationDto(
    string Question,
    string Answer,
    string AiAnswer,
    IReadOnlyList<string> Evidence);

public sealed record SearchResultDto(
    BugMemoryDto Entry,
    float Score);

public sealed record RagResponseDto(
    string Answer,
    IReadOnlyList<SearchResultDto> Citations);

/// <summary>
/// One external hit returned to the frontend — Jira ticket, GitHub
/// commit, etc. Mirrors <see cref="Application.Abstractions.ExternalHit"/>
/// minus the LLM-internal Snippet field (which is fed to the model but
/// not shown verbatim in the UI; the UI shows Title + the source link).
/// </summary>
public sealed record ExternalCitationDto(
    string Provider,
    string ExternalId,
    string Url,
    string Title,
    DateTimeOffset? When,
    double Score,
    // We DO surface the snippet to the UI as a 'Save as bug memory'
    // pre-fill source. The UI doesn't render it inline (would be noisy)
    // but the save flow round-trips it back to /api/extract.
    string Snippet);

/// <summary>
/// Mixed response: an answer plus citations from any combination of
/// saved bugs and external sources. The two citation lists are
/// independent — either may be empty depending on what the LLM cited.
/// </summary>
public sealed record MixedRagResponseDto(
    string Answer,
    IReadOnlyList<SearchResultDto> BugCitations,
    IReadOnlyList<ExternalCitationDto> ExternalCitations,
    // Diagnostic info: which sources were queried, which failed, etc.
    // The UI doesn't have to render this but it makes debugging
    // ("why isn't Jira showing up?") tractable from the response alone.
    IReadOnlyList<string> SourcesQueried,
    IReadOnlyList<SourceErrorDto> SourceErrors);

public sealed record SourceErrorDto(string Source, string Error);

public sealed record ExtractionResultDto(
    string Title,
    IReadOnlyList<string> Tags,
    string Context,
    string RootCause,
    string Solution);

public sealed record ContextReviewDto(
    string Summary,
    IReadOnlyList<string> Suggestions,
    string RewrittenContext,
    IReadOnlyList<string> ScannedRepos,
    IReadOnlyList<string> UnconfiguredServices);

public sealed record ClarificationAnswerDto(
    string Answer,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> ScannedRepos,
    IReadOnlyList<string> UnconfiguredServices);

public sealed record RewrittenContextDto(
    string Context,
    IReadOnlyList<string> ScannedRepos,
    IReadOnlyList<string> UnconfiguredServices);
