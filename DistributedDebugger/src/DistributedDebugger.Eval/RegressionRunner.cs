using System.Threading.Channels;
using DistributedDebugger.Agent;
using DistributedDebugger.Core.Models;
using DistributedDebugger.Core.Tools;
using DistributedDebugger.Eval.Tools;
using DistributedDebugger.Tools;
using DistributedDebugger.Tools.HumanLoop;

namespace DistributedDebugger.Eval;

/// <summary>
/// Runs the cross-product of eval cases × agent configs, grades each run,
/// produces summary rows. The HarnessArena pattern, narrowed to bug
/// investigation.
///
/// Sequential by design. We could parallelise across cases, but bug
/// investigations are cheap enough (~$0.001 each) that the extra complexity
/// — rate-limit juggling, ordered output, error handling — isn't worth it
/// at this scale. If we ever hit 1000+ cases, revisit.
/// </summary>
public sealed class RegressionRunner
{
    private readonly LlmAsJudgeGrader _grader;
    private readonly string _openAiKey;

    public RegressionRunner(LlmAsJudgeGrader grader, string openAiKey)
    {
        _grader = grader;
        _openAiKey = openAiKey;
    }

    public async Task<IReadOnlyList<RegressionRow>> RunAsync(
        IReadOnlyList<EvalCase> cases,
        IReadOnlyList<NamedConfig> configs,
        Action<RegressionRow>? onRowCompleted = null,
        CancellationToken ct = default)
    {
        var rows = new List<RegressionRow>(cases.Count * configs.Count);

        foreach (var cfg in configs)
        {
            foreach (var @case in cases)
            {
                ct.ThrowIfCancellationRequested();

                RegressionRow row;
                try
                {
                    row = await RunOneAsync(@case, cfg, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // A thrown investigation shouldn't kill the whole suite —
                    // record it as a failure and carry on. This is a harness,
                    // not a pipeline.
                    row = new RegressionRow(
                        ConfigName: cfg.Name,
                        CaseId: @case.Id,
                        Passed: false,
                        CauseCorrect: false,
                        ServiceCoverage: 0,
                        Iterations: 0,
                        InputTokens: 0,
                        OutputTokens: 0,
                        JudgeTokens: 0,
                        Duration: TimeSpan.Zero,
                        Notes: $"EXCEPTION: {ex.Message}");
                }

                rows.Add(row);
                onRowCompleted?.Invoke(row);
            }
        }

        return rows;
    }

    private async Task<RegressionRow> RunOneAsync(
        EvalCase @case,
        NamedConfig cfg,
        CancellationToken ct)
    {
        // Build a scripted tool registry: real tool surface area (same Names
        // and schemas as production), scripted data underneath.
        var hypothesisChannel = Channel.CreateUnbounded<(string, string)>();
        var scriptedProvider = new ScriptedHumanDataProvider(@case.DataResponses);

        var registry = new ToolRegistry(new IDebugTool[]
        {
            new ScriptedLogTool(@case.LogResponses),
            new RequestMongoQueryTool(scriptedProvider),
            new RequestOpenSearchQueryTool(scriptedProvider),
            new RequestKafkaEventsTool(scriptedProvider),
            new RecordHypothesisTool(hypothesisChannel),
            new FinishInvestigationTool(),
        });

        var agent = new InvestigatorAgent(registry, _openAiKey);

        var report = new BugReport(
            Description: @case.Description,
            TicketId: @case.TicketId,
            TicketSource: @case.TicketId is null ? null : "eval",
            ReportedAt: DateTimeOffset.UtcNow);

        var start = DateTimeOffset.UtcNow;
        var investigation = await agent.InvestigateAsync(
            report,
            config: cfg.Config,
            hypothesisChannel: hypothesisChannel,
            onEvent: null,
            ct: ct);
        var duration = DateTimeOffset.UtcNow - start;

        var grade = await _grader.GradeAsync(@case, investigation, ct);

        return new RegressionRow(
            ConfigName: cfg.Name,
            CaseId: @case.Id,
            Passed: grade.Passed,
            CauseCorrect: grade.CauseCorrect,
            ServiceCoverage: grade.ServiceCoverageScore,
            Iterations: investigation.Usage.Iterations,
            InputTokens: investigation.Usage.InputTokens,
            OutputTokens: investigation.Usage.OutputTokens,
            JudgeTokens: grade.JudgeTokens,
            Duration: duration,
            Notes: investigation.Status == InvestigationStatus.Completed
                ? grade.JudgeRationale
                : $"{investigation.Status}: {grade.JudgeRationale}");
    }
}

/// <summary>
/// A named agent configuration to run in a regression suite. Lets you compare
/// e.g. "baseline" vs "lower-iterations" vs "stronger-model" side by side.
/// </summary>
public sealed record NamedConfig(string Name, AgentConfig Config);

/// <summary>
/// One (config, case) row in the regression output. Small and copy-friendly
/// so it can be emitted to a terminal table, CSV, or dashboard unchanged.
/// </summary>
public sealed record RegressionRow(
    string ConfigName,
    string CaseId,
    bool Passed,
    bool CauseCorrect,
    double ServiceCoverage,
    int Iterations,
    int InputTokens,
    int OutputTokens,
    int JudgeTokens,
    TimeSpan Duration,
    string Notes);
