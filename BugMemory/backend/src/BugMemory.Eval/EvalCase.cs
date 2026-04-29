namespace BugMemory.Eval;

/// <summary>
/// One harness case = one question with known good behaviour.
///
/// The case shape is split into "what the user asks" (Question), "what the
/// retrieval should surface" (ExpectedBugIds), and "what makes a good
/// answer" (AnswerCriteria, plain prose used by the LLM-as-judge).
///
/// We deliberately do NOT pin an exact expected answer string. RAG output
/// varies between runs even at temperature 0 (model updates, prompt-token
/// reordering, etc.), and any grader that compares exact strings would
/// produce noise. The criteria let us grade whether the answer ADDRESSED
/// the right things, not whether it matched a specific phrasing.
/// </summary>
public sealed class EvalCase
{
    /// <summary>Stable id, used in leaderboard rows and for filtering runs.</summary>
    public string Id { get; set; } = "";

    /// <summary>Free-form description of what this case is testing.</summary>
    public string? Description { get; set; }

    /// <summary>The user's question — fed to /api/ask verbatim.</summary>
    public string Question { get; set; } = "";

    /// <summary>
    /// Stable string ids of the bugs from the seed corpus that the
    /// retriever SHOULD return in top-K. These are the seed-bug ids
    /// (e.g. "kafka-retry-dup-key"), not Guids — Guids are generated
    /// fresh each run when the harness loads the seed.
    /// </summary>
    public List<string> ExpectedBugIds { get; set; } = new();

    /// <summary>
    /// Plain-prose criteria for what makes a good answer. The LLM-as-judge
    /// grader reads this and decides whether the actual answer met them.
    ///
    /// Example: "Names the idempotency-key fix as the resolution. Does not
    /// suggest unrelated retry strategies. Acknowledges the duplicate-key
    /// constraint as the root cause."
    /// </summary>
    public string AnswerCriteria { get; set; } = "";
}
