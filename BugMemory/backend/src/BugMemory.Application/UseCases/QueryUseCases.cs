using BugMemory.Application.Abstractions;
using BugMemory.Application.Dtos;
using BugMemory.Application.Mapping;
using BugMemory.Domain.Entities;

namespace BugMemory.Application.UseCases;

public sealed class ListBugMemoriesUseCase
{
    private readonly IBugMemoryRepository _repository;

    public ListBugMemoriesUseCase(IBugMemoryRepository repository) => _repository = repository;

    public async Task<IReadOnlyList<BugMemoryDto>> ExecuteAsync(MemoryKind? kind, CancellationToken ct)
    {
        var entries = await _repository.GetAllAsync(ct);
        var filtered = kind is null ? entries : entries.Where(e => e.Kind == kind.Value);
        return filtered.Select(e => e.ToDto()).ToList();
    }
}

public sealed class GetBugMemoryUseCase
{
    private readonly IBugMemoryRepository _repository;

    public GetBugMemoryUseCase(IBugMemoryRepository repository) => _repository = repository;

    public async Task<BugMemoryDto?> ExecuteAsync(Guid id, CancellationToken ct)
    {
        var entry = await _repository.GetByIdAsync(id, ct);
        return entry?.ToDto();
    }
}
