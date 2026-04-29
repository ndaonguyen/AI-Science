namespace BugMemory.Application.Dtos;

public sealed record BugMemoryDto(
    Guid Id,
    string Title,
    IReadOnlyList<string> Tags,
    string Context,
    string RootCause,
    string Solution,
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
