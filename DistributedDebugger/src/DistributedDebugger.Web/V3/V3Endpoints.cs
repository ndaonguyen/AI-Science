using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;

namespace DistributedDebugger.Web.V3;

/// <summary>
/// V3 endpoints — same shape as V2 but with RAG retrieval applied to the
/// gathered log set when it crosses a configurable threshold (default 100
/// entries). All three actions remain stateless on the server. Three actions, all stateless on the server:
///
///   POST /api/v3/logs/filter    — keyword + range  → log records
///   POST /api/v3/logs/extend    — pivot + window   → log records
///   POST /api/v3/logs/analyze   — logs + context   → analysis result
///
/// The browser owns the accumulated set of logs across multiple
/// filter/extend calls. The server is a thin adapter over CloudWatch +
/// the analyzer. No GuidedSession, no agent loop, no SSE — these are
/// plain request/response calls because a single fetch is fast enough
/// (no multi-turn streaming to wait through).
/// </summary>
public static class V3Endpoints
{
    public static void MapV3Endpoints(this WebApplication app)
    {
        // Singleton-ish: one CloudWatch client per process. Cached AWS
        // clients live on it.
        var cwClient = new CloudWatchLogClient();
        app.Lifetime.ApplicationStopping.Register(() => cwClient.Dispose());

        // Load schema markdowns once at startup; the LogAnalyzer prepends
        // them to every analyze prompt so the model always knows the shape
        // of CoCo's authoring-service / content-search-service collections
        // without the user having to repeat it in every bug description.
        var schemas = new SchemaLoader();

        // Investigation memory — persists past analyses so similar future
        // bugs can retrieve them as 'related past investigations'. Lazy:
        // we don't construct the SQLite-backed store until the first request
        // that needs it, so a process that never calls /analyze never opens
        // the db. Path can be overridden via DD_MEMORY_DB_PATH (used by the
        // eval harness to keep its synthetic cases out of your real memory).
        IInvestigationMemory? memoryStore = null;
        IInvestigationMemory GetMemoryStore()
        {
            // Double-check is fine — endpoint handlers already serialise
            // around the singleton via ASP.NET's request pipeline; we only
            // race ourselves on first construction.
            if (memoryStore is not null) return memoryStore;
            var path = Environment.GetEnvironmentVariable("DD_MEMORY_DB_PATH");
            if (string.IsNullOrWhiteSpace(path))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                path = Path.Combine(home, ".dd", "memory.db");
            }
            memoryStore = new SqliteVecInvestigationMemory(path);
            Console.Error.WriteLine($"[v3/memory] store initialised at {path}");
            return memoryStore;
        }

        app.MapPost("/api/v3/logs/filter", async (
            FilterRequest req, HttpContext ctx, CancellationToken ct) =>
        {
            // Wrap every endpoint in a try/catch returning a JSON error so the
            // browser never sees an empty 500 body (which causes JSON.parse
            // failures on the client). The framework's default 500 response
            // is empty unless we configure a developer exception page; we
            // prefer not to depend on that being on.
            try
            {
                var error = ValidateFilter(req);
                if (error is not null) return Results.BadRequest(new { error });

                var (start, end) = ResolveRange(req.StartTime, req.EndTime);
                var pattern = NormaliseFilterPattern(req.FilterText ?? "");
                var allLogs = new List<LogRecord>();

                // One AWS call per service — they have independent log groups.
                foreach (var svc in req.Services!)
                {
                    var logs = await cwClient.SearchAsync(
                        svc, req.Environment ?? "dev", pattern, start, end,
                        limit: req.Limit ?? 500, ct);
                    allLogs.AddRange(logs);
                }

                // Server-side ordering by timestamp gives the browser a stable
                // baseline regardless of the order CloudWatch returned events
                // (CloudWatch's order is per-stream, not per-log-group).
                allLogs.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

                return Results.Ok(new
                {
                    logs = allLogs,
                    appliedFilter = pattern,
                    window = new { start, end },
                });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[v3/filter] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                return Results.Json(new { error = $"{ex.GetType().Name}: {ex.Message}" }, statusCode: 500);
            }
        });

        app.MapPost("/api/v3/logs/extend", async (
            ExtendRequest req, HttpContext ctx, CancellationToken ct) =>
        {
            try
            {
                var error = ValidateExtend(req);
                if (error is not null) return Results.BadRequest(new { error });

                var pivot = req.Around!.Value;
                var minutes = req.WindowMinutes ?? 1;
                // ±0 means 'logs at exactly this timestamp' — same millisecond
                // as the pivot. CloudWatch's FilterLogEvents API requires
                // start < end, not start <= end, so we widen by 1ms only.
                // Result: only events whose @timestamp == pivot are returned.
                if (minutes <= 0)
                {
                    var allLogs0 = new List<LogRecord>();
                    foreach (var svc in req.Services!)
                    {
                        var logs = await cwClient.SearchAsync(
                            svc, req.Environment ?? "dev", "",
                            pivot, pivot.AddMilliseconds(1),
                            limit: req.Limit ?? 500, ct);
                        allLogs0.AddRange(logs);
                    }
                    allLogs0.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
                    return Results.Ok(new
                    {
                        logs = allLogs0,
                        window = new { start = pivot, end = pivot.AddMilliseconds(1) },
                    });
                }
                var window = TimeSpan.FromMinutes(minutes);
                var allLogs = new List<LogRecord>();

                foreach (var svc in req.Services!)
                {
                    var logs = await cwClient.ExtendAsync(
                        svc, req.Environment ?? "dev", pivot, window,
                        limit: req.Limit ?? 500, ct);
                    allLogs.AddRange(logs);
                }
                allLogs.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

                return Results.Ok(new
                {
                    logs = allLogs,
                    window = new { start = pivot - window, end = pivot + window },
                });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[v3/extend] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                return Results.Json(new { error = $"{ex.GetType().Name}: {ex.Message}" }, statusCode: 500);
            }
        });

        app.MapPost("/api/v3/logs/analyze", async (
            AnalyzeRequest req, IConfiguration cfg, CancellationToken ct) =>
        {
            try
            {
                if (req.Logs is null || req.Logs.Count == 0)
                    return Results.BadRequest(new { error = "no logs to analyse" });
                if (string.IsNullOrWhiteSpace(req.Description))
                    return Results.BadRequest(new { error = "description is required" });

                var openaiKey = cfg["OPENAI_API_KEY"]
                                ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
                if (string.IsNullOrWhiteSpace(openaiKey))
                    return Results.Json(new { error = "OPENAI_API_KEY is not configured" }, statusCode: 500);

                var evidence = (req.Evidence ?? Array.Empty<EvidenceItem>())
                    .Where(e => !string.IsNullOrWhiteSpace(e.Content))
                    .ToList();

                // ---- RAG step ----
                // Threshold and topK are env-overridable so we can tune them
                // without redeploying. Defaults: 100 / 25 — pull from research
                // on how many relevant log lines the gpt-4o-mini context can
                // really attend to without losing the plot.
                var threshold = ParseEnvInt("V3_RAG_THRESHOLD", defaultValue: 100);
                var topK      = ParseEnvInt("V3_RAG_TOPK",      defaultValue: 25);

                // Query: bug description plus evidence titles. Titles often
                // contain ids and collection names that are exactly the
                // keywords/concepts we want to surface in the logs.
                var query = req.Description!.Trim();
                if (evidence.Count > 0)
                {
                    var titles = string.Join(" | ", evidence
                        .Select(e => e.Title)
                        .Where(t => !string.IsNullOrWhiteSpace(t)));
                    if (!string.IsNullOrEmpty(titles))
                        query = $"{query} | {titles}";
                }

                var rag = new RagPipeline(openaiKey, threshold, topK);
                var ragOutcome = await rag.ApplyAsync(query, req.Logs!, ct);

                Console.Error.WriteLine(
                    ragOutcome.Used
                        ? $"[v3/analyze] RAG: {ragOutcome.FromCount} → {ragOutcome.KeptCount} (threshold {ragOutcome.Threshold}, topK {topK})"
                        : $"[v3/analyze] RAG: skipped — {ragOutcome.FromCount} ≤ threshold {ragOutcome.Threshold}");

                // ---- Memory step ----
                // Decide whether to use memory: per-request flag wins, else
                // env default (on unless DD_MEMORY_DISABLED). When off we
                // still WRITE the result afterwards (so memory keeps growing
                // even during 'no-memory' runs) — only the read side is
                // skipped.
                var memoryOff = string.Equals(
                    Environment.GetEnvironmentVariable("DD_MEMORY_DISABLED"),
                    "1", StringComparison.OrdinalIgnoreCase);
                var useMemory = req.UseMemory ?? !memoryOff;

                MemoryReadOutcome memoryOutcome = MemoryReadOutcome.Skipped("disabled");
                MemoryPipeline? memoryPipeline = null;
                string? memorySection = null;
                if (useMemory)
                {
                    var memTopK = ParseEnvInt("V3_MEMORY_TOPK", defaultValue: 3);
                    var memMinSim = ParseEnvFloat("V3_MEMORY_MIN_SIM", defaultValue: 0.5f);
                    try
                    {
                        memoryPipeline = new MemoryPipeline(
                            GetMemoryStore(), openaiKey, memTopK, memMinSim);
                        memoryOutcome = await memoryPipeline.ReadAsync(req.Description!, ct);
                        memorySection = MemoryPipeline.RenderPromptSection(memoryOutcome.Hits);
                        Console.Error.WriteLine(
                            memoryOutcome.Used
                                ? $"[v3/analyze] memory: {memoryOutcome.Hits.Count} hits of {memoryOutcome.CorpusSize} (min similarity {memMinSim})"
                                : $"[v3/analyze] memory: skipped — {memoryOutcome.SkipReason}");
                    }
                    catch (Exception ex)
                    {
                        // Memory init itself blew up (vec0 missing, db locked,
                        // etc). Log and continue — analysis is the priority.
                        Console.Error.WriteLine(
                            $"[v3/analyze] memory: init failed, continuing without — {ex.GetType().Name}: {ex.Message}");
                        memoryOutcome = MemoryReadOutcome.Skipped($"init failed: {ex.Message}");
                    }
                }

                // ---- LLM analysis with the RAG-narrowed set + memory section ----
                var analyzer = new LogAnalyzer(openaiKey);
                var result = await analyzer.AnalyzeAsync(
                    req.Description, req.TicketId, ragOutcome.Logs,
                    evidence, schemas.All, ct,
                    memorySection: memorySection);

                // ---- Memory write (after success) ----
                // Persist this analysis to memory so future similar bugs can
                // retrieve it. We honour the toggle literally: when memory
                // is off (request flag false or env disabled), memoryPipeline
                // is null and we skip BOTH read and write. That respects the
                // user's explicit choice — flipping memory off shouldn't
                // silently keep recording.
                if (memoryPipeline is not null && !string.IsNullOrWhiteSpace(result.Hypothesis))
                {
                    var entry = new InvestigationMemoryEntry(
                        Id: Guid.NewGuid().ToString("N"),
                        CreatedAt: DateTimeOffset.UtcNow,
                        Description: req.Description!.Trim(),
                        Hypothesis: result.Hypothesis,
                        EvidenceSummary: SummariseEvidence(evidence),
                        TicketId: req.TicketId,
                        SchemasIncluded: result.SchemasIncluded,
                        // Reuse the embedding from the read step to avoid
                        // paying for a second embeddings call. If read was
                        // skipped before reaching the embed call (memory off
                        // entirely), the embedding is empty and we'll skip
                        // the write — that's the right call: we'd need to
                        // pay for an extra embed just to remember a result
                        // the user explicitly opted out of remembering.
                        Embedding: memoryOutcome.QueryEmbedding);
                    if (entry.Embedding.Length > 0)
                    {
                        await memoryPipeline.WriteAsync(entry, ct);
                    }
                }

                // Surface RAG bookkeeping in the response so the UI can show
                // 'RAG: kept 25 of 240 (threshold 100)' or 'RAG: skipped'.
                return Results.Ok(new
                {
                    result.Summary,
                    result.Suspicious,
                    result.Hypothesis,
                    result.SuggestedFollowups,
                    result.SchemasIncluded,
                    result.InputTokens,
                    result.OutputTokens,
                    rag = new
                    {
                        used      = ragOutcome.Used,
                        threshold = ragOutcome.Threshold,
                        fromCount = ragOutcome.FromCount,
                        keptCount = ragOutcome.KeptCount,
                        topK,
                    },
                    memory = new
                    {
                        enabled       = useMemory,
                        used          = memoryOutcome.Used,
                        retrievedCount= memoryOutcome.Hits.Count,
                        corpusSize    = memoryOutcome.CorpusSize,
                        skipReason    = memoryOutcome.SkipReason,
                    },
                });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[v3/analyze] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                return Results.Json(new { error = $"{ex.GetType().Name}: {ex.Message}" }, statusCode: 500);
            }
        });

        // POST /api/v3/logs/suggest-queries
        // Optional follow-up to /analyze. Takes the analysis the user just
        // received and produces a list of executable queries (Mongo /
        // OpenSearch / Kafka) that would confirm or rule out the hypothesis.
        // On-demand: only fires when the user clicks 'Suggest queries' in
        // the UI. Doesn't re-process raw logs (~8K tokens) — the analysis
        // output already distills what's relevant.
        app.MapPost("/api/v3/logs/suggest-queries", async (
            SuggestQueriesRequest req,
            CancellationToken ct) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(req.Description))
                    return Results.Json(new { error = "description is required" }, statusCode: 400);
                if (req.Analysis is null || string.IsNullOrWhiteSpace(req.Analysis.Hypothesis))
                    return Results.Json(new { error = "analysis with hypothesis is required" }, statusCode: 400);

                var openaiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                    ?? throw new InvalidOperationException("OPENAI_API_KEY not set.");

                var evidence = (req.Evidence ?? Array.Empty<EvidenceItem>())
                    .Where(e => !string.IsNullOrWhiteSpace(e.Content) || !string.IsNullOrWhiteSpace(e.Title))
                    .ToList();

                var analysisInput = new AnalysisInput(
                    Summary: req.Analysis.Summary,
                    Suspicious: req.Analysis.Suspicious ?? Array.Empty<string>(),
                    Hypothesis: req.Analysis.Hypothesis,
                    SuggestedFollowups: req.Analysis.SuggestedFollowups ?? Array.Empty<string>());

                var suggester = new QuerySuggester(openaiKey);
                var result = await suggester.SuggestAsync(
                    req.Description, req.TicketId, analysisInput, evidence, schemas.All, ct);

                Console.Error.WriteLine(
                    $"[v3/suggest-queries] {result.Suggestions.Count} suggestions ({result.InputTokens}+{result.OutputTokens} tok)");

                return Results.Ok(new
                {
                    suggestions = result.Suggestions,
                    inputTokens = result.InputTokens,
                    outputTokens = result.OutputTokens,
                });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[v3/suggest-queries] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                return Results.Json(new { error = $"{ex.GetType().Name}: {ex.Message}" }, statusCode: 500);
            }
        });
    }

    /// <summary>
    /// Parse an integer environment variable with a default. Returns the
    /// default for missing or unparseable values so a typo in deployment
    /// config doesn't crash the process.
    /// </summary>
    private static int ParseEnvInt(string name, int defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw)) return defaultValue;
        return int.TryParse(raw, out var n) && n > 0 ? n : defaultValue;
    }

    /// <summary>
    /// Parse a float environment variable with a default. Used for the
    /// memory similarity threshold (V3_MEMORY_MIN_SIM); InvariantCulture so
    /// '0.5' is parsed the same on comma-locale machines.
    /// </summary>
    private static float ParseEnvFloat(string name, float defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw)) return defaultValue;
        return float.TryParse(raw, System.Globalization.NumberStyles.Float,
                              System.Globalization.CultureInfo.InvariantCulture,
                              out var n) && n >= 0
            ? n : defaultValue;
    }

    /// <summary>
    /// One-liner summary of pasted evidence — kind + title — joined for
    /// memory storage. We intentionally don't store full evidence content
    /// here (would bloat the db quickly and isn't needed for the 'have I
    /// seen this before' use case). The current investigation gets full
    /// evidence content; the past-investigation card just gets the
    /// labels.
    /// </summary>
    private static string SummariseEvidence(IReadOnlyList<EvidenceItem> evidence)
    {
        if (evidence.Count == 0) return "";
        return string.Join("; ", evidence
            .Select(e => string.IsNullOrWhiteSpace(e.Title)
                ? (e.Kind ?? "note")
                : $"{e.Kind ?? "note"}: {e.Title}"));
    }

    // ---- request types ----

    public sealed record FilterRequest(
        IReadOnlyList<string>? Services,
        string? Environment,
        string? FilterText,
        DateTimeOffset? StartTime,
        DateTimeOffset? EndTime,
        int? Limit);

    public sealed record ExtendRequest(
        IReadOnlyList<string>? Services,
        string? Environment,
        DateTimeOffset? Around,
        int? WindowMinutes,
        int? Limit);

    public sealed record AnalyzeRequest(
        string? Description,
        string? TicketId,
        IReadOnlyList<LogRecord>? Logs,
        // Optional supporting evidence the user has gathered alongside the
        // logs — Mongo documents, OpenSearch query results, Kafka event
        // payloads, or free-form notes. Threaded into the prompt as labelled
        // sections so the LLM can correlate logs with state in other systems.
        IReadOnlyList<EvidenceItem>? Evidence,
        // Per-request override of the memory feature. Null = follow the
        // process default (which is on unless V3_MEMORY_DISABLED is set);
        // false = explicitly skip memory for this analyze; true = explicitly
        // include even if the env disabled it. Lets the UI A/B without a
        // restart and lets the harness force memory off for 'no-memory'
        // configs.
        bool? UseMemory);

    /// <summary>
    /// One piece of supporting evidence pasted in by the user. Kind is one of
    /// "mongo" / "opensearch" / "kafka" / "note" — used to label the section
    /// in the prompt and pick the right wording. Title is short context the
    /// user provides ("activities collection — _id act-789"), Command is the
    /// query/shell-command they ran to get the result (e.g.
    /// db.activities.findOne({_id: ...})) — captured so the LLM can reason
    /// about the SHAPE of what was asked, not just what came back. Content
    /// is the raw payload they pasted (a JSON document, a query hit, an
    /// event body). For "note" evidence Command is unused.
    /// </summary>
    public sealed record EvidenceItem(
        string? Kind,
        string? Title,
        string? Command,
        string? Content);

    /// <summary>
    /// Request body for the /suggest-queries endpoint. The analysis subset
    /// is the JSON-shape the V3 /analyze response uses, sent back so the
    /// suggester can ground its queries in the just-completed analysis
    /// without re-processing the raw logs.
    /// </summary>
    public sealed record SuggestQueriesRequest(
        string? Description,
        string? TicketId,
        IReadOnlyList<EvidenceItem>? Evidence,
        AnalysisSubset? Analysis);

    /// <summary>
    /// Just the parts of <see cref="AnalysisResult"/> the suggester actually
    /// needs. Schema-included list, token counts, etc are dropped — they
    /// don't help the suggester. Mirrors <see cref="AnalysisInput"/>.
    /// </summary>
    public sealed record AnalysisSubset(
        string? Summary,
        IReadOnlyList<string>? Suspicious,
        string Hypothesis,
        IReadOnlyList<string>? SuggestedFollowups);

    // ---- helpers ----

    private static string? ValidateFilter(FilterRequest r) =>
        (r.Services is null || r.Services.Count == 0) ? "services is required" : null;

    private static string? ValidateExtend(ExtendRequest r) =>
        r.Around is null ? "around (timestamp) is required"
        : (r.Services is null || r.Services.Count == 0) ? "services is required"
        : null;

    /// <summary>
    /// Defaults a missing range to "last 1 hour from now" so the form is
    /// usable without configuring a time picker for the simple case.
    /// </summary>
    private static (DateTimeOffset start, DateTimeOffset end) ResolveRange(
        DateTimeOffset? start, DateTimeOffset? end)
    {
        if (start is not null && end is not null) return (start.Value, end.Value);
        var now = DateTimeOffset.UtcNow;
        return (now.AddHours(-1), now);
    }

    /// <summary>
    /// Same normalisation rule as the V1 enforcing tool: multi-word plain
    /// text gets double-quoted so AWS treats it as a single literal phrase
    /// rather than space-separated AND'd terms. CloudWatch syntax users
    /// (strings starting with " { ? -) pass through untouched.
    /// </summary>
    internal static string NormaliseFilterPattern(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0) return trimmed;
        var first = trimmed[0];
        if (first == '"' || first == '{' || first == '?' || first == '-') return trimmed;
        if (!trimmed.Contains(' ')) return trimmed;
        return "\"" + trimmed.Replace("\"", "\\\"") + "\"";
    }
}
