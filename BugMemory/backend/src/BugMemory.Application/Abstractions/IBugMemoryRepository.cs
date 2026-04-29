using BugMemory.Domain.Entities;

namespace BugMemory.Application.Abstractions;

public interface IBugMemoryRepository
{
    Task AddAsync(BugMemoryEntry entry, CancellationToken ct);
    Task UpdateAsync(BugMemoryEntry entry, CancellationToken ct);
    Task<BugMemoryEntry?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<BugMemoryEntry>> GetAllAsync(CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
}
