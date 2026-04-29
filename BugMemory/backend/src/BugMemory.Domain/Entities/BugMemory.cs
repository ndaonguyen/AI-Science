namespace BugMemory.Domain.Entities;

public sealed class BugMemoryEntry
{
    public Guid Id { get; private set; }
    public string Title { get; private set; }
    public string Context { get; private set; }
    public string RootCause { get; private set; }
    public string Solution { get; private set; }
    public IReadOnlyList<string> Tags { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private BugMemoryEntry(
        Guid id,
        string title,
        string context,
        string rootCause,
        string solution,
        IReadOnlyList<string> tags,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        Id = id;
        Title = title;
        Context = context;
        RootCause = rootCause;
        Solution = solution;
        Tags = tags;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public static BugMemoryEntry Create(
        string title,
        string context,
        string rootCause,
        string solution,
        IEnumerable<string> tags,
        DateTimeOffset now)
    {
        Guard(title, context, rootCause, solution);
        var normalizedTags = NormalizeTags(tags);
        return new BugMemoryEntry(
            Guid.NewGuid(),
            title.Trim(),
            context.Trim(),
            rootCause.Trim(),
            solution.Trim(),
            normalizedTags,
            now,
            now);
    }

    public static BugMemoryEntry Hydrate(
        Guid id,
        string title,
        string context,
        string rootCause,
        string solution,
        IEnumerable<string> tags,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        return new BugMemoryEntry(
            id,
            title,
            context,
            rootCause,
            solution,
            NormalizeTags(tags),
            createdAt,
            updatedAt);
    }

    public void Update(
        string title,
        string context,
        string rootCause,
        string solution,
        IEnumerable<string> tags,
        DateTimeOffset now)
    {
        Guard(title, context, rootCause, solution);
        Title = title.Trim();
        Context = context.Trim();
        RootCause = rootCause.Trim();
        Solution = solution.Trim();
        Tags = NormalizeTags(tags);
        UpdatedAt = now;
    }

    public string ToEmbeddingText()
    {
        var tagsLine = Tags.Count > 0 ? string.Join(", ", Tags) : "none";
        return $"""
        Title: {Title}
        Tags: {tagsLine}
        Context: {Context}
        Root cause: {RootCause}
        Solution: {Solution}
        """;
    }

    private static void Guard(string title, string context, string rootCause, string solution)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Title is required", nameof(title));
        if (string.IsNullOrWhiteSpace(context) && string.IsNullOrWhiteSpace(rootCause) && string.IsNullOrWhiteSpace(solution))
            throw new ArgumentException("At least one of context, root cause, or solution must be provided");
    }

    private static IReadOnlyList<string> NormalizeTags(IEnumerable<string> tags)
    {
        return tags
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim().ToLowerInvariant())
            .Distinct()
            .ToList();
    }
}
