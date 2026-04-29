using BugMemory.Application.Abstractions;

namespace BugMemory.Application.UseCases;

public sealed class DeleteBugMemoryUseCase
{
    private readonly IBugMemoryRepository _repository;
    private readonly IVectorStore _vectorStore;

    public DeleteBugMemoryUseCase(IBugMemoryRepository repository, IVectorStore vectorStore)
    {
        _repository = repository;
        _vectorStore = vectorStore;
    }

    public async Task<bool> ExecuteAsync(Guid id, CancellationToken ct)
    {
        var entry = await _repository.GetByIdAsync(id, ct);
        if (entry is null) return false;

        await _repository.DeleteAsync(id, ct);
        await _vectorStore.DeleteAsync(id, ct);
        return true;
    }
}
