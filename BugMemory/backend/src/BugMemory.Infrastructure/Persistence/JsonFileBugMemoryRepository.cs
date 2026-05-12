using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using BugMemory.Application.Abstractions;
using BugMemory.Domain.Entities;
using Microsoft.Extensions.Options;

namespace BugMemory.Infrastructure.Persistence;

public sealed class JsonFileBugMemoryRepositoryOptions
{
    public string FilePath { get; set; } = "bug-memories.json";
}

internal sealed record BugMemoryRecord(
    Guid Id,
    string Title,
    string Context,
    string RootCause,
    string Solution,
    List<string> Tags,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    // Optional fields added later — older JSON files won't have them.
    // Default Kind = Bug keeps existing data behaving as before.
    public MemoryKind Kind { get; init; } = MemoryKind.Bug;
    public List<string>? AffectedServices { get; init; }
    public List<string>? Links { get; init; }
    public ReviewHistoryRecord? ReviewHistory { get; init; }
}

internal sealed record ReviewHistoryRecord(
    string Summary,
    List<ReviewClarificationRecord> Clarifications,
    List<string> ScannedRepos,
    List<string> UnconfiguredServices,
    string RewrittenContext,
    DateTimeOffset ReviewedAt);

internal sealed record ReviewClarificationRecord(
    string Question,
    string Answer,
    string AiAnswer,
    List<string> Evidence);

public sealed class JsonFileBugMemoryRepository : IBugMemoryRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _filePath;
    private readonly ConcurrentDictionary<Guid, BugMemoryEntry> _store = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _loaded;

    public JsonFileBugMemoryRepository(IOptions<JsonFileBugMemoryRepositoryOptions> options)
    {
        _filePath = options.Value.FilePath;
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_loaded) return;
        await _writeLock.WaitAsync(ct);
        try
        {
            if (_loaded) return;
            if (File.Exists(_filePath))
            {
                await using var stream = File.OpenRead(_filePath);
                var records = await JsonSerializer.DeserializeAsync<List<BugMemoryRecord>>(stream, JsonOptions, ct)
                              ?? new List<BugMemoryRecord>();
                foreach (var r in records)
                {
                    var entry = BugMemoryEntry.Hydrate(
                        r.Id,
                        r.Kind,
                        r.Title,
                        r.Context,
                        r.RootCause,
                        r.Solution,
                        r.Tags,
                        r.AffectedServices,
                        r.Links,
                        r.CreatedAt,
                        r.UpdatedAt);
                    if (r.ReviewHistory is not null)
                    {
                        entry.SetReviewHistory(ToDomain(r.ReviewHistory));
                    }
                    _store[r.Id] = entry;
                }
            }
            _loaded = true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        var records = _store.Values
            .Select(e => new BugMemoryRecord(
                e.Id, e.Title, e.Context, e.RootCause, e.Solution, e.Tags.ToList(), e.CreatedAt, e.UpdatedAt)
            {
                Kind = e.Kind,
                AffectedServices = e.AffectedServices.ToList(),
                Links = e.Links.ToList(),
                ReviewHistory = e.ReviewHistory is null ? null : ToRecord(e.ReviewHistory),
            })
            .ToList();
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await using var stream = File.Create(_filePath);
        await JsonSerializer.SerializeAsync(stream, records, JsonOptions, ct);
    }

    public async Task AddAsync(BugMemoryEntry entry, CancellationToken ct)
    {
        await EnsureLoadedAsync(ct);
        await _writeLock.WaitAsync(ct);
        try
        {
            _store[entry.Id] = entry;
            await PersistAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task UpdateAsync(BugMemoryEntry entry, CancellationToken ct)
    {
        await EnsureLoadedAsync(ct);
        await _writeLock.WaitAsync(ct);
        try
        {
            _store[entry.Id] = entry;
            await PersistAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<BugMemoryEntry?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        await EnsureLoadedAsync(ct);
        return _store.TryGetValue(id, out var entry) ? entry : null;
    }

    public async Task<IReadOnlyList<BugMemoryEntry>> GetAllAsync(CancellationToken ct)
    {
        await EnsureLoadedAsync(ct);
        return _store.Values.OrderByDescending(e => e.UpdatedAt).ToList();
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        await EnsureLoadedAsync(ct);
        await _writeLock.WaitAsync(ct);
        try
        {
            _store.TryRemove(id, out _);
            await PersistAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static ReviewHistory ToDomain(ReviewHistoryRecord r) => new()
    {
        Summary = r.Summary,
        Clarifications = r.Clarifications
            .Select(c => new ReviewClarification
            {
                Question = c.Question,
                Answer = c.Answer,
                AiAnswer = c.AiAnswer,
                Evidence = c.Evidence,
            })
            .ToList(),
        ScannedRepos = r.ScannedRepos,
        UnconfiguredServices = r.UnconfiguredServices,
        RewrittenContext = r.RewrittenContext,
        ReviewedAt = r.ReviewedAt,
    };

    private static ReviewHistoryRecord ToRecord(ReviewHistory h) => new(
        h.Summary,
        h.Clarifications.Select(c => new ReviewClarificationRecord(
            c.Question, c.Answer, c.AiAnswer, c.Evidence.ToList())).ToList(),
        h.ScannedRepos.ToList(),
        h.UnconfiguredServices.ToList(),
        h.RewrittenContext,
        h.ReviewedAt);
}
