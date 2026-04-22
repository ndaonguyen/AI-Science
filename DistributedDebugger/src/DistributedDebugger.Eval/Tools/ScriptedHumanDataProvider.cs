using System.Text.Json;
using DistributedDebugger.Core.Tools;

namespace DistributedDebugger.Eval.Tools;

/// <summary>
/// Eval-time replacement for ConsoleHumanDataProvider. Instead of prompting
/// a human, matches the agent's request against a bank of
/// <see cref="ScriptedDataResponse"/> and returns the scripted payload.
///
/// Match rule: the <see cref="ScriptedDataResponse.MatchesAny"/> string must
/// be a substring of the serialised request (tool name + rendered query +
/// reason). This lets case authors pin on whatever makes their case distinctive
/// — an entity id like "act-789", a topic name, a field like "isEpContent".
///
/// If nothing matches, we return null — which the real request_* tools
/// interpret as "engineer declined." That's the closest equivalent to "we
/// don't have this data" and preserves the agent's decline-handling path.
///
/// Note: this provider doesn't know which tool called it, so it receives
/// the request AS ALREADY RENDERED BY THE TOOL (mongosh string, OpenSearch
/// POST body, etc.). We tag the match against <see cref="HumanDataRequest.SourceName"/>
/// too when it's present, so a case can say "for MongoDB, matching 'act-789',
/// return X" and not accidentally match an OpenSearch query.
/// </summary>
public sealed class ScriptedHumanDataProvider : IHumanDataProvider
{
    private readonly IReadOnlyList<ScriptedDataResponse> _responses;

    public ScriptedHumanDataProvider(IReadOnlyList<ScriptedDataResponse> responses)
    {
        _responses = responses;
    }

    public Task<string?> RequestDataAsync(HumanDataRequest request, CancellationToken ct)
    {
        // Build a haystack that combines everything distinctive about the call.
        // Lowercased once for cheap case-insensitive substring checks.
        var haystack = (
            request.SourceName + "\n" +
            request.RenderedQuery + "\n" +
            request.Reason
        ).ToLowerInvariant();

        // Tool-name hints map the agent's source (MongoDB / OpenSearch / Kafka)
        // to the ToolName field on the scripted response. Case authors wrote
        // ToolName = "request_mongo_query" etc., so we need to translate.
        var sourceToToolName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MongoDB"]    = "request_mongo_query",
            ["OpenSearch"] = "request_opensearch_query",
            ["Kafka"]      = "request_kafka_events",
        };

        var expectedToolName = sourceToToolName.TryGetValue(request.SourceName, out var tn) ? tn : null;

        var match = _responses.FirstOrDefault(r =>
            (expectedToolName is null
                || string.Equals(r.ToolName, expectedToolName, StringComparison.OrdinalIgnoreCase))
            && !string.IsNullOrWhiteSpace(r.MatchesAny)
            && haystack.Contains(r.MatchesAny.ToLowerInvariant()));

        if (match is null)
        {
            // null = "engineer declined" semantics in the real provider.
            return Task.FromResult<string?>(null);
        }

        return Task.FromResult<string?>(match.Response);
    }
}
