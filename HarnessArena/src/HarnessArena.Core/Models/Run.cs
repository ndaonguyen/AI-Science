using System.Text.Json.Serialization;

namespace HarnessArena.Core.Models;

/// <summary>
/// A single evaluation task. Definitions are loaded from YAML files under tasks/.
/// </summary>
public sealed record TaskDefinition(
    string Id,
    string Domain,
    string Prompt,
    string ExpectedAnswer,
    int Difficulty = 1,
    int MaxIterations = 10,
    Dictionary<string, string>? Metadata = null
);

/// <summary>
/// An agent configuration — one "contestant" in the arena. You evaluate the same
/// task suite against multiple configs to see which prompt/tool/model combo wins.
/// </summary>
public sealed record AgentConfig(
    string Id,
    string Model,
    string SystemPrompt,
    IReadOnlyList<string> ToolNames,
    int MaxIterations = 10,
    double Temperature = 0.0
);

public enum RunStatus
{
    Running,
    Completed,
    MaxIterationsHit,
    Error
}

public sealed record RunUsage(
    int InputTokens,
    int OutputTokens,
    int Iterations,
    TimeSpan WallTime
);

/// <summary>
/// The full record of one (task, config) execution. Trace captures every observable
/// step so you can replay, debug, and grade.
/// </summary>
public sealed record Run(
    Guid Id,
    string TaskId,
    string AgentConfigId,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    RunStatus Status,
    string? FinalAnswer,
    IReadOnlyList<TraceEvent> Trace,
    RunUsage Usage
);
