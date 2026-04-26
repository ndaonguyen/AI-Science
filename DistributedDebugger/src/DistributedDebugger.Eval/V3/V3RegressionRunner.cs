using DistributedDebugger.Web.V3;

namespace DistributedDebugger.Eval.V3;

/// <summary>
/// Runs a list of V3 eval cases through <see cref="LogAnalyzer"/> and grades
/// each result via the existing <see cref="LlmAsJudgeGrader"/>.
///
/// Sequential by design — same reasoning as <see cref="RegressionRunner"/>:
/// at this scale (a handful of cases) the wall-time saving from parallelism
/// isn't worth the complexity of rate-limit juggling and ordered output. If
/// the suite ever grows past 50 cases, revisit.
///
/// One configuration knob worth highlighting: <see cref="V3RegressionConfig"/>
/// captures whether RAG runs and at what threshold, plus the analyzer model.
/// That lets you do exactly the kind of A/B that V3 exists to enable —
/// 'baseline (no RAG)' vs 'rag-100' vs 'rag-50' as separately-named configs
/// run over the same case set.
/// </summary>
public sealed class V3RegressionRunner
{
    private readonly LlmAsJudgeGrader _grader;
    private readonly string _openaiKey;
    private readonly IReadOnlyList<SchemaDoc> _schemas;

    public V3RegressionRunner(
        LlmAsJudgeGrader grader,
        string openaiKey,
        IReadOnlyList<SchemaDoc> schemas)
    {
        _grader = grader;
        _openaiKey = openaiKey;
        _schemas = schemas;
    }

    public async Task<IReadOnlyList<V3RegressionRow>> RunAsync(
        IReadOnlyList<EvalCaseV3> cases,
        IReadOnlyList<V3RegressionConfig> configs,
        Action<V3RegressionRow>? onRowCompleted = null,
        CancellationToken ct = default)
    {
        var rows = new List<V3RegressionRow>(cases.Count * configs.Count);

        foreach (var cfg in configs)
        {
            foreach (var @case in cases)
            {
                ct.ThrowIfCancellationRequested();

                V3RegressionRow row;
                try
                {
                    row = await RunOneAsync(@case, cfg, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // A thrown analysis shouldn't kill the suite — record it
                    // as a failure and carry on. Same convention as V1's
                    // RegressionRunner.
                    row = new V3RegressionRow(
                        ConfigId: cfg.Id,
                        CaseId: @case.Id,
                        Passed: false,
                        Error: $"{ex.GetType().Name}: {ex.Message}",
                        InputTokens: 0,
                        OutputTokens: 0,
                        WallTime: TimeSpan.Zero,
                        RagUsed: false,
                        RagFromCount: @case.Logs.Count,
                        RagKeptCount: @case.Logs.Count,
                        Grade: null);
                }

                rows.Add(row);
                onRowCompleted?.Invoke(row);
            }
        }

        return rows;
    }

    private async Task<V3RegressionRow> RunOneAsync(
        EvalCaseV3 @case, V3RegressionConfig cfg, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;

        // Optional RAG step. Mirrors the path the live /api/v3/logs/analyze
        // endpoint takes, so eval grades reflect what the user actually sees.
        IReadOnlyList<LogRecord> analyzeLogs = @case.Logs;
        bool ragUsed = false;
        int ragFromCount = @case.Logs.Count;
        int ragKeptCount = @case.Logs.Count;

        if (cfg.RagEnabled)
        {
            var query = BuildRagQuery(@case);
            var rag = new RagPipeline(_openaiKey, cfg.RagThreshold, cfg.RagTopK);
            var outcome = await rag.ApplyAsync(query, @case.Logs, ct);
            analyzeLogs = outcome.Logs;
            ragUsed = outcome.Used;
            ragFromCount = outcome.FromCount;
            ragKeptCount = outcome.KeptCount;
        }

        // Adapt evidence to the V3Endpoints type the analyzer expects.
        var evidence = @case.Evidence
            .Select(e => new V3Endpoints.EvidenceItem(
                Kind: e.Kind, Title: e.Title, Command: e.Command, Content: e.Content))
            .ToList();

        var analyzer = new LogAnalyzer(_openaiKey, model: cfg.AnalyzerModel);
        var result = await analyzer.AnalyzeAsync(
            bugDescription: @case.Description,
            ticketId: @case.TicketId,
            logs: analyzeLogs,
            evidence: evidence,
            schemas: _schemas,
            ct);

        var finishedAt = DateTimeOffset.UtcNow;

        // Adapt to V1 shapes for the grader. Existing LlmAsJudgeGrader is
        // unchanged — that's the win of the adapter approach.
        var investigation = AnalysisToInvestigationAdapter.ToInvestigation(
            @case, result, startedAt, finishedAt);
        var v1Case = AnalysisToInvestigationAdapter.ToEvalCase(@case);

        var grade = await _grader.GradeAsync(v1Case, investigation, ct);

        return new V3RegressionRow(
            ConfigId: cfg.Id,
            CaseId: @case.Id,
            Passed: grade.Passed,
            Error: null,
            InputTokens: result.InputTokens,
            OutputTokens: result.OutputTokens,
            WallTime: finishedAt - startedAt,
            RagUsed: ragUsed,
            RagFromCount: ragFromCount,
            RagKeptCount: ragKeptCount,
            Grade: grade);
    }

    /// <summary>
    /// Build the query the RAG retriever uses. Same shape as the live
    /// endpoint: bug description plus evidence titles. Keeping eval and live
    /// query construction in sync means the harness measures what users
    /// actually experience.
    /// </summary>
    private static string BuildRagQuery(EvalCaseV3 @case)
    {
        var query = @case.Description.Trim();
        var titles = string.Join(" | ", @case.Evidence
            .Select(e => e.Title)
            .Where(t => !string.IsNullOrWhiteSpace(t)));
        return string.IsNullOrEmpty(titles) ? query : $"{query} | {titles}";
    }
}

/// <summary>
/// One row of a V3 eval run. Mirrors V1's RegressionRow but adds RAG
/// bookkeeping (was retrieval used, how many logs in / kept).
/// </summary>
public sealed record V3RegressionRow(
    string ConfigId,
    string CaseId,
    bool Passed,
    string? Error,
    int InputTokens,
    int OutputTokens,
    TimeSpan WallTime,
    bool RagUsed,
    int RagFromCount,
    int RagKeptCount,
    GradeResult? Grade);

/// <summary>
/// One named configuration to run all cases against. Lets you A/B different
/// models or RAG settings cleanly — the leaderboard shows pass-rate per
/// config across the case set.
/// </summary>
public sealed record V3RegressionConfig(
    string Id,
    string AnalyzerModel,
    bool RagEnabled,
    int RagThreshold,
    int RagTopK)
{
    /// <summary>
    /// Default config: gpt-4o-mini analyzer, RAG enabled with threshold 100
    /// and topK 25. Mirrors the V3 live endpoint defaults so eval results
    /// reflect production behaviour.
    /// </summary>
    public static V3RegressionConfig Baseline { get; } = new(
        Id: "baseline",
        AnalyzerModel: "gpt-4o-mini",
        RagEnabled: true,
        RagThreshold: 100,
        RagTopK: 25);

    /// <summary>
    /// No-RAG comparison — same model, but every log goes to the analyzer.
    /// Useful for measuring 'is RAG actually helping?' on a fixed case set.
    /// </summary>
    public static V3RegressionConfig NoRag { get; } = new(
        Id: "no-rag",
        AnalyzerModel: "gpt-4o-mini",
        RagEnabled: false,
        RagThreshold: int.MaxValue,
        RagTopK: int.MaxValue);
}
