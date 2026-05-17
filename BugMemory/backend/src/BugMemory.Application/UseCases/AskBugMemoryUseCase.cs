using BugMemory.Application.Abstractions;
using BugMemory.Application.Dtos;
using BugMemory.Application.Mapping;
using Microsoft.Extensions.Logging;

namespace BugMemory.Application.UseCases;

public sealed record AskBugMemoryQuery(string Question, int TopK);

/// <summary>
/// Ask query with per-source toggles. The Sources list contains stable
/// provider names: "bugs" for saved bug memories, plus any registered
/// IExternalSearchProvider.Name (currently "jira", "github"). Empty
/// list = saved bugs only (preserves legacy behavior).
/// </summary>
public sealed record AskWithSourcesQuery(
    string Question,
    int TopK,
    IReadOnlyList<string> Sources);

public sealed class AskBugMemoryUseCase
{
    /// <summary>Reserved source name for the in-process bug-memory vector store.</summary>
    public const string BugsSourceName = "bugs";

    private readonly IEmbeddingService _embeddings;
    private readonly IVectorStore _vectorStore;
    private readonly IBugMemoryRepository _repository;
    private readonly ILlmService _llm;
    private readonly IEnumerable<IExternalSearchProvider> _externalProviders;
    private readonly ILogger<AskBugMemoryUseCase> _logger;

    public AskBugMemoryUseCase(
        IEmbeddingService embeddings,
        IVectorStore vectorStore,
        IBugMemoryRepository repository,
        ILlmService llm,
        IEnumerable<IExternalSearchProvider> externalProviders,
        ILogger<AskBugMemoryUseCase> logger)
    {
        _embeddings = embeddings;
        _vectorStore = vectorStore;
        _repository = repository;
        _llm = llm;
        _externalProviders = externalProviders;
        _logger = logger;
    }

    /// <summary>
    /// Legacy entry point — saved bugs only. Preserved verbatim so the
    /// eval harness and any existing callers keep their contract.
    /// New callers should use <see cref="ExecuteWithSourcesAsync"/>.
    /// </summary>
    public async Task<RagResponseDto> ExecuteAsync(AskBugMemoryQuery query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query.Question))
            return new RagResponseDto("Please provide a question.", Array.Empty<SearchResultDto>());

        var topK = Math.Clamp(query.TopK, 1, 10);
        var (contexts, citations) = await RetrieveBugsAsync(query.Question, topK, ct);

        if (contexts.Count == 0)
        {
            return new RagResponseDto(
                citations.Count == 0
                    ? "I don't have any bug memories yet, or none matched your question."
                    : "No usable context retrieved.",
                Array.Empty<SearchResultDto>());
        }

        var answer = await _llm.AnswerWithContextAsync(query.Question, contexts, ct);

        var citedSet = new HashSet<Guid>(answer.CitedEntryIds);
        var filtered = citedSet.Count > 0
            ? citations.Where(c => citedSet.Contains(c.Entry.Id)).ToList()
            : citations;

        return new RagResponseDto(answer.Answer, filtered);
    }

    /// <summary>
    /// Source-aware entry point. Queries each requested source in
    /// parallel, merges hits, hands them to the LLM with provenance,
    /// returns answer + per-source citations.
    ///
    /// Failure mode: if a source throws (Jira unreachable, GitHub rate-
    /// limited, etc), it's recorded in SourceErrors and skipped — the
    /// answer still gets produced from whatever did work. A complete
    /// failure (all sources errored AND no saved-bug hits) returns
    /// the same 'no usable context' message as the legacy path.
    /// </summary>
    public async Task<MixedRagResponseDto> ExecuteWithSourcesAsync(
        AskWithSourcesQuery query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query.Question))
        {
            return new MixedRagResponseDto(
                "Please provide a question.",
                Array.Empty<SearchResultDto>(),
                Array.Empty<ExternalCitationDto>(),
                Array.Empty<string>(),
                Array.Empty<SourceErrorDto>());
        }

        var topK = Math.Clamp(query.TopK, 1, 10);
        var requested = NormalizeSources(query.Sources);
        var sourcesQueried = new List<string>();
        var sourceErrors = new List<SourceErrorDto>();

        // ---- saved bugs ----
        List<MixedContext> bugContexts = new();
        List<SearchResultDto> bugCitations = new();
        if (requested.Contains(BugsSourceName))
        {
            sourcesQueried.Add(BugsSourceName);
            try
            {
                var (retrievedContexts, retrievedCitations) =
                    await RetrieveBugsAsync(query.Question, topK, ct);
                bugCitations = retrievedCitations;
                bugContexts = retrievedContexts
                    .Select(c => new MixedContext(
                        Content: c.Content,
                        Score: c.Score,
                        InternalEntryId: c.EntryId,
                        ExternalProvider: null,
                        ExternalId: null,
                        ExternalUrl: null,
                        ExternalTitle: null))
                    .ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Bug retrieval failed");
                sourceErrors.Add(new SourceErrorDto(BugsSourceName, ex.Message));
            }
        }

        // ---- external providers ----
        var providersToRun = _externalProviders
            .Where(p => requested.Contains(p.Name))
            .ToList();
        foreach (var p in providersToRun) sourcesQueried.Add(p.Name);

        var externalTasks = providersToRun
            .Select(p => RunExternalProviderAsync(p, query.Question, topK, ct))
            .ToList();
        var externalResults = await Task.WhenAll(externalTasks);

        var externalContexts = new List<MixedContext>();
        var externalCitations = new List<ExternalCitationDto>();
        foreach (var result in externalResults)
        {
            if (result.Error is not null)
            {
                sourceErrors.Add(new SourceErrorDto(result.ProviderName, result.Error));
                continue;
            }
            foreach (var hit in result.Hits)
            {
                externalContexts.Add(new MixedContext(
                    Content: hit.Snippet,
                    Score: (float)hit.Score,
                    InternalEntryId: null,
                    ExternalProvider: hit.Provider,
                    ExternalId: hit.ExternalId,
                    ExternalUrl: hit.Url,
                    ExternalTitle: hit.Title));
                externalCitations.Add(new ExternalCitationDto(
                    Provider: hit.Provider,
                    ExternalId: hit.ExternalId,
                    Url: hit.Url,
                    Title: hit.Title,
                    When: hit.When,
                    Score: hit.Score,
                    Snippet: hit.Snippet));
            }
        }

        var mergedContexts = bugContexts
            .Concat(externalContexts)
            .OrderByDescending(c => c.Score)
            .ToList();

        if (mergedContexts.Count == 0)
        {
            var msg = sourceErrors.Count > 0
                ? "No usable context retrieved (one or more sources errored — see SourceErrors)."
                : "No usable context retrieved.";
            return new MixedRagResponseDto(
                msg,
                Array.Empty<SearchResultDto>(),
                Array.Empty<ExternalCitationDto>(),
                sourcesQueried,
                sourceErrors);
        }

        var answer = await _llm.AnswerWithMixedContextAsync(query.Question, mergedContexts, ct);

        // Filter citations to those actually cited by the LLM.
        var citedBugIds = new HashSet<Guid>(answer.CitedEntryIds);
        var filteredBugCitations = citedBugIds.Count > 0
            ? bugCitations.Where(c => citedBugIds.Contains(c.Entry.Id)).ToList()
            : bugCitations;

        var citedExternalIds = new HashSet<string>(answer.CitedExternalIds, StringComparer.Ordinal);
        var filteredExternalCitations = citedExternalIds.Count > 0
            ? externalCitations.Where(c => citedExternalIds.Contains(c.ExternalId)).ToList()
            : externalCitations;

        return new MixedRagResponseDto(
            answer.Answer,
            filteredBugCitations,
            filteredExternalCitations,
            sourcesQueried,
            sourceErrors);
    }

    // ----- helpers -----

    private HashSet<string> NormalizeSources(IReadOnlyList<string>? requested)
    {
        if (requested is null || requested.Count == 0)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase) { BugsSourceName };

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { BugsSourceName };
        foreach (var p in _externalProviders) known.Add(p.Name);

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in requested)
        {
            if (string.IsNullOrWhiteSpace(s)) continue;
            if (!known.Contains(s))
            {
                _logger.LogWarning("Unknown source '{Source}' requested — ignoring", s);
                continue;
            }
            result.Add(s);
        }
        return result;
    }

    private async Task<(List<RetrievedContext> Contexts, List<SearchResultDto> Citations)>
        RetrieveBugsAsync(string question, int topK, CancellationToken ct)
    {
        var queryEmbedding = await _embeddings.EmbedAsync(question, ct);
        var hits = await _vectorStore.SearchAsync(queryEmbedding, topK, ct);

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
        return (contexts, citations);
    }

    private async Task<ProviderRunResult> RunExternalProviderAsync(
        IExternalSearchProvider provider, string question, int topK, CancellationToken ct)
    {
        if (!provider.IsConfigured)
        {
            return new ProviderRunResult(
                provider.Name,
                Array.Empty<ExternalHit>(),
                $"Provider '{provider.Name}' is not configured (check appsettings).");
        }
        try
        {
            var hits = await provider.SearchAsync(question, topK, ct);
            return new ProviderRunResult(provider.Name, hits, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Provider {Name} threw during search", provider.Name);
            return new ProviderRunResult(provider.Name, Array.Empty<ExternalHit>(), ex.Message);
        }
    }

    private sealed record ProviderRunResult(
        string ProviderName,
        IReadOnlyList<ExternalHit> Hits,
        string? Error);
}
