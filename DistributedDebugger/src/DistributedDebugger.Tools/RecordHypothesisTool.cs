using System.Text.Json;
using System.Threading.Channels;
using DistributedDebugger.Core.Tools;

namespace DistributedDebugger.Tools;

/// <summary>
/// Lets the agent explicitly declare a working hypothesis during investigation.
///
/// Why this exists: in real bug hunting, an engineer forms a theory ("I think
/// Kafka consumer lag is causing this"), gathers evidence for/against it, then
/// either confirms or moves to the next theory. Without this tool, the model's
/// hypotheses are buried in free-text reasoning and hard to audit.
///
/// The tool emits a HypothesisEvent into the trace so you can replay the
/// investigator's thought process step by step. It does NOT short-circuit the
/// loop — the agent must still gather evidence and call finish_investigation.
/// </summary>
public sealed class RecordHypothesisTool : IDebugTool
{
    // Hypotheses are emitted via a channel the agent drains into the trace.
    // Keeping this tool pure (no direct trace-list mutation) makes it safe to
    // unit test without wiring up a whole agent.
    private readonly Channel<(string Hypothesis, string Reasoning)> _channel;

    public RecordHypothesisTool(Channel<(string, string)> channel)
    {
        _channel = channel;
    }

    public string Name => "record_hypothesis";

    public string Description =>
        "Record a working hypothesis about what's causing the bug. Use this whenever " +
        "you form a new theory based on evidence so far. You can record multiple " +
        "hypotheses — each one gets added to the investigation trace.";

    public JsonElement InputSchema { get; } = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "hypothesis": {
              "type": "string",
              "description": "Short statement of the theory, e.g. 'Kafka consumer lag in content-media-service'."
            },
            "reasoning": {
              "type": "string",
              "description": "Why you think this — what evidence led to this theory."
            }
          },
          "required": ["hypothesis", "reasoning"]
        }
        """
    ).RootElement.Clone();

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement input, CancellationToken ct)
    {
        var hypothesis = input.TryGetProperty("hypothesis", out var h) ? h.GetString() ?? "" : "";
        var reasoning = input.TryGetProperty("reasoning", out var r) ? r.GetString() ?? "" : "";

        if (string.IsNullOrWhiteSpace(hypothesis))
        {
            return new ToolExecutionResult(
                "Error: 'hypothesis' is required and cannot be empty.",
                IsError: true);
        }

        await _channel.Writer.WriteAsync((hypothesis, reasoning), ct);

        return new ToolExecutionResult(
            $"Hypothesis recorded: {hypothesis}. Continue investigating to confirm or rule it out.",
            IsError: false);
    }
}
