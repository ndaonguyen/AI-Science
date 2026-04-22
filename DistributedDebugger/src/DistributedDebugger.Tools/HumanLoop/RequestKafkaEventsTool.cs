using System.Text.Json;
using DistributedDebugger.Core.Tools;

namespace DistributedDebugger.Tools.HumanLoop;

/// <summary>
/// Agent-facing tool for Kafka event inspection. Since EP engineers don't have
/// direct broker access from their laptops, the agent describes what events
/// it's looking for, and the human checks via whatever UI they have
/// (Conduktor, AKHQ, Kafdrop, etc.) and pastes back what they find.
///
/// The tool leans on the "missing events matter" principle — the human can
/// reply "empty" to tell the agent a specific event wasn't found, and that
/// gap is often the root cause.
///
/// Replaces MockKafkaEventTool from Phase 1.
/// </summary>
public sealed class RequestKafkaEventsTool : IDebugTool
{
    private readonly IHumanDataProvider _provider;

    public RequestKafkaEventsTool(IHumanDataProvider provider)
    {
        _provider = provider;
    }

    public string Name => "request_kafka_events";

    public string Description =>
        "Ask the engineer to check Kafka for events related to an entity within a time window. " +
        "The engineer has UI-only access to Kafka (Conduktor or similar), so be specific about " +
        "which topic to look at and what to filter by. Pay close attention to MISSING events — " +
        "if a ContentPublished was emitted but no ContentIndexed followed, that gap is the bug.";

    public JsonElement InputSchema { get; } = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "topic": {
              "type": "string",
              "description": "Kafka topic name, e.g. 'content.published' or 'content.indexed'. If unsure, list likely topics in the reason."
            },
            "entityFilter": {
              "type": "object",
              "description": "Filter hints — e.g. { \"activityId\": \"act-789\" } or { \"contentId\": \"content-123\" }. The engineer uses these to search in their UI."
            },
            "timeWindow": {
              "type": "string",
              "description": "Time window, e.g. '2024-01-15T14:20/15:00'. Keep it tight — 30 minutes around the bug time is usually plenty."
            },
            "expectingMissing": {
              "type": "boolean",
              "description": "Set true if you EXPECT no events — i.e. you suspect a message was never published. The engineer will know to report 'empty' as a real finding, not a failure."
            },
            "environment": {
              "type": "string",
              "description": "Environment: 'test', 'staging', or 'live'.",
              "enum": ["test", "staging", "live"]
            },
            "reason": {
              "type": "string",
              "description": "One sentence: what's the hypothesis? e.g. 'Expect a ContentIndexed event matching the ContentPublished for act-789 at 14:27.'"
            }
          },
          "required": ["topic", "entityFilter", "timeWindow", "environment", "reason"]
        }
        """
    ).RootElement.Clone();

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement input, CancellationToken ct)
    {
        if (!input.TryGetProperty("topic", out var topic) ||
            !input.TryGetProperty("entityFilter", out var filter) ||
            !input.TryGetProperty("timeWindow", out var window) ||
            !input.TryGetProperty("environment", out var env) ||
            !input.TryGetProperty("reason", out var reason))
        {
            return new ToolExecutionResult(
                "Error: topic, entityFilter, timeWindow, environment, and reason are required.",
                IsError: true);
        }

        var expectingMissing = input.TryGetProperty("expectingMissing", out var em)
                               && em.ValueKind == JsonValueKind.True;

        var rendered = RenderLookup(
            topic: topic.GetString() ?? "",
            filter: filter,
            timeWindow: window.GetString() ?? "",
            expectingMissing: expectingMissing);

        var request = new HumanDataRequest(
            SourceName: "Kafka",
            RenderedQuery: rendered,
            Reason: reason.GetString() ?? "",
            SuggestedEnv: env.GetString());

        var result = await _provider.RequestDataAsync(request, ct);

        if (result is null)
        {
            return new ToolExecutionResult(
                "Engineer declined to check Kafka. Try CloudWatch logs instead — consumer services usually log the events they process.",
                IsError: false);
        }

        if (string.IsNullOrWhiteSpace(result))
        {
            // Tailor the "empty" message to whether the agent expected missing events.
            var emptyMessage = expectingMissing
                ? "CONFIRMED MISSING: no events found in the specified window. This supports your hypothesis that the event was never emitted."
                : "No events found in the specified window. If you expected events here, this is itself a finding — the upstream producer may not have fired.";
            return new ToolExecutionResult(emptyMessage, IsError: false);
        }

        return new ToolExecutionResult(
            $"Kafka events reported:\n{result}",
            IsError: false);
    }

    private static string RenderLookup(
        string topic,
        JsonElement filter,
        string timeWindow,
        bool expectingMissing)
    {
        var opts = new JsonSerializerOptions { WriteIndented = true };
        var filterJson = JsonSerializer.Serialize(filter, opts);

        var header = expectingMissing
            ? "⚠ EXPECTING EMPTY — please confirm if NO matching events exist in this window."
            : "";

        return
            (header.Length > 0 ? header + "\n\n" : "") +
            $"Topic:       {topic}\n" +
            $"Time window: {timeWindow}\n" +
            $"Filter:      {filterJson}";
    }
}
