using System.Text.Json;
using System.Text.Json.Serialization;

namespace HarnessArena.Core.Models;

/// <summary>
/// Polymorphic base for any observable event during a run. Uses a "kind" discriminator
/// so traces round-trip cleanly through System.Text.Json.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ModelCallEvent), "model_call")]
[JsonDerivedType(typeof(ModelResponseEvent), "model_response")]
[JsonDerivedType(typeof(ToolCallEvent), "tool_call")]
[JsonDerivedType(typeof(ToolResultEvent), "tool_result")]
[JsonDerivedType(typeof(AgentFinishedEvent), "agent_finished")]
[JsonDerivedType(typeof(ErrorEvent), "error")]
public abstract record TraceEvent(DateTimeOffset At, int Iteration);

public sealed record ModelCallEvent(
    DateTimeOffset At,
    int Iteration,
    int PromptMessageCount
) : TraceEvent(At, Iteration);

public sealed record ModelResponseEvent(
    DateTimeOffset At,
    int Iteration,
    string? Text,
    int OutputTokens,
    string StopReason
) : TraceEvent(At, Iteration);

public sealed record ToolCallEvent(
    DateTimeOffset At,
    int Iteration,
    string ToolUseId,
    string Name,
    JsonElement Input
) : TraceEvent(At, Iteration);

public sealed record ToolResultEvent(
    DateTimeOffset At,
    int Iteration,
    string ToolUseId,
    string Output,
    bool IsError
) : TraceEvent(At, Iteration);

public sealed record AgentFinishedEvent(
    DateTimeOffset At,
    int Iteration,
    string Answer
) : TraceEvent(At, Iteration);

public sealed record ErrorEvent(
    DateTimeOffset At,
    int Iteration,
    string Message,
    string? Exception
) : TraceEvent(At, Iteration);
