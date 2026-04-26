using DistributedDebugger.Web.V3;

namespace DistributedDebugger.Eval.V3;

/// <summary>
/// A V3 eval case — bug description + a fixed log set + optional evidence +
/// ground truth. The runner feeds this to <c>LogAnalyzer.AnalyzeAsync</c>
/// directly (skips the HTTP layer; we're testing the analyzer, not the
/// network) and the existing <see cref="LlmAsJudgeGrader"/> grades the
/// resulting analysis against the truth fields.
///
/// Why this is a different shape from V1's <c>EvalCase</c>: V1's case
/// scripts a sequence of agent tool calls (search_logs returns X, then
/// request_mongo returns Y). V3 has no agent and no tools — it's a single
/// LLM call over a fixed input. The case just IS the input plus the
/// expected conclusion.
///
/// YAML round-trip happens via <see cref="V3CaseLoader"/>.
/// </summary>
public sealed record EvalCaseV3(
    string Id,
    string Description,
    string? TicketId,
    IReadOnlyList<LogRecord> Logs,
    IReadOnlyList<EvidenceItem> Evidence,
    GroundTruth Truth);

/// <summary>
/// One evidence item within an eval case. Mirrors <c>V3Endpoints.EvidenceItem</c>
/// but lives here so the YAML schema is stable independent of API drift in
/// the Web project.
/// </summary>
public sealed record EvidenceItem(
    string Kind,
    string Title,
    string? Command,
    string Content);
