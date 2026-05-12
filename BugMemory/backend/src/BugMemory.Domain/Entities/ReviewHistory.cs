namespace BugMemory.Domain.Entities;

public sealed class ReviewHistory
{
    public string Summary { get; init; } = string.Empty;
    public IReadOnlyList<ReviewClarification> Clarifications { get; init; } = Array.Empty<ReviewClarification>();
    public IReadOnlyList<string> ScannedRepos { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> UnconfiguredServices { get; init; } = Array.Empty<string>();
    public string RewrittenContext { get; init; } = string.Empty;
    public DateTimeOffset ReviewedAt { get; init; }
}

public sealed class ReviewClarification
{
    public string Question { get; init; } = string.Empty;
    public string Answer { get; init; } = string.Empty;
    public string AiAnswer { get; init; } = string.Empty;
    public IReadOnlyList<string> Evidence { get; init; } = Array.Empty<string>();
}
