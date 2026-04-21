using System.Data;
using System.Globalization;
using System.Text.Json;
using HarnessArena.Core.Tools;

namespace HarnessArena.Tools;

/// <summary>
/// Evaluates basic arithmetic expressions. Uses DataTable.Compute under the hood
/// which handles + - * / ( ) and basic precedence. Enough for word problems.
///
/// Deliberately simple: this tool exists to teach the loop, not to be a math engine.
/// </summary>
public sealed class CalculatorTool : ITool
{
    public string Name => "calculator";

    public string Description =>
        "Evaluates a basic arithmetic expression and returns the numeric result. " +
        "Supports + - * / and parentheses. Example input: {\"expression\": \"(12 * 3) - 7\"}";

    public JsonElement InputSchema { get; } = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "expression": {
              "type": "string",
              "description": "Arithmetic expression to evaluate, e.g. '2 + 3 * 4'"
            }
          },
          "required": ["expression"]
        }
        """
    ).RootElement.Clone();

    public Task<ToolExecutionResult> ExecuteAsync(JsonElement input, CancellationToken ct)
    {
        if (!input.TryGetProperty("expression", out var exprEl) ||
            exprEl.ValueKind != JsonValueKind.String)
        {
            return Task.FromResult(new ToolExecutionResult(
                "Error: missing required string property 'expression'.",
                IsError: true));
        }

        var expression = exprEl.GetString()!;
        try
        {
            var result = new DataTable().Compute(expression, null);
            var formatted = Convert.ToDouble(result, CultureInfo.InvariantCulture)
                .ToString("0.##########", CultureInfo.InvariantCulture);
            return Task.FromResult(new ToolExecutionResult(formatted, IsError: false));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ToolExecutionResult(
                $"Error evaluating expression '{expression}': {ex.Message}",
                IsError: true));
        }
    }
}
