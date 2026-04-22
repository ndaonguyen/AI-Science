using System.Text.Json;
using DistributedDebugger.Core.Tools;

namespace DistributedDebugger.Tools;

/// <summary>
/// Called by the agent when it's ready to submit its final root cause analysis.
/// The agent loop intercepts this before execution — the input schema mirrors
/// <see cref="Core.Models.RootCauseReport"/> so the agent produces structured
/// output directly, without a second "extract the JSON" pass.
///
/// This is the same pattern HarnessArena uses with its FinishTool — the tool
/// itself does nothing; its purpose is to give the model a typed "I'm done" signal.
/// </summary>
public sealed class FinishInvestigationTool : IDebugTool
{
    public string Name => "finish_investigation";

    public string Description =>
        "Submit the final root cause analysis. Call this exactly once when you have " +
        "enough evidence to explain the bug. Include a short summary, the likely cause, " +
        "affected services, supporting evidence, and a confidence level.";

    public JsonElement InputSchema { get; } = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "summary": {
              "type": "string",
              "description": "One-sentence summary of what went wrong."
            },
            "likelyCause": {
              "type": "string",
              "description": "The root cause of the bug, in 2-4 sentences."
            },
            "affectedServices": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Services involved (e.g. 'content-media-service', 'authoring-service')."
            },
            "evidence": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Bullet-point evidence supporting the conclusion. Reference logs, events, or data by source."
            },
            "suggestedFix": {
              "type": "string",
              "description": "Concrete next step for the engineer. Can be empty if unclear."
            },
            "confidence": {
              "type": "string",
              "enum": ["Low", "Medium", "High"],
              "description": "How confident you are in this root cause. High = strong evidence across multiple sources. Medium = one source points to it. Low = educated guess."
            }
          },
          "required": ["summary", "likelyCause", "affectedServices", "evidence", "confidence"]
        }
        """
    ).RootElement.Clone();

    public Task<ToolExecutionResult> ExecuteAsync(JsonElement input, CancellationToken ct)
    {
        // Intercepted by the agent loop — this should never actually run.
        return Task.FromResult(new ToolExecutionResult(
            "Internal error: finish_investigation should be intercepted by the agent loop.",
            IsError: true));
    }
}
