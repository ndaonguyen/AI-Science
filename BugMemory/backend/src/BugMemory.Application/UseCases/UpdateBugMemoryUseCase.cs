using BugMemory.Application.Abstractions;
using BugMemory.Application.Dtos;
using BugMemory.Application.Mapping;
using BugMemory.Domain.Entities;

namespace BugMemory.Application.UseCases;

public sealed record UpdateBugMemoryCommand(
    Guid Id,
    MemoryKind Kind,
    string Title,
    IReadOnlyList<string> Tags,
    string Context,
    string RootCause,
    string Solution,
    IReadOnlyList<string>? AffectedServices,
    IReadOnlyList<string>? Links);

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

        entry.Update(
            command.Kind,
            command.Title,
            command.Context,
            command.RootCause,
            command.Solution,
            command.Tags,
            command.AffectedServices,
            command.Links,
            _clock.UtcNow);
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
