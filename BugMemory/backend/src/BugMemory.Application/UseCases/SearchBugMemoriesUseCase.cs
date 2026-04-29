using BugMemory.Application.Abstractions;
using BugMemory.Application.Dtos;
using BugMemory.Application.Mapping;
using Microsoft.Extensions.Logging;

namespace BugMemory.Application.UseCases;

public sealed record SearchBugMemoriesQuery(string Query, int TopK);

public sealed class SearchBugMemoriesUseCase
{
    private readonly IEmbeddingService _embeddings;
    private readonly IVectorStore _vectorStore;
    private readonly IBugMemoryRepository _repository;
    private readonly ILogger<SearchBugMemoriesUseCase> _logger;

    public SearchBugMemoriesUseCase(
        IEmbeddingService embeddings,
        IVectorStore vectorStore,
        IBugMemoryRepository repository,
        ILogger<SearchBugMemoriesUseCase> logger)
    {
        _embeddings = embeddings;
        _vectorStore = vectorStore;
        _repository = repository;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SearchResultDto>> ExecuteAsync(SearchBugMemoriesQuery query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query.Query))
            return Array.Empty<SearchResultDto>();

        var queryEmbedding = await _embeddings.EmbedAsync(query.Query, ct);
        var hits = await _vectorStore.SearchAsync(queryEmbedding, Math.Clamp(query.TopK, 1, 20), ct);

        if (hits.Count == 0)
            return Array.Empty<SearchResultDto>();

        var results = new List<SearchResultDto>(hits.Count);
        foreach (var hit in hits)
        {
            var entry = await _repository.GetByIdAsync(hit.EntryId, ct);
            if (entry is null)
            {
                _logger.LogWarning("Vector hit {Id} has no matching repository entry — orphan", hit.EntryId);
                continue;
            }
            results.Add(new SearchResultDto(entry.ToDto(), hit.Score));
        }
        return results;
    }
}
