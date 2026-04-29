namespace BugMemory.Eval;

/// <summary>
/// One named configuration to run all cases against. Lets you A/B
/// retrieval and generation parameters cleanly: the leaderboard prints
/// retrieval-precision and answer-pass-rate per config across the
/// case set, so you can see whether 'topk-3 + gpt-4o-mini' beats
/// 'topk-5 + gpt-4o' on cost-per-pass.
///
/// Keep configs small — every config multiplies the run cost. Three is
/// usually plenty: a baseline, a smaller variant (cheaper), and one
/// hypothesis you want to test ('does temperature 0 help?').
/// </summary>
public sealed record EvalConfig(
    string Id,
    int TopK,
    string ChatModel,
    double Temperature)
{
    /// <summary>
    /// Mirrors the Application/Infrastructure defaults — what the live
    /// API actually does. The 'pass-rate' on this config tells you how
    /// the system performs in production.
    /// </summary>
    public static EvalConfig Baseline { get; } = new(
        Id: "baseline",
        TopK: 5,
        ChatModel: "gpt-4o",
        Temperature: 0.3);

    /// <summary>
    /// Cheaper model, same retrieval. Tells you whether you can downgrade
    /// the answerer without losing quality. 4o-mini is ~10x cheaper.
    /// </summary>
    public static EvalConfig CheapModel { get; } = new(
        Id: "cheap-model",
        TopK: 5,
        ChatModel: "gpt-4o-mini",
        Temperature: 0.3);

    /// <summary>
    /// Narrower retrieval. Tests the 'fewer better-targeted sources'
    /// hypothesis: too many sources can dilute the answer with weakly
    /// related material.
    /// </summary>
    public static EvalConfig NarrowRetrieval { get; } = new(
        Id: "narrow-retrieval",
        TopK: 3,
        ChatModel: "gpt-4o",
        Temperature: 0.3);
}
