using System.Text;
using DistributedDebugger.Core.Models;
using DistributedDebugger.Eval.Internal;
using OpenAI.Chat;

namespace DistributedDebugger.Eval;

/// <summary>
/// LLM-as-judge grader. Given a completed investigation and the case's
/// <see cref="GroundTruth"/>, asks a separate model call to judge:
///
///   - cause correctness   (did the agent identify the real root cause?)
///   - service coverage    (did the agent name the services that were actually involved?)
///   - keyword coverage    (did the report mention the required keywords?)
///   - absence of common false positives (didn't claim things marked as must-not-claim)
///   - confidence match    (did the agent hit the minimum confidence bar?)
///
/// The output is structured (PassFail + subscores + rationale), so a regression
/// leaderboard can compute pass rate across a suite of cases without further parsing.
///
/// A few deliberate design choices:
///
///   - We use a DIFFERENT (and usually more capable) model for judging than for
///     the investigation. Using the same model risks self-preferencing bias.
///   - Temperature 0 and a structured JSON schema — graders must be boring and
///     reproducible. If you can't reproduce grading, you can't measure changes.
///   - Keyword checks are done deterministically FIRST (no AI cost), and their
///     result is fed into the LLM's prompt as evidence. Saves tokens and keeps
///     the judge honest about fact-level presence/absence.
/// </summary>
public sealed class LlmAsJudgeGrader
{
    private readonly ChatClient _judge;

    public LlmAsJudgeGrader(string openAiApiKey, string judgeModel = "gpt-4o")
    {
        _judge = new ChatClient(judgeModel, openAiApiKey);
    }

    public async Task<GradeResult> GradeAsync(
        EvalCase @case,
        Investigation investigation,
        CancellationToken ct)
    {
        // Deterministic pre-checks. Compute these first so the judge sees the
        // results as evidence rather than having to guess.
        var reportText = BuildReportText(investigation);

        var keywordHits = @case.Truth.MustMentionKeywords
            .Select(k => new KeywordCheck(k, reportText.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var badClaims = @case.Truth.MustNotClaim
            .Select(k => new KeywordCheck(k, reportText.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // Build the judge prompt. Kept concise: the judge doesn't need to
        // re-investigate, just score against concrete criteria.
        var prompt = BuildJudgePrompt(@case, investigation, keywordHits, badClaims);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(
                "You are an evaluator grading bug-investigation reports. " +
                "Respond with a JSON object matching the schema in the user message. " +
                "Be strict but fair. Reward correct root cause identification; " +
                "penalise invented evidence, wrong services, or false claims. " +
                "Return ONLY JSON, no prose before or after."),
            new UserChatMessage(prompt),
        };

        var options = new ChatCompletionOptions
        {
            Temperature = 0f,
        };

        var response = await _judge.CompleteChatAsync(messages, options, ct);
        var jsonText = response.Value.Content.Count > 0
            ? response.Value.Content[0].Text ?? "{}"
            : "{}";

        var parsed = JudgeResponseParser.Parse(jsonText);

        // Final pass/fail rule: it's a PASS only if the judge says the cause
        // is correct AND all must-mention keywords were hit AND no must-not-claim
        // showed up. The keyword checks are deterministic so they can't be
        // wiggled by the judge model.
        var allKeywordsHit = keywordHits.All(k => k.Present);
        var noBadClaims = badClaims.All(k => !k.Present);
        var passed = parsed.CauseCorrect && allKeywordsHit && noBadClaims;

        return new GradeResult(
            Passed: passed,
            CaseId: @case.Id,
            CauseCorrect: parsed.CauseCorrect,
            ServiceCoverageScore: parsed.ServiceCoverageScore,
            ConfidenceAppropriate: parsed.ConfidenceAppropriate,
            KeywordHits: keywordHits,
            BadClaims: badClaims,
            JudgeRationale: parsed.Rationale,
            JudgeTokens: (response.Value.Usage?.InputTokenCount ?? 0)
                       + (response.Value.Usage?.OutputTokenCount ?? 0));
    }

    private static string BuildReportText(Investigation inv)
    {
        var rc = inv.RootCause;
        if (rc is null) return "(no root cause produced)";

        var sb = new StringBuilder();
        sb.AppendLine($"Summary: {rc.Summary}");
        sb.AppendLine($"Likely cause: {rc.LikelyCause}");
        sb.AppendLine($"Confidence: {rc.Confidence}");
        sb.AppendLine("Affected services: " + string.Join(", ", rc.AffectedServices));
        sb.AppendLine("Evidence:");
        foreach (var e in rc.Evidence) sb.AppendLine($"  - {e}");
        if (!string.IsNullOrWhiteSpace(rc.SuggestedFix))
        {
            sb.AppendLine($"Suggested fix: {rc.SuggestedFix}");
        }
        return sb.ToString();
    }

    private static string BuildJudgePrompt(
        EvalCase @case,
        Investigation inv,
        IReadOnlyList<KeywordCheck> keywordHits,
        IReadOnlyList<KeywordCheck> badClaims)
    {
        var sb = new StringBuilder();

        sb.AppendLine("## Case");
        sb.AppendLine($"Id: {@case.Id}");
        sb.AppendLine($"Description: {@case.Description}");
        sb.AppendLine();
        sb.AppendLine("## Ground truth");
        sb.AppendLine($"Expected cause: {@case.Truth.ExpectedCause}");
        sb.AppendLine($"Expected services: {string.Join(", ", @case.Truth.ExpectedServices)}");
        sb.AppendLine($"Minimum confidence: {@case.Truth.MinConfidence}");
        sb.AppendLine();
        sb.AppendLine("## Agent's report");
        sb.AppendLine(BuildReportText(inv));
        sb.AppendLine();
        sb.AppendLine("## Deterministic checks (already computed)");
        sb.AppendLine("Keywords that MUST appear (true = present):");
        foreach (var h in keywordHits)
        {
            sb.AppendLine($"  - \"{h.Keyword}\": {h.Present}");
        }
        sb.AppendLine("Phrases that MUST NOT be claimed (true = present, which is BAD):");
        foreach (var h in badClaims)
        {
            sb.AppendLine($"  - \"{h.Keyword}\": {h.Present}");
        }
        sb.AppendLine();
        sb.AppendLine("## Required JSON output");
        sb.AppendLine(
            """
            {
              "causeCorrect":         boolean,  // does the agent's cause match the ground truth?
              "serviceCoverageScore": number,   // 0.0-1.0, fraction of expected services correctly named
              "confidenceAppropriate": boolean, // agent's confidence is >= case minConfidence AND not wildly overconfident when wrong
              "rationale":            string    // one short paragraph explaining the scores
            }
            """);

        return sb.ToString();
    }
}

public sealed record KeywordCheck(string Keyword, bool Present);

public sealed record GradeResult(
    bool Passed,
    string CaseId,
    bool CauseCorrect,
    double ServiceCoverageScore,
    bool ConfidenceAppropriate,
    IReadOnlyList<KeywordCheck> KeywordHits,
    IReadOnlyList<KeywordCheck> BadClaims,
    string JudgeRationale,
    int JudgeTokens);
