using BugMemory.Application.Abstractions;
using BugMemory.Application.UseCases;
using BugMemory.Domain.Entities;
using BugMemory.Infrastructure;
using BugMemory.Infrastructure.OpenAi;
using BugMemory.Infrastructure.Persistence;
using BugMemory.Infrastructure.Qdrant;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BugMemory.Eval;

/// <summary>
/// Runs the harness end-to-end: indexes the seed corpus into a dedicated
/// Qdrant collection, walks each config × each case, scores both
/// retrieval and the generated answer, and returns the result rows.
///
/// Two design choices worth flagging:
///
///   1. ISOLATED Qdrant collection. We use a separate collection
///      ("bug_memories_eval") so harness runs never touch the user's
///      production "bug_memories" data. Wiped + recreated at run start
///      so results are reproducible — same seed in, same retrieval out
///      (modulo OpenAI embedding-model nondeterminism, which is small).
///
///   2. Per-config DI scope. Each config builds a fresh service
///      provider configured with that config's chat model + temperature.
///      Verbose but ensures the harness exercises the EXACT SAME code
///      path the live API does (including any future prompt tweaks
///      inside OpenAiLlmService.AnswerWithContextAsync). Mocking the
///      LLM service would defeat the point.
/// </summary>
public sealed class HarnessRunner
{
    private const string EvalCollectionName = "bug_memories_eval";

    private readonly string _openAiApiKey;
    private readonly string _qdrantBaseUrl;
    private readonly AnswerGrader _grader;

    public HarnessRunner(string openAiApiKey, string qdrantBaseUrl)
    {
        _openAiApiKey = openAiApiKey;
        _qdrantBaseUrl = qdrantBaseUrl;
        _grader = new AnswerGrader(openAiApiKey);
    }

    public async Task<IReadOnlyList<HarnessRow>> RunAsync(
        IReadOnlyList<SeedBug> seed,
        IReadOnlyList<EvalCase> cases,
        IReadOnlyList<EvalConfig> configs,
        Action<HarnessRow>? onRowCompleted = null,
        CancellationToken ct = default)
    {
        // Clean slate for the harness's JSON repo. The repo persists
        // across runs by design (it's a real durable store for the live
        // app), so without an explicit wipe a second harness run would
        // see entries from the first run. The vector store wipe is
        // handled separately inside SeedAsync.
        if (File.Exists(HarnessJsonPath))
        {
            File.Delete(HarnessJsonPath);
        }

        // Build a one-off provider just for seeding — same Qdrant
        // collection name as the configs will read from. Embedding model
        // is fixed (text-embedding-3-small) since changing it requires
        // re-indexing anyway and isn't a config dimension.
        Console.WriteLine($"[harness] seeding {seed.Count} bugs into '{EvalCollectionName}' on {_qdrantBaseUrl}...");
        var (seedIdMap, seededProvider) = await SeedAsync(seed, ct);
        seededProvider.Dispose();
        Console.WriteLine($"[harness] seeded — id map has {seedIdMap.Count} entries.");

        var rows = new List<HarnessRow>(cases.Count * configs.Count);

        foreach (var cfg in configs)
        {
            // Fresh provider per config so OpenAiOptions can carry that
            // config's chat model. ServiceProvider is IDisposable; using
            // for cleanup.
            using var provider = BuildProviderForConfig(cfg);
            var ask = provider.GetRequiredService<AskBugMemoryUseCase>();
            var embeddings = provider.GetRequiredService<IEmbeddingService>();
            var vectorStore = provider.GetRequiredService<IVectorStore>();

            foreach (var @case in cases)
            {
                ct.ThrowIfCancellationRequested();

                HarnessRow row;
                try
                {
                    row = await RunOneAsync(@case, cfg, seedIdMap, ask, embeddings, vectorStore, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Failure on one case shouldn't stop the suite. Same
                    // convention as DistributedDebugger's V3 harness.
                    row = new HarnessRow(
                        ConfigId: cfg.Id,
                        CaseId: @case.Id,
                        Retrieval: new RetrievalScore(0, 0, 0, @case.ExpectedBugIds.Count, 0),
                        Answer: new AnswerGrade(false, $"(error: {ex.GetType().Name}: {ex.Message})"),
                        AnswerText: "",
                        Error: $"{ex.GetType().Name}: {ex.Message}");
                }
                rows.Add(row);
                onRowCompleted?.Invoke(row);
            }
        }

        return rows;
    }

    private async Task<HarnessRow> RunOneAsync(
        EvalCase @case,
        EvalConfig cfg,
        IReadOnlyDictionary<string, Guid> seedIdMap,
        AskBugMemoryUseCase ask,
        IEmbeddingService embeddings,
        IVectorStore vectorStore,
        CancellationToken ct)
    {
        // --- retrieval ---
        // We do an extra vector search BESIDES the one Ask does internally.
        // This is wasteful (~1.5x the work) but it gives us the retrieved
        // ids without having to crack open the Ask use case's internals.
        // For a harness with ~10 cases × 3 configs that's ~30 extra
        // searches per run — fractions of a second, no real cost.
        var queryEmbedding = await embeddings.EmbedAsync(@case.Question, ct);
        var hits = await vectorStore.SearchAsync(queryEmbedding, cfg.TopK, ct);
        var retrievedIds = hits.Select(h => h.EntryId).ToList();

        var expectedGuids = @case.ExpectedBugIds
            .Select(id => seedIdMap.TryGetValue(id, out var g) ? g : (Guid?)null)
            .Where(g => g.HasValue)
            .Select(g => g!.Value)
            .ToList();

        var retrievalScore = RetrievalGrader.Grade(retrievedIds, expectedGuids);

        // --- answer ---
        var ragResponse = await ask.ExecuteAsync(
            new AskBugMemoryQuery(@case.Question, cfg.TopK), ct);

        var answerGrade = await _grader.GradeAsync(
            @case.Question, @case.AnswerCriteria, ragResponse.Answer, ct);

        return new HarnessRow(
            ConfigId: cfg.Id,
            CaseId: @case.Id,
            Retrieval: retrievalScore,
            Answer: answerGrade,
            AnswerText: ragResponse.Answer,
            Error: null);
    }

    /// <summary>
    /// Seed the eval collection. Returns the stable-id → Guid map so
    /// cases can later say 'I expect kafka-retry-dup-key' and the
    /// retrieval grader can translate that to the actual Guid that came
    /// back from Qdrant.
    /// </summary>
    private async Task<(Dictionary<string, Guid> Map, ServiceProvider Provider)> SeedAsync(
        IReadOnlyList<SeedBug> seed,
        CancellationToken ct)
    {
        var provider = BuildProviderForConfig(EvalConfig.Baseline);
        try
        {
            var vectorStore = provider.GetRequiredService<IVectorStore>();
            var embeddings = provider.GetRequiredService<IEmbeddingService>();
            var repository = provider.GetRequiredService<IBugMemoryRepository>();
            var clock = provider.GetRequiredService<IClock>();

            // Wipe and recreate. EnsureCollectionAsync is idempotent, but
            // we want a clean slate — leftover entries from a previous run
            // would inflate Recall scores.
            await WipeCollectionAsync(ct);
            await vectorStore.EnsureCollectionAsync(ct);

            var map = new Dictionary<string, Guid>(seed.Count);
            foreach (var s in seed)
            {
                var entry = BugMemoryEntry.Create(
                    s.Title, s.Context, s.RootCause, s.Solution,
                    s.Tags, clock.UtcNow);
                var vec = await embeddings.EmbedAsync(entry.ToEmbeddingText(), ct);
                await vectorStore.UpsertAsync(
                    entry.Id, vec,
                    new Dictionary<string, object> { ["title"] = entry.Title },
                    ct);
                // Critical: also persist to the JSON repository. Ask
                // retrieves vector hits, then looks each up in the repo
                // to fetch full content. If we skip this, Ask sees the
                // hits but logs 'no matching entry' and returns 'No
                // usable context retrieved' for every case.
                await repository.AddAsync(entry, ct);
                map[s.Id] = entry.Id;
            }
            return (map, provider);
        }
        catch
        {
            provider.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Wipe the eval collection by deleting it outright via Qdrant's HTTP
    /// API. This is the cleanest way — Qdrant doesn't have a 'truncate'
    /// op; the next EnsureCollectionAsync call will recreate it.
    /// </summary>
    private async Task WipeCollectionAsync(CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = new Uri(_qdrantBaseUrl) };
        // 404 is fine — the collection might not exist yet on first run.
        // Other errors propagate.
        var resp = await http.DeleteAsync($"collections/{EvalCollectionName}", ct);
        if (resp.StatusCode != System.Net.HttpStatusCode.NotFound &&
            !resp.IsSuccessStatusCode)
        {
            string body;
            try { body = await resp.Content.ReadAsStringAsync(ct); }
            catch { body = "(could not read body)"; }
            throw new InvalidOperationException(
                $"Qdrant collection wipe failed: {(int)resp.StatusCode}: {body}");
        }
    }

    /// <summary>
    /// Build a service provider with the OpenAi + Qdrant + Application
    /// services wired up the way the live API does, but with this
    /// config's chat model and temperature in OpenAiOptions, and the
    /// harness Qdrant collection name.
    ///
    /// Persistence (the JSON file repository) is also wired but pointed
    /// at a temp-file path so the harness doesn't write into the user's
    /// real bug-memories.json. The JSON repo is needed because Ask reads
    /// full entries from it after the vector search.
    /// </summary>
    private ServiceProvider BuildProviderForConfig(EvalConfig cfg)
    {
        var inMemoryConfig = new Dictionary<string, string?>
        {
            ["OpenAi:ApiKey"]              = _openAiApiKey,
            ["OpenAi:ChatModel"]           = cfg.ChatModel,
            ["OpenAi:EmbeddingModel"]      = "text-embedding-3-small",
            ["OpenAi:EmbeddingDimensions"] = "1536",
            ["Qdrant:BaseUrl"]             = _qdrantBaseUrl,
            ["Qdrant:CollectionName"]      = EvalCollectionName,
            ["Qdrant:VectorSize"]          = "1536",
            ["Qdrant:Distance"]            = "Cosine",
            ["Storage:FilePath"]           = HarnessJsonPath,
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemoryConfig!)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();  // null logger via abstractions below
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddApplication();
        services.AddInfrastructure(configuration);

        // Note: temperature isn't currently passed through OpenAiOptions
        // to OpenAiLlmService. Adding that requires a small Infrastructure
        // change — not done in this PR to keep the harness scope tight.
        // The cfg.Temperature setting is ignored for now; the live
        // service's hard-coded 0.3 (extract) and 0.3 (answer) are used.
        // TODO: thread temperature through OpenAiOptions.

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Path to the JSON file the JsonFileBugMemoryRepository will use
    /// during harness runs. Lives in the OS temp dir so it never
    /// collides with the user's production data.
    /// </summary>
    private static string HarnessJsonPath =>
        Path.Combine(Path.GetTempPath(), "bug-memory-eval.json");
}

public sealed record HarnessRow(
    string ConfigId,
    string CaseId,
    RetrievalScore Retrieval,
    AnswerGrade Answer,
    string AnswerText,
    string? Error);
