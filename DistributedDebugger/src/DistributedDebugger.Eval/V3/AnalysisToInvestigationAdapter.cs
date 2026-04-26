using DistributedDebugger.Core.Models;
using DistributedDebugger.Web.V3;

namespace DistributedDebugger.Eval.V3;

/// <summary>
/// Adapts V3's <see cref="AnalysisResult"/> into the V1 shapes
/// (<see cref="Investigation"/>, <see cref="EvalCase"/>) that
/// <see cref="LlmAsJudgeGrader"/> already understands. Lets us reuse the
/// grader unchanged instead of forking it.
///
/// The mapping has a few approximations:
///
///   - V3 has no per-step trace (one LLM call, not an agent loop) so the
///     Investigation's Trace is empty. The grader's BuildReportText doesn't
///     read Trace, so this is fine for grading purposes.
///   - V3 has no Confidence concept — the analyzer returns hypothesis text
///     but not a Low/Medium/High enum. We set Confidence = Medium as a
///     neutral default. This makes the grader's MinConfidence check soft;
///     if you really want strict confidence checking, set MinConfidence = Low
///     in your case truth section.
///   - AffectedServices isn't returned by the analyzer; we extract it from
///     the input logs (deduped). Most CoCo cases only involve 1-2 services
///     so this approximation is usually right.
/// </summary>
public static class AnalysisToInvestigationAdapter
{
    public static Investigation ToInvestigation(
        EvalCaseV3 @case,
        AnalysisResult result,
        DateTimeOffset startedAt,
        DateTimeOffset finishedAt)
    {
        var affectedServices = @case.Logs
            .Select(l => l.Service)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Collapse the analyzer's followup suggestions into a single string —
        // RootCauseReport.SuggestedFix is a single line by V1's convention.
        // We join with '; ' so a grader keyword check still finds individual
        // suggestions if they're listed in MustMention.
        var suggestedFix = result.SuggestedFollowups.Count > 0
            ? string.Join("; ", result.SuggestedFollowups)
            : null;

        var rootCause = new RootCauseReport(
            Summary: result.Summary,
            LikelyCause: result.Hypothesis,
            AffectedServices: affectedServices,
            Evidence: result.Suspicious,
            SuggestedFix: suggestedFix,
            Confidence: ConfidenceLevel.Medium);

        var report = new BugReport(
            Description: @case.Description,
            TicketId: @case.TicketId,
            TicketSource: @case.TicketId is null ? null : "Jira",
            ReportedAt: startedAt);

        return new Investigation(
            Id: Guid.NewGuid(),
            Report: report,
            StartedAt: startedAt,
            FinishedAt: finishedAt,
            Status: InvestigationStatus.Completed,
            RootCause: rootCause,
            Trace: Array.Empty<InvestigationEvent>(),
            Usage: new InvestigationUsage(
                InputTokens: result.InputTokens,
                OutputTokens: result.OutputTokens,
                Iterations: 1,
                WallTime: finishedAt - startedAt));
    }

    /// <summary>
    /// Adapt the V3 case into a V1 EvalCase for the grader's
    /// case-aware prompt building. The scripted-response lists are empty
    /// because V3 cases don't replay tools — those fields exist for V1's
    /// agent loop and aren't read by the grader anyway.
    /// </summary>
    public static EvalCase ToEvalCase(EvalCaseV3 v3) =>
        new(
            Id: v3.Id,
            Description: v3.Description,
            TicketId: v3.TicketId,
            Truth: v3.Truth,
            LogResponses: Array.Empty<ScriptedLogResponse>(),
            DataResponses: Array.Empty<ScriptedDataResponse>());
}
