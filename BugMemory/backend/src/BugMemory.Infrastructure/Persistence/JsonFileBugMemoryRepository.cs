using System.Collections.Concurrent;
using System.Text.Json;
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
    DateTimeOffset UpdatedAt);

public sealed class JsonFileBugMemoryRepository : IBugMemoryRepository
{
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
                var records = await JsonSerializer.DeserializeAsync<List<BugMemoryRecord>>(stream, cancellationToken: ct)
                              ?? new List<BugMemoryRecord>();
                foreach (var r in records)
                {
                    var entry = BugMemoryEntry.Hydrate(r.Id, r.Title, r.Context, r.RootCause, r.Solution, r.Tags, r.CreatedAt, r.UpdatedAt);
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
            .Select(e => new BugMemoryRecord(e.Id, e.Title, e.Context, e.RootCause, e.Solution, e.Tags.ToList(), e.CreatedAt, e.UpdatedAt))
            .ToList();
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await using var stream = File.Create(_filePath);
        await JsonSerializer.SerializeAsync(stream, records, new JsonSerializerOptions { WriteIndented = true }, ct);
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
}
