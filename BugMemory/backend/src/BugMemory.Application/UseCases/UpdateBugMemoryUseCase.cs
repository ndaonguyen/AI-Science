using BugMemory.Application.Abstractions;
using BugMemory.Application.Dtos;
using BugMemory.Application.Mapping;

namespace BugMemory.Application.UseCases;

public sealed record UpdateBugMemoryCommand(
    Guid Id,
    string Title,
    IReadOnlyList<string> Tags,
    string Context,
    string RootCause,
    string Solution);

public sealed class UpdateBugMemoryUseCase
{
    private readonly IBugMemoryRepository _repository;
    private readonly IEmbeddingService _embeddings;
    private readonly IVectorStore _vectorStore;
    private readonly IClock _clock;

    public UpdateBugMemoryUseCase(
        IBugMemoryRepository repository,
        IEmbeddingService embeddings,
        IVectorStore vectorStore,
        IClock clock)
    {
        _repository = repository;
        _embeddings = embeddings;
        _vectorStore = vectorStore;
        _clock = clock;
    }

    public async Task<BugMemoryDto?> ExecuteAsync(UpdateBugMemoryCommand command, CancellationToken ct)
    {
        var entry = await _repository.GetByIdAsync(command.Id, ct);
        if (entry is null) return null;

        entry.Update(command.Title, command.Context, command.RootCause, command.Solution, command.Tags, _clock.UtcNow);
        await _repository.UpdateAsync(entry, ct);

        var embedding = await _embeddings.EmbedAsync(entry.ToEmbeddingText(), ct);
        await _vectorStore.UpsertAsync(
            entry.Id,
            embedding,
            new Dictionary<string, object> { ["entryId"] = entry.Id.ToString() },
            ct);

        return entry.ToDto();
    }
}
