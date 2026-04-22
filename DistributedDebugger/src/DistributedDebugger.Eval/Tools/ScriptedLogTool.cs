using System.Text.Json;
using DistributedDebugger.Core.Tools;

namespace DistributedDebugger.Eval.Tools;

/// <summary>
/// Eval-time replacement for CloudWatchLogSearchTool and MockLogSearchTool.
/// Looks up the agent's (service, keyword) pair against a bank of
/// <see cref="ScriptedLogResponse"/> entries from the case YAML and returns
/// the matching logs.
///
/// Match rules (deliberately generous):
///   - service equality (case-insensitive)
///   - case's MatchesKeyword is a substring of the agent's actual keyword,
///     OR the agent's keyword is a substring of the case's MatchesKeyword.
///     (The second half catches the agent searching for a prefix like "act-789"
///      when the case anchored on a fuller string.)
///
/// If nothing matches: return an empty-result message rather than erroring.
/// The agent should treat "no logs" as evidence, not as tool failure.
/// </summary>
public sealed class ScriptedLogTool : IDebugTool
{
    private readonly IReadOnlyList<ScriptedLogResponse> _responses;

    public ScriptedLogTool(IReadOnlyList<ScriptedLogResponse> responses)
    {
        _responses = responses;
    }

    public string Name => "search_logs";

    public string Description =>
        "Search service logs for a given service and keyword within a time window. " +
        "Returns matching log lines, or a 'no results' message if nothing was found.";

    // Match the input schema of the real tool so the model produces the same
    // shape of call. Only required fields needed — extra ones (filterPattern,
    // times) are ignored in eval runs.
    public JsonElement InputSchema { get; } = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "service":     { "type": "string" },
            "environment": { "type": "string" },
            "query":       { "type": "string" },
            "keyword":     { "type": "string" },
            "filterPattern": { "type": "string" },
            "startTime":   { "type": "string" },
            "endTime":     { "type": "string" }
          },
          "required": ["service", "query"]
        }
        """
    ).RootElement.Clone();

    public Task<ToolExecutionResult> ExecuteAsync(JsonElement input, CancellationToken ct)
    {
        var service = input.TryGetProperty("service", out var s) ? s.GetString() ?? "" : "";
        // The agent might call either schema (CloudWatch uses `query`, mock uses
        // `keyword`). Accept either — keyword wins for matching since it's
        // typically tighter.
        var keyword = input.TryGetProperty("keyword", out var k) && k.ValueKind == JsonValueKind.String
            ? k.GetString() ?? ""
            : (input.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "");

        if (string.IsNullOrWhiteSpace(service))
        {
            return Task.FromResult(new ToolExecutionResult(
                "Error: 'service' is required.", IsError: true));
        }

        var match = _responses.FirstOrDefault(r =>
            string.Equals(r.Service, service, StringComparison.OrdinalIgnoreCase)
            && (Contains(keyword, r.MatchesKeyword) || Contains(r.MatchesKeyword, keyword)));

        if (match is null)
        {
            return Task.FromResult(new ToolExecutionResult(
                $"No scripted logs in {service} matched keyword '{keyword}'. " +
                "Treat this as 'no matching logs in the window' — which is itself a signal.",
                IsError: false));
        }

        return Task.FromResult(new ToolExecutionResult(match.Logs, IsError: false));
    }

    private static bool Contains(string haystack, string needle) =>
        !string.IsNullOrWhiteSpace(needle)
        && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
