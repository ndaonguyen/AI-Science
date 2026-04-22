using System.Text.Json;
using DistributedDebugger.Core.Tools;

namespace DistributedDebugger.Tools;

/// <summary>
/// Placeholder Kafka event lookup for Phase 1. In production this would
/// query a Kafka consumer or event archive; here it returns fixtures keyed
/// by an entity id (activityId, contentId, etc.).
///
/// Kept separate from the log tool because events and logs are structurally
/// different — events are discrete, ordered, and tell you about state
/// transitions; logs are free-form text that may or may not correspond to a
/// specific entity.
/// </summary>
public sealed class MockKafkaEventTool : IDebugTool
{
    private readonly IReadOnlyDictionary<string, string> _fixtures;

    public MockKafkaEventTool(IReadOnlyDictionary<string, string>? fixtures = null)
    {
        _fixtures = fixtures ?? DefaultFixtures;
    }

    public string Name => "fetch_kafka_events";

    public string Description =>
        "Look up Kafka events for a specific entity ID (activityId, contentId, etc.) " +
        "within a time window. Returns ordered events so you can see what happened " +
        "and — just as importantly — what DIDN'T happen (missing expected events).";

    public JsonElement InputSchema { get; } = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "entityId": {
              "type": "string",
              "description": "The entity identifier, e.g. 'act-789', 'content-123'."
            },
            "timeWindow": {
              "type": "string",
              "description": "Optional time window hint, e.g. '2024-01-15T14:00/14:45'."
            }
          },
          "required": ["entityId"]
        }
        """
    ).RootElement.Clone();

    public Task<ToolExecutionResult> ExecuteAsync(JsonElement input, CancellationToken ct)
    {
        var entityId = input.TryGetProperty("entityId", out var e) ? e.GetString() ?? "" : "";

        if (string.IsNullOrWhiteSpace(entityId))
        {
            return Task.FromResult(new ToolExecutionResult(
                "Error: 'entityId' is required.",
                IsError: true));
        }

        if (!_fixtures.TryGetValue(entityId, out var events))
        {
            return Task.FromResult(new ToolExecutionResult(
                $"No Kafka events found for entityId '{entityId}'. " +
                $"Known fixtures: {string.Join(", ", _fixtures.Keys)}.",
                IsError: false));
        }

        return Task.FromResult(new ToolExecutionResult(
            $"Kafka events for {entityId}:\n{events}",
            IsError: false));
    }

    /// <summary>
    /// Fixtures for the same act-789 story as the log tool. Note there is NO
    /// ContentIndexed event — the agent should spot that missing event as key
    /// evidence.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> DefaultFixtures =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["act-789"] = """
                14:27:01Z  topic=content.published    ContentPublished    {activityId: "act-789", userId: "u-42"}
                14:27:02Z  topic=content.audit        ActivityAudited     {activityId: "act-789", action: "publish"}
                (no ContentIndexed event found in this time window — expected one shortly after ContentPublished)
                """,
            ["content-123"] = """
                14:15:00Z  topic=content.created      ContentCreated      {contentId: "content-123"}
                14:15:02Z  topic=content.indexed      ContentIndexed      {contentId: "content-123"}
                """,
        };
}
