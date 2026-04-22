namespace DistributedDebugger.Core.Tools;

/// <summary>
/// Bridges the agent's tools and whoever runs the CLI. When the agent wants to
/// inspect MongoDB/OpenSearch/Kafka state, the tool formulates a structured
/// query, then asks the human (via this interface) to run it and paste back
/// the result.
///
/// This indirection matters because:
///
///   - The tools live in DistributedDebugger.Tools and must not depend on the
///     CLI project (that would invert the dependency graph).
///   - Different frontends want different UX: a CLI wants stdin prompts, a
///     future web UI wants a form. The interface is the seam.
///   - Unit tests can inject a fake provider that auto-answers with fixtures.
/// </summary>
public interface IHumanDataProvider
{
    /// <summary>
    /// Present a query request to the user and return their pasted response.
    ///
    /// Return value semantics:
    ///   - non-null, non-empty string → the data the user provided
    ///   - null → user skipped ("skip") — tool should tell the agent it was declined
    ///
    /// The implementation is responsible for rendering the prompt nicely and
    /// handling the multi-line paste flow.
    /// </summary>
    Task<string?> RequestDataAsync(HumanDataRequest request, CancellationToken ct);
}

/// <summary>
/// Everything the user needs to see to decide whether to run the query.
/// Kept as a record so each tool can fill it in declaratively without worrying
/// about rendering.
/// </summary>
public sealed record HumanDataRequest(
    string SourceName,        // "MongoDB", "OpenSearch", "Kafka"
    string RenderedQuery,     // the exact query in human-readable form
    string Reason,            // why the agent wants this data
    string? SuggestedEnv      // test / staging / live — agent's suggestion, user can override
);
