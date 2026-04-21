using System.Text.Json;
using HarnessArena.Core.Models;
using HarnessArena.Core.Tools;

namespace HarnessArena.Agents;

/// <summary>
/// A deterministic, no-API-calls agent that simulates Claude's behaviour using
/// a scripted response per task ID. Useful for:
///
///   - Learning the harness end-to-end without spending tokens.
///   - Exercising the loop, grader, trace writer, and leaderboard in unit tests.
///   - Demoing the system when offline or without an API key.
///
/// Each "script" is a sequence of fake model turns. On each iteration, FakeAgent
/// pops the next turn and produces the same trace events a real agent would.
///
/// Scripts can mimic three kinds of agent behaviour:
///   - happy path: call calculator a few times, then call finish.
///   - wrong answer: call finish with the wrong value (exercises the grader).
///   - loop failure: never call finish (exercises MaxIterationsHit).
/// </summary>
public sealed class FakeAgent : IAgent
{
    private readonly IToolRegistry _tools;
    private readonly IReadOnlyDictionary<string, ScriptedRun> _scripts;

    public FakeAgent(IToolRegistry tools, IReadOnlyDictionary<string, ScriptedRun> scripts)
    {
        _tools = tools;
        _scripts = scripts;
    }

    public async Task<Run> RunAsync(
        TaskDefinition task,
        AgentConfig config,
        CancellationToken ct)
    {
        var runId = Guid.NewGuid();
        var started = DateTimeOffset.UtcNow;
        var trace = new List<TraceEvent>();
        var status = RunStatus.Running;
        string? finalAnswer = null;
        int iterationsUsed = 0;
        int totalIn = 0, totalOut = 0;

        if (!_scripts.TryGetValue(task.Id, out var script))
        {
            // No script defined — simulate the agent giving up.
            trace.Add(new ErrorEvent(
                DateTimeOffset.UtcNow, 0,
                $"FakeAgent has no script for task '{task.Id}'.",
                Exception: null));
            status = RunStatus.Error;
            var now = DateTimeOffset.UtcNow;
            return new Run(runId, task.Id, config.Id, started, now, status,
                FinalAnswer: null, trace, new RunUsage(0, 0, 0, now - started));
        }

        var maxIter = Math.Min(task.MaxIterations, config.MaxIterations);

        for (int iter = 0; iter < script.Turns.Count && iter < maxIter; iter++)
        {
            ct.ThrowIfCancellationRequested();
            iterationsUsed = iter + 1;
            var turn = script.Turns[iter];

            trace.Add(new ModelCallEvent(DateTimeOffset.UtcNow, iterationsUsed, iter + 1));

            // Simulate some token usage so the leaderboard has numbers to show.
            totalIn += turn.FakeInputTokens;
            totalOut += turn.FakeOutputTokens;

            trace.Add(new ModelResponseEvent(
                DateTimeOffset.UtcNow, iterationsUsed,
                Text: turn.Thinking,
                OutputTokens: turn.FakeOutputTokens,
                StopReason: turn.ToolCall is null ? "end_turn" : "tool_use"));

            // Simulate a small network delay so timestamps spread out.
            await Task.Delay(20, ct);

            if (turn.ToolCall is null)
            {
                // No tool call — agent is done talking (implicit finish).
                status = RunStatus.Completed;
                finalAnswer = turn.Thinking;
                break;
            }

            var call = turn.ToolCall;
            var inputJson = JsonDocument.Parse(call.InputJson).RootElement.Clone();
            var toolUseId = $"fake_{Guid.NewGuid():N}".Substring(0, 16);

            trace.Add(new ToolCallEvent(
                DateTimeOffset.UtcNow, iterationsUsed, toolUseId, call.ToolName, inputJson));

            if (call.ToolName == "finish")
            {
                finalAnswer = inputJson.GetProperty("answer").GetString();
                trace.Add(new AgentFinishedEvent(
                    DateTimeOffset.UtcNow, iterationsUsed, finalAnswer ?? ""));
                status = RunStatus.Completed;
                break;
            }

            // Actually execute the tool — this is why you still learn something
            // from the mock: CalculatorTool runs for real.
            var tool = _tools.Get(call.ToolName);
            var result = await tool.ExecuteAsync(inputJson, ct);
            trace.Add(new ToolResultEvent(
                DateTimeOffset.UtcNow, iterationsUsed, toolUseId, result.Output, result.IsError));
        }

        if (status == RunStatus.Running)
        {
            status = RunStatus.MaxIterationsHit;
        }

        var finished = DateTimeOffset.UtcNow;
        return new Run(
            Id: runId,
            TaskId: task.Id,
            AgentConfigId: config.Id,
            StartedAt: started,
            FinishedAt: finished,
            Status: status,
            FinalAnswer: finalAnswer,
            Trace: trace,
            Usage: new RunUsage(totalIn, totalOut, iterationsUsed, finished - started));
    }
}

/// <summary>
/// A scripted sequence of model turns for one task.
/// </summary>
public sealed record ScriptedRun(IReadOnlyList<ScriptedTurn> Turns);

public sealed record ScriptedTurn(
    string? Thinking,              // Text Claude "says" alongside the tool call.
    ScriptedToolCall? ToolCall,    // null = no tool call this turn (implicit finish).
    int FakeInputTokens = 200,
    int FakeOutputTokens = 50
);

public sealed record ScriptedToolCall(string ToolName, string InputJson);
