namespace BugMemory.Domain.Entities;

public sealed class BugMemoryEntry
{
    public Guid Id { get; private set; }
    public MemoryKind Kind { get; private set; }
    public string Title { get; private set; }
    public string Context { get; private set; }
    public string RootCause { get; private set; }
    public string Solution { get; private set; }
    public IReadOnlyList<string> Tags { get; private set; }
    public IReadOnlyList<string> AffectedServices { get; private set; }
    public IReadOnlyList<string> Links { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private BugMemoryEntry(
        Guid id,
        MemoryKind kind,
        string title,
        string context,
        string rootCause,
        string solution,
        IReadOnlyList<string> tags,
        IReadOnlyList<string> affectedServices,
        IReadOnlyList<string> links,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        Id = id;
        Kind = kind;
        Title = title;
        Context = context;
        RootCause = rootCause;
        Solution = solution;
        Tags = tags;
        AffectedServices = affectedServices;
        Links = links;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public static BugMemoryEntry Create(
        MemoryKind kind,
        string title,
        string context,
        string rootCause,
        string solution,
        IEnumerable<string> tags,
        IEnumerable<string>? affectedServices,
        IEnumerable<string>? links,
        DateTimeOffset now)
    {
        Guard(title, context, rootCause, solution);
        return new BugMemoryEntry(
            Guid.NewGuid(),
            kind,
            title.Trim(),
            context.Trim(),
            rootCause.Trim(),
            solution.Trim(),
            NormalizeTags(tags),
            NormalizeServices(affectedServices),
            NormalizeLinks(links),
            now,
            now);
    }

    public static BugMemoryEntry Hydrate(
        Guid id,
        MemoryKind kind,
        string title,
        string context,
        string rootCause,
        string solution,
        IEnumerable<string> tags,
        IEnumerable<string>? affectedServices,
        IEnumerable<string>? links,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        return new BugMemoryEntry(
            id,
            kind,
            title,
            context,
            rootCause,
            solution,
            NormalizeTags(tags),
            NormalizeServices(affectedServices),
            NormalizeLinks(links),
            createdAt,
            updatedAt);
    }

    public void Update(
        MemoryKind kind,
        string title,
        string context,
        string rootCause,
        string solution,
        IEnumerable<string> tags,
        IEnumerable<string>? affectedServices,
        IEnumerable<string>? links,
        DateTimeOffset now)
    {
        Guard(title, context, rootCause, solution);
        Kind = kind;
        Title = title.Trim();
        Context = context.Trim();
        RootCause = rootCause.Trim();
        Solution = solution.Trim();
        Tags = NormalizeTags(tags);
        AffectedServices = NormalizeServices(affectedServices);
        Links = NormalizeLinks(links);
        UpdatedAt = now;
    }

    public string ToEmbeddingText()
    {
        var tagsLine = Tags.Count > 0 ? string.Join(", ", Tags) : "none";

        if (Kind == MemoryKind.Feature)
        {
            var lines = new List<string>
            {
                "Kind: Feature",
                $"Title: {Title}",
                $"Tags: {tagsLine}",
            };
            if (AffectedServices.Count > 0)
                lines.Add($"Affected services: {string.Join(", ", AffectedServices)}");
            lines.Add($"Context: {Context}");
            lines.Add($"Why: {RootCause}");
            lines.Add($"Decision: {Solution}");
            if (Links.Count > 0)
                lines.Add($"Links: {string.Join(", ", Links)}");
            return string.Join("\n", lines);
        }

        // Bug — keep the original format so existing vectors remain valid.
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

    private static IReadOnlyList<string> NormalizeServices(IEnumerable<string>? services)
    {
        if (services is null) return Array.Empty<string>();
        return services
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToLowerInvariant())
            .Distinct()
            .ToList();
    }

    private static IReadOnlyList<string> NormalizeLinks(IEnumerable<string>? links)
    {
        if (links is null) return Array.Empty<string>();
        return links
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
