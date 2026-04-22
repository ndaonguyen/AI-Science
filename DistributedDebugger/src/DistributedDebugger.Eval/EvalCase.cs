namespace DistributedDebugger.Eval;

/// <summary>
/// A replayable bug investigation case. Think of it as a golden test for an
/// investigation agent — you captured a real bug once, now you can re-run the
/// agent against it anytime to measure whether prompt/model/tool changes helped
/// or hurt.
///
/// The pre-fetched data is the crucial piece. In live investigation the agent
/// calls request_mongo_query / CloudWatch / Kafka UI, which depends on humans
/// and live systems — neither of which is reproducible. For eval runs, we
/// substitute those tools with scripted versions that return pre-recorded
/// data keyed by what the agent asks for. That way a regression run is
/// deterministic: same inputs, same data available, only the agent's behaviour
/// varies.
/// </summary>
public sealed record EvalCase(
    string Id,
    string Description,
    string? TicketId,
    GroundTruth Truth,
    IReadOnlyList<ScriptedLogResponse> LogResponses,
    IReadOnlyList<ScriptedDataResponse> DataResponses
);

/// <summary>
/// What the investigation SHOULD conclude. Used by the grader to judge the
/// agent's output. Structured instead of free-text so the LLM judge has
/// concrete points to check off, rather than a vague "does this sound right."
/// </summary>
public sealed record GroundTruth(
    string ExpectedCause,                        // free text: the actual root cause
    IReadOnlyList<string> ExpectedServices,      // which services were involved
    IReadOnlyList<string> MustMentionKeywords,   // agent's report must cite these
    IReadOnlyList<string> MustNotClaim,          // common false positives to watch for
    string MinConfidence = "Medium"              // Low / Medium / High
);

/// <summary>
/// A scripted CloudWatch/log response. At replay time, if the agent's
/// search_logs call matches any of these (by service + keyword substring),
/// we return the pre-recorded log content instead of hitting AWS.
///
/// Match semantics: service must match exactly; keyword may be any substring
/// of the agent's actual query. This is deliberately loose — if the agent
/// searches for "timeout" and we have a script for "OpenSearch timeout",
/// we still match. Stricter matching makes cases brittle to innocuous
/// rewording.
/// </summary>
public sealed record ScriptedLogResponse(
    string Service,
    string MatchesKeyword,
    string Logs
);

/// <summary>
/// A scripted response for one of the request_* tools (Mongo/OpenSearch/Kafka).
/// Matched by tool name + a discriminator drawn from the tool's input —
/// typically a collection/index/topic name plus an entity id.
///
/// Keep this loose: we match if MatchesAny is a substring of the serialised
/// tool input. Not bulletproof, but good enough for a regression suite and
/// avoids forcing case authors to write exact JSON predicates.
/// </summary>
public sealed record ScriptedDataResponse(
    string ToolName,
    string MatchesAny,
    string Response
);
