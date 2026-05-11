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
    DateTimeOffset UpdatedAt);

public sealed record SearchResultDto(
    BugMemoryDto Entry,
    float Score);

public sealed record RagResponseDto(
    string Answer,
    IReadOnlyList<SearchResultDto> Citations);

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
