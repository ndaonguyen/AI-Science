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
        var override_ = _session.TurnTimeWindowOverride;
        if (override_ is null)
        {
            // No override for this turn — standard behaviour.
            return await _inner.ExecuteAsync(input, ct);
        }

        // Rebuild the JSON with overridden startTime/endTime while preserving
        // every other field. We copy into a Dictionary so we can serialise
        // back to a new JsonElement deterministically.
        var rewritten = new Dictionary<string, JsonElement>();
        if (input.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in input.EnumerateObject())
            {
                // Skip startTime/endTime from the original — we'll write our own.
                if (prop.NameEquals("startTime") || prop.NameEquals("endTime")) continue;
                rewritten[prop.Name] = prop.Value.Clone();
            }
        }

        // Emit the forced values as string JSON. Using JsonSerializer.Serialize
        // on a string handles escaping correctly.
        using var startDoc = JsonDocument.Parse(JsonSerializer.Serialize(override_.Value.Start));
        using var endDoc   = JsonDocument.Parse(JsonSerializer.Serialize(override_.Value.End));
        rewritten["startTime"] = startDoc.RootElement.Clone();
        rewritten["endTime"]   = endDoc.RootElement.Clone();

        // Serialise back to a JsonElement the inner tool will parse.
        var json = JsonSerializer.Serialize(rewritten);
        using var newDoc = JsonDocument.Parse(json);
        var newInput = newDoc.RootElement.Clone();

        // Leave a server-side breadcrumb in stderr so it's obvious in Rider's
        // run output whether enforcement kicked in for a given call.
        Console.Error.WriteLine(
            $"[TimeWindowEnforcing] overrode startTime/endTime for {_inner.Name}: " +
            $"{override_.Value.Start} → {override_.Value.End}");

        return await _inner.ExecuteAsync(newInput, ct);
    }

    public void Dispose()
    {
        if (_inner is IDisposable d) d.Dispose();
    }
}
