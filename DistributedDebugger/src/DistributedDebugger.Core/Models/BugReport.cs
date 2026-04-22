using System.Text.Json;

namespace DistributedDebugger.Core.Models;

/// <summary>
/// Input to an investigation. Two ways in:
///
///   1. Freeform description — the user pastes what they know about the bug.
///      This is Phase 1's primary mode. Minimal assumptions about format.
///   2. Jira ticket ID + pre-fetched ticket text — for when you've already
///      pulled the ticket from Atlassian MCP and want to feed it in.
///
/// Both paths end up in <see cref="Description"/>, which is what the agent
/// actually reasons about. TicketId/TicketSource are preserved for reporting.
/// </summary>
public sealed record BugReport(
    string Description,
    string? TicketId = null,
    string? TicketSource = null,
    DateTimeOffset? ReportedAt = null
);

/// <summary>
/// A single observable step during an investigation. The trace is the audit log
/// of what the agent did — exactly like HarnessArena's trace events but specialised
/// for debugging. Polymorphic so the report renderer can distinguish evidence
/// types at output time.
/// </summary>
public abstract record InvestigationEvent(DateTimeOffset At, int Iteration);

public sealed record ModelCallEvent(
    DateTimeOffset At, int Iteration, int PromptMessageCount
) : InvestigationEvent(At, Iteration);

public sealed record ModelResponseEvent(
    DateTimeOffset At, int Iteration, string? Text,
    int OutputTokens, string StopReason
) : InvestigationEvent(At, Iteration);

public sealed record ToolCallEvent(
    DateTimeOffset At, int Iteration, string ToolUseId,
    string ToolName, JsonElement Input
) : InvestigationEvent(At, Iteration);

public sealed record ToolResultEvent(
    DateTimeOffset At, int Iteration, string ToolUseId,
    string Output, bool IsError
) : InvestigationEvent(At, Iteration);

public sealed record HypothesisEvent(
    DateTimeOffset At, int Iteration, string Hypothesis, string Reasoning
) : InvestigationEvent(At, Iteration);

public sealed record ErrorEvent(
    DateTimeOffset At, int Iteration, string Message, string? Exception
) : InvestigationEvent(At, Iteration);

public enum InvestigationStatus
{
    Running,
    Completed,
    MaxIterationsHit,
    Error
}

public sealed record InvestigationUsage(
    int InputTokens,
    int OutputTokens,
    int Iterations,
    TimeSpan WallTime
);

/// <summary>
/// The final structured output of an investigation. This is what gets rendered
/// to the user as a markdown report and persisted as a trace JSON file.
/// </summary>
public sealed record Investigation(
    Guid Id,
    BugReport Report,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    InvestigationStatus Status,
    RootCauseReport? RootCause,
    IReadOnlyList<InvestigationEvent> Trace,
    InvestigationUsage Usage
);

/// <summary>
/// The agent's final conclusion. Kept deliberately narrow — a few text fields the
/// model can fill in via a finish-style tool. Evidence items are free-form for
/// Phase 1; later phases will make them structured (service, timestamp, source URL).
/// </summary>
public sealed record RootCauseReport(
    string Summary,
    string LikelyCause,
    IReadOnlyList<string> AffectedServices,
    IReadOnlyList<string> Evidence,
    string? SuggestedFix,
    ConfidenceLevel Confidence
);

public enum ConfidenceLevel
{
    Low,
    Medium,
    High
}
