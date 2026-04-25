using System.Text.Json;
using DistributedDebugger.Core.Tools;

namespace DistributedDebugger.Web;

/// <summary>
/// Decorator around an existing IDebugTool that stomps the <c>startTime</c>
/// and <c>endTime</c> fields in the tool's input JSON with values from the
/// containing <see cref="GuidedSession"/> whenever a time-window override
/// is set for the current turn.
///
/// Why this exists: LLMs routinely rewrite timestamps despite being told to
/// use them verbatim in the instruction prompt. In the dig_errors flow the
/// user clicks a specific log row expecting a symmetric window around it,
/// but the model would "helpfully" round the end or substitute a near value,
/// producing lopsided windows. Fixing this via prompt engineering is
/// unreliable; the only durable fix is to enforce the values server-side
/// before the inner tool ever sees them.
///
/// The override is session-scoped and per-turn. /api/guided/step sets it
/// when a context with StartTime+EndTime is received and clears it in the
/// finally block. Normal turns (no override) pass through untouched, so
/// the LLM retains full freedom whenever the user hasn't pinned a window.
///
/// Everything else — Name, Description, InputSchema, IDisposable — is
/// forwarded verbatim so the LLM sees the same tool contract as before.
/// </summary>
internal sealed class TimeWindowEnforcingTool : IDebugTool, IDisposable
{
    private readonly IDebugTool _inner;
    private readonly GuidedSession _session;

    public TimeWindowEnforcingTool(IDebugTool inner, GuidedSession session)
    {
        _inner = inner;
        _session = session;
    }

    public string Name => _inner.Name;
    public string Description => _inner.Description;
    public JsonElement InputSchema => _inner.InputSchema;

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement input, CancellationToken ct)
    {
        var timeOverride = _session.TurnTimeWindowOverride;
        var filterOverride = _session.TurnFilterPatternOverride;

        if (timeOverride is null && filterOverride is null)
        {
            // No override for this turn — standard behaviour. Surface this
            // fact in the event feed so 'why isn't enforcement working' is
            // easy to diagnose — typically it means the browser didn't send
            // a time context (dig_errors without a clicked row, for example).
            _session.AgentEvents.Writer.TryWrite(new SessionEvent("diagnostic",
                new { message = $"[enforce] no overrides set; LLM values used as-is for {_inner.Name}" }));
            return await _inner.ExecuteAsync(input, ct);
        }

        // Capture what the LLM actually emitted so we can show a before/after
        // in the event feed. Helps confirm to the user that enforcement ran
        // and shows exactly what was rewritten.
        string llmStart = "(missing)", llmEnd = "(missing)", llmFilter = "(missing)";
        if (input.ValueKind == JsonValueKind.Object)
        {
            if (input.TryGetProperty("startTime", out var s) && s.ValueKind == JsonValueKind.String)
                llmStart = s.GetString() ?? "(missing)";
            if (input.TryGetProperty("endTime", out var e) && e.ValueKind == JsonValueKind.String)
                llmEnd = e.GetString() ?? "(missing)";
            if (input.TryGetProperty("filterPattern", out var f) && f.ValueKind == JsonValueKind.String)
                llmFilter = f.GetString() ?? "(missing)";
        }

        // Rebuild the JSON with whichever fields are overridden. Preserve
        // every other field so query, service, environment etc. pass through
        // untouched. Dictionary copy is the cleanest way to do partial
        // overrides on an immutable JsonElement.
        var rewritten = new Dictionary<string, JsonElement>();
        if (input.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in input.EnumerateObject())
            {
                if (timeOverride is not null && (prop.NameEquals("startTime") || prop.NameEquals("endTime"))) continue;
                if (filterOverride is not null && prop.NameEquals("filterPattern")) continue;
                rewritten[prop.Name] = prop.Value.Clone();
            }
        }

        if (timeOverride is not null)
        {
            using var startDoc = JsonDocument.Parse(JsonSerializer.Serialize(timeOverride.Value.Start));
            using var endDoc   = JsonDocument.Parse(JsonSerializer.Serialize(timeOverride.Value.End));
            rewritten["startTime"] = startDoc.RootElement.Clone();
            rewritten["endTime"]   = endDoc.RootElement.Clone();
        }

        string? appliedFilter = null;
        if (filterOverride is not null)
        {
            // CloudWatch's FilterPattern syntax: bare space-separated terms
            // are AND'd ('foo bar' = events containing both foo AND bar
            // anywhere). A double-quoted phrase is treated as a single
            // literal substring. The user's intent when typing 'Unexpected
            // Execution Error' in the filter field is almost certainly
            // 'find that exact phrase', so we wrap multi-word plain text
            // in quotes if it isn't already. Strings that look like the
            // user already wrote CloudWatch syntax (start with '?', '{', '"'
            // or '-') are left alone.
            appliedFilter = NormaliseFilterPattern(filterOverride);

            using var filterDoc = JsonDocument.Parse(JsonSerializer.Serialize(appliedFilter));
            rewritten["filterPattern"] = filterDoc.RootElement.Clone();
        }

        var json = JsonSerializer.Serialize(rewritten);
        using var newDoc = JsonDocument.Parse(json);
        var newInput = newDoc.RootElement.Clone();

        // Build a single human-readable diagnostic line covering whatever
        // was rewritten this turn. Easier to scan than two separate events.
        var parts = new List<string>();
        if (timeOverride is not null)
        {
            parts.Add($"start={llmStart}→{timeOverride.Value.Start}");
            parts.Add($"end={llmEnd}→{timeOverride.Value.End}");
        }
        if (filterOverride is not null)
        {
            parts.Add($"filterPattern={llmFilter}→{appliedFilter}");
        }
        var summary = $"[enforce] {_inner.Name}: {string.Join(", ", parts)}";

        Console.Error.WriteLine(summary);
        _session.AgentEvents.Writer.TryWrite(new SessionEvent("diagnostic", new { message = summary }));

        return await _inner.ExecuteAsync(newInput, ct);
    }

    /// <summary>
    /// Coerce user-typed filter text into a CloudWatch-friendly FilterPattern.
    /// Plain multi-word phrases get double-quoted so AWS treats them as a
    /// single literal substring; existing CloudWatch syntax (quoted phrases,
    /// JSON expressions, term/exclusion patterns) is left alone.
    /// </summary>
    internal static string NormaliseFilterPattern(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0) return trimmed;
        // Heuristic: if it already looks like CloudWatch syntax, don't touch it.
        var first = trimmed[0];
        if (first == '"' || first == '{' || first == '?' || first == '-') return trimmed;
        // Single token — bare term works as substring. No quoting needed.
        if (!trimmed.Contains(' ')) return trimmed;
        // Multi-word plain text — quote so AWS treats it as one phrase.
        return "\"" + trimmed.Replace("\"", "\\\"") + "\"";
    }

    public void Dispose()
    {
        if (_inner is IDisposable d) d.Dispose();
    }
}
