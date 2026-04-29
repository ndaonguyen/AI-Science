using BugMemory.Application.Abstractions;
using BugMemory.Application.Dtos;
using BugMemory.Application.Mapping;
using BugMemory.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace BugMemory.Application.UseCases;

public sealed record CreateBugMemoryCommand(
    string Title,
    IReadOnlyList<string> Tags,
    string Context,
    string RootCause,
    string Solution);

public sealed class CreateBugMemoryUseCase
{
    private readonly IBugMemoryRepository _repository;
    private readonly IEmbeddingService _embeddings;
    private readonly IVectorStore _vectorStore;
    private readonly IClock _clock;
    private readonly ILogger<CreateBugMemoryUseCase> _logger;

    public CreateBugMemoryUseCase(
        IBugMemoryRepository repository,
        IEmbeddingService embeddings,
        IVectorStore vectorStore,
        IClock clock,
        ILogger<CreateBugMemoryUseCase> logger)
    {
        _repository = repository;
        _embeddings = embeddings;
        _vectorStore = vectorStore;
        _clock = clock;
        _logger = logger;
    }

    public async Task<BugMemoryDto> ExecuteAsync(CreateBugMemoryCommand command, CancellationToken ct)
    {
        var entry = BugMemoryEntry.Create(
            command.Title,
            command.Context,
            command.RootCause,
            command.Solution,
            command.Tags,
            _clock.UtcNow);

        await _repository.AddAsync(entry, ct);

        var embedding = await _embeddings.EmbedAsync(entry.ToEmbeddingText(), ct);
        await _vectorStore.UpsertAsync(
            entry.Id,
            embedding,
            new Dictionary<string, object> { ["entryId"] = entry.Id.ToString() },
            ct);

        _logger.LogInformation("Created bug memory {Id}", entry.Id);
        return entry.ToDto();
    }
}
