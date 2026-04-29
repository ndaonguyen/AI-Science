using BugMemory.Application.Abstractions;
using BugMemory.Application.Dtos;
using BugMemory.Application.Mapping;
using Microsoft.Extensions.Logging;

namespace BugMemory.Application.UseCases;

public sealed record AskBugMemoryQuery(string Question, int TopK);

public sealed class AskBugMemoryUseCase
{
    private readonly IEmbeddingService _embeddings;
    private readonly IVectorStore _vectorStore;
    private readonly IBugMemoryRepository _repository;
    private readonly ILlmService _llm;
    private readonly ILogger<AskBugMemoryUseCase> _logger;

    public AskBugMemoryUseCase(
        IEmbeddingService embeddings,
        IVectorStore vectorStore,
        IBugMemoryRepository repository,
        ILlmService llm,
        ILogger<AskBugMemoryUseCase> logger)
    {
        _embeddings = embeddings;
        _vectorStore = vectorStore;
        _repository = repository;
        _llm = llm;
        _logger = logger;
    }

    public async Task<RagResponseDto> ExecuteAsync(AskBugMemoryQuery query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query.Question))
            return new RagResponseDto("Please provide a question.", Array.Empty<SearchResultDto>());

        var topK = Math.Clamp(query.TopK, 1, 10);
        var queryEmbedding = await _embeddings.EmbedAsync(query.Question, ct);
        var hits = await _vectorStore.SearchAsync(queryEmbedding, topK, ct);

        if (hits.Count == 0)
        {
            return new RagResponseDto(
                "I don't have any bug memories yet, or none matched your question.",
                Array.Empty<SearchResultDto>());
        }

        var contexts = new List<RetrievedContext>(hits.Count);
        var citations = new List<SearchResultDto>(hits.Count);

        foreach (var hit in hits)
        {
            var entry = await _repository.GetByIdAsync(hit.EntryId, ct);
            if (entry is null)
            {
                _logger.LogWarning("Vector hit {Id} has no matching entry", hit.EntryId);
                continue;
            }
            contexts.Add(new RetrievedContext(entry.Id, entry.ToEmbeddingText(), hit.Score));
            citations.Add(new SearchResultDto(entry.ToDto(), hit.Score));
        }

        if (contexts.Count == 0)
        {
            return new RagResponseDto("No usable context retrieved.", Array.Empty<SearchResultDto>());
        }

        var answer = await _llm.AnswerWithContextAsync(query.Question, contexts, ct);

        // Filter citations to only those the LLM actually cited
        var citedSet = new HashSet<Guid>(answer.CitedEntryIds);
        var filteredCitations = citedSet.Count > 0
            ? citations.Where(c => citedSet.Contains(c.Entry.Id)).ToList()
            : citations;

        return new RagResponseDto(answer.Answer, filteredCitations);
    }
}
