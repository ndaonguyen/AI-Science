using System.Text.Json;
using HarnessArena.Core.Tools;

namespace HarnessArena.Tools;

/// <summary>
/// The agent invokes this to signal a final answer. The loop intercepts the call
/// before "executing" it — this tool's ExecuteAsync is only reached in error paths.
/// </summary>
public sealed class FinishTool : ITool
{
    public string Name => "finish";

    public string Description =>
        "Submit the final answer for this task. Call exactly once when you're confident. " +
        "The answer should be the simplest possible form (e.g. just a number, or a single word).";

    public JsonElement InputSchema { get; } = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "answer": {
              "type": "string",
              "description": "The final answer in its simplest form."
            }
          },
          "required": ["answer"]
        }
        """
    ).RootElement.Clone();

    public Task<ToolExecutionResult> ExecuteAsync(JsonElement input, CancellationToken ct)
    {
        // The agent loop special-cases finish and never calls ExecuteAsync on it.
        // If we ever get here, something is wrong with the loop.
        return Task.FromResult(new ToolExecutionResult(
            "Internal error: finish tool should be intercepted by the agent loop.",
            IsError: true));
    }
}
