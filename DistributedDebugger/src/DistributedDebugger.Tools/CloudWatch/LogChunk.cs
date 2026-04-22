namespace DistributedDebugger.Tools.CloudWatch;

/// <summary>
/// A single retrievable unit of log context. Might be one log line, or a small
/// block of consecutive lines — whichever the chunking strategy produces.
///
/// Carries enough metadata that the final prompt can cite the source:
/// service, timestamp, and original log group.
/// </summary>
public sealed record LogChunk(
    string Service,
    string LogGroup,
    DateTimeOffset Timestamp,
    string Text
)
{
    /// <summary>
    /// Rough token count — used by the top-K retriever to budget how much
    /// context it can fit. Uses the cheap "chars ÷ 4" heuristic which is
    /// good enough for log-ish text; precise tokenisation would cost more
    /// than it saves here.
    /// </summary>
    public int EstimatedTokens => Math.Max(1, Text.Length / 4);

    /// <summary>
    /// The format the retriever hands back to the model. Having a uniform
    /// rendering avoids the agent having to guess how to parse each chunk.
    /// </summary>
    public string Render() =>
        $"[{Timestamp:yyyy-MM-dd HH:mm:ss}Z] ({Service}) {Text}";
}
