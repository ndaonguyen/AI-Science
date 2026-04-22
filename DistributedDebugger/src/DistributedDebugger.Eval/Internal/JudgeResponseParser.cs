using System.Text.Json;

namespace DistributedDebugger.Eval.Internal;

/// <summary>
/// Tolerant parser for the JSON object the LLM judge produces. Kept as a
/// separate internal class so the logic (stripping markdown fences, extracting
/// the first brace-enclosed block, defaulting missing fields) can be tested
/// without a network call to OpenAI.
///
/// Why not just use JsonDocument.Parse directly: the judge model doesn't always
/// respect "JSON only" instructions. It sometimes wraps in ```json``` fences,
/// sometimes adds a prose preamble like "Here's my evaluation:". We strip all
/// of that before parsing so the grader survives normal model drift.
/// </summary>
internal static class JudgeResponseParser
{
    internal sealed record Result(
        bool CauseCorrect,
        double ServiceCoverageScore,
        bool ConfidenceAppropriate,
        string Rationale,
        bool ParseFailed);

    internal static Result Parse(string raw)
    {
        // Strip a leading ```json / ``` fence and any trailing ``` if present.
        // Also pull out the first { ... } block if the judge wrote preamble.
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("```"))
        {
            var nl = trimmed.IndexOf('\n');
            if (nl > 0) trimmed = trimmed[(nl + 1)..];
            if (trimmed.EndsWith("```")) trimmed = trimmed[..^3];
        }

        var firstBrace = trimmed.IndexOf('{');
        var lastBrace = trimmed.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            trimmed = trimmed[firstBrace..(lastBrace + 1)];
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            return new Result(
                CauseCorrect: root.TryGetProperty("causeCorrect", out var c)
                              && c.ValueKind == JsonValueKind.True,
                ServiceCoverageScore: root.TryGetProperty("serviceCoverageScore", out var s)
                              && s.ValueKind == JsonValueKind.Number
                    ? s.GetDouble() : 0.0,
                ConfidenceAppropriate: root.TryGetProperty("confidenceAppropriate", out var ca)
                              && ca.ValueKind == JsonValueKind.True,
                Rationale: root.TryGetProperty("rationale", out var r) ? r.GetString() ?? "" : "",
                ParseFailed: false);
        }
        catch
        {
            // Malformed JSON → treat as failure. Caller surfaces this as
            // "Judge returned malformed JSON" rather than falsely passing the run.
            return new Result(false, 0, false, "Judge returned malformed JSON.", ParseFailed: true);
        }
    }
}
