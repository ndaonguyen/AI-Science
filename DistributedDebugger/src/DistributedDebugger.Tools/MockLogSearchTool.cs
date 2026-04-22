using System.Text.Json;
using DistributedDebugger.Core.Tools;

namespace DistributedDebugger.Tools;

/// <summary>
/// A placeholder log search tool for Phase 1 — returns fixture data so the agent
/// can exercise the full investigation loop without real CloudWatch/Datadog
/// credentials. Replaced by DatadogTool + CloudWatchTool in Phase 2.
///
/// The fixtures are modelled after real-looking log lines so the agent's
/// reasoning over them is realistic. If the query matches no fixture, returns
/// empty (simulating no logs found in that time window) — this is itself useful
/// evidence, not an error.
/// </summary>
public sealed class MockLogSearchTool : IDebugTool
{
    private readonly IReadOnlyDictionary<string, string> _fixtures;

    public MockLogSearchTool(IReadOnlyDictionary<string, string>? fixtures = null)
    {
        // Default fixture bank. Each key is a service name; value is a block of
        // realistic-looking logs. The agent queries by service+keyword; we do a
        // naive substring filter to decide what to return.
        _fixtures = fixtures ?? DefaultFixtures;
    }

    public string Name => "search_logs";

    public string Description =>
        "Search service logs for a given service and keyword within a time window. " +
        "Returns matching log lines, or a 'no results' message if nothing was found. " +
        "Common services: content-media-service, authoring-service, content-search-service.";

    public JsonElement InputSchema { get; } = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "service": {
              "type": "string",
              "description": "Service name (e.g. 'content-media-service')."
            },
            "keyword": {
              "type": "string",
              "description": "Substring to match in log lines (e.g. 'timeout', 'OpenSearch', contentId)."
            },
            "timeWindow": {
              "type": "string",
              "description": "Optional time window hint in ISO-ish form, e.g. '2024-01-15T14:00/14:45'. Used to narrow results; may be ignored in the mock."
            }
          },
          "required": ["service", "keyword"]
        }
        """
    ).RootElement.Clone();

    public Task<ToolExecutionResult> ExecuteAsync(JsonElement input, CancellationToken ct)
    {
        var service = input.TryGetProperty("service", out var s) ? s.GetString() ?? "" : "";
        var keyword = input.TryGetProperty("keyword", out var k) ? k.GetString() ?? "" : "";

        if (string.IsNullOrWhiteSpace(service) || string.IsNullOrWhiteSpace(keyword))
        {
            return Task.FromResult(new ToolExecutionResult(
                "Error: both 'service' and 'keyword' are required.",
                IsError: true));
        }

        if (!_fixtures.TryGetValue(service, out var logs))
        {
            return Task.FromResult(new ToolExecutionResult(
                $"No log stream found for service '{service}'. Available: {string.Join(", ", _fixtures.Keys)}.",
                IsError: false));
        }

        var matchingLines = logs
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .Take(20) // cap output to avoid blowing up context
            .ToList();

        if (matchingLines.Count == 0)
        {
            return Task.FromResult(new ToolExecutionResult(
                $"No log lines in '{service}' matched keyword '{keyword}'. This is itself a data point — the expected log may not have been emitted.",
                IsError: false));
        }

        return Task.FromResult(new ToolExecutionResult(
            $"Found {matchingLines.Count} matching lines in {service}:\n" +
            string.Join("\n", matchingLines),
            IsError: false));
    }

    /// <summary>
    /// Default fixtures loosely modelled on a CoCo-style content indexing bug —
    /// Kafka event published, consumer fails to index into OpenSearch, no retry.
    /// The agent should be able to piece together the story from these.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> DefaultFixtures =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["authoring-service"] = """
                2024-01-15T14:27:01Z INFO  ContentPublished activityId=act-789 userId=u-42 ok=true
                2024-01-15T14:27:01Z INFO  Kafka publish succeeded topic=content.published partition=3 offset=99012
                2024-01-15T14:27:02Z INFO  HTTP 200 POST /activities/act-789/publish duration=420ms
                2024-01-15T14:27:10Z INFO  Health check ok
                """,

            ["content-media-service"] = """
                2024-01-15T14:27:03Z INFO  Kafka consumed topic=content.published activityId=act-789 offset=99012
                2024-01-15T14:27:03Z INFO  Starting OpenSearch index for act-789
                2024-01-15T14:28:03Z ERROR OpenSearch index timeout after 60s activityId=act-789 host=search-cluster-01
                2024-01-15T14:28:04Z ERROR Retry 1/3 for act-789 - will re-attempt in 5s
                2024-01-15T14:28:09Z ERROR OpenSearch index timeout after 60s activityId=act-789 (retry 1)
                2024-01-15T14:28:10Z ERROR Retry 2/3 for act-789 - will re-attempt in 5s
                2024-01-15T14:28:15Z ERROR OpenSearch index timeout after 60s activityId=act-789 (retry 2)
                2024-01-15T14:28:15Z ERROR All retries exhausted for act-789. DLQ handler not configured in this env; event dropped.
                2024-01-15T14:28:20Z INFO  Resuming normal consumption
                """,

            ["content-search-service"] = """
                2024-01-15T14:32:10Z INFO  Query q="quadratic equations" results=0 userId=u-55
                2024-01-15T14:32:11Z INFO  Query q="act-789" results=0
                2024-01-15T14:33:00Z INFO  Index health check: 12,394 documents, last update 14:26:55Z
                """,
        };
}
