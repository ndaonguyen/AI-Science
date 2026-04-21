using HarnessArena.Agents;
using HarnessArena.Core.Grading;
using HarnessArena.Core.Models;

namespace HarnessArena.Runner;

/// <summary>
/// Executes the cross-product of tasks × configs, grades each run, and writes
/// traces to disk. Sequential for v0 — parallelism comes in v1 once we have
/// a handle on rate limits.
/// </summary>
public sealed class RunOrchestrator
{
    private readonly IAgent _agent;
    private readonly IGrader _grader;
    private readonly TraceWriter _writer;

    public RunOrchestrator(IAgent agent, IGrader grader, TraceWriter writer)
    {
        _agent = agent;
        _grader = grader;
        _writer = writer;
    }

    public async Task<IReadOnlyList<RunSummary>> RunSuiteAsync(
        IReadOnlyList<TaskDefinition> tasks,
        IReadOnlyList<AgentConfig> configs,
        CancellationToken ct,
        Action<RunSummary>? onRunCompleted = null)
    {
        var summaries = new List<RunSummary>(tasks.Count * configs.Count);

        foreach (var config in configs)
        {
            foreach (var task in tasks)
            {
                ct.ThrowIfCancellationRequested();

                Run run;
                GradeResult? grade = null;
                try
                {
                    run = await _agent.RunAsync(task, config, ct);
                    grade = await _grader.GradeAsync(task, run, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Record a failed run so the summary stays meaningful.
                    run = new Run(
                        Id: Guid.NewGuid(),
                        TaskId: task.Id,
                        AgentConfigId: config.Id,
                        StartedAt: DateTimeOffset.UtcNow,
                        FinishedAt: DateTimeOffset.UtcNow,
                        Status: RunStatus.Error,
                        FinalAnswer: null,
                        Trace: new[]
                        {
                            (TraceEvent)new ErrorEvent(
                                DateTimeOffset.UtcNow, 0, ex.Message, ex.ToString())
                        },
                        Usage: new RunUsage(0, 0, 0, TimeSpan.Zero));
                }

                var tracePath = await _writer.WriteAsync(run, grade, ct);
                var summary = new RunSummary(task, config, run, grade, tracePath);
                summaries.Add(summary);
                onRunCompleted?.Invoke(summary);
            }
        }

        return summaries;
    }
}

public sealed record RunSummary(
    TaskDefinition Task,
    AgentConfig Config,
    Run Run,
    GradeResult? Grade,
    string TracePath
);
