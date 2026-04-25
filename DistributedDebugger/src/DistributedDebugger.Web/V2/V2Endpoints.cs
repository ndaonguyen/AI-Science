using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;

namespace DistributedDebugger.Web.V2;

/// <summary>
/// V2 endpoints. Three actions, all stateless on the server:
///
///   POST /api/v2/logs/filter    — keyword + range  → log records
///   POST /api/v2/logs/extend    — pivot + window   → log records
///   POST /api/v2/logs/analyze   — logs + context   → analysis result
///
/// The browser owns the accumulated set of logs across multiple
/// filter/extend calls. The server is a thin adapter over CloudWatch +
/// the analyzer. No GuidedSession, no agent loop, no SSE — these are
/// plain request/response calls because a single fetch is fast enough
/// (no multi-turn streaming to wait through).
/// </summary>
public static class V2Endpoints
{
    public static void MapV2Endpoints(this WebApplication app)
    {
        // Singleton-ish: one CloudWatch client per process. Cached AWS
        // clients live on it.
        var cwClient = new CloudWatchLogClient();
        app.Lifetime.ApplicationStopping.Register(() => cwClient.Dispose());

        app.MapPost("/api/v2/logs/filter", async (
            FilterRequest req, HttpContext ctx, CancellationToken ct) =>
        {
            // Wrap every endpoint in a try/catch returning a JSON error so the
            // browser never sees an empty 500 body (which causes JSON.parse
            // failures on the client). The framework's default 500 response
            // is empty unless we configure a developer exception page; we
            // prefer not to depend on that being on.
            try
            {
                var error = ValidateFilter(req);
                if (error is not null) return Results.BadRequest(new { error });

                var (start, end) = ResolveRange(req.StartTime, req.EndTime);
                var pattern = NormaliseFilterPattern(req.FilterText ?? "");
                var allLogs = new List<LogRecord>();

                // One AWS call per service — they have independent log groups.
                foreach (var svc in req.Services!)
                {
                    var logs = await cwClient.SearchAsync(
                        svc, req.Environment ?? "dev", pattern, start, end,
                        limit: req.Limit ?? 500, ct);
                    allLogs.AddRange(logs);
                }

                // Server-side ordering by timestamp gives the browser a stable
                // baseline regardless of the order CloudWatch returned events
                // (CloudWatch's order is per-stream, not per-log-group).
                allLogs.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

                return Results.Ok(new
                {
                    logs = allLogs,
                    appliedFilter = pattern,
                    window = new { start, end },
                });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[v2/filter] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                return Results.Json(new { error = $"{ex.GetType().Name}: {ex.Message}" }, statusCode: 500);
            }
        });

        app.MapPost("/api/v2/logs/extend", async (
            ExtendRequest req, HttpContext ctx, CancellationToken ct) =>
        {
            try
            {
                var error = ValidateExtend(req);
                if (error is not null) return Results.BadRequest(new { error });

                var pivot = req.Around!.Value;
                var minutes = req.WindowMinutes ?? 1;
                // ±0 is a valid user choice ("just this second") but CloudWatch
                // rejects start==end. Floor to 30 seconds either side, which
                // catches anything stamped within the same second the user
                // clicked while still being a clearly minimal window.
                var window = minutes <= 0
                    ? TimeSpan.FromSeconds(30)
                    : TimeSpan.FromMinutes(minutes);
                var allLogs = new List<LogRecord>();

                foreach (var svc in req.Services!)
                {
                    var logs = await cwClient.ExtendAsync(
                        svc, req.Environment ?? "dev", pivot, window,
                        limit: req.Limit ?? 500, ct);
                    allLogs.AddRange(logs);
                }
                allLogs.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

                return Results.Ok(new
                {
                    logs = allLogs,
                    window = new { start = pivot - window, end = pivot + window },
                });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[v2/extend] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                return Results.Json(new { error = $"{ex.GetType().Name}: {ex.Message}" }, statusCode: 500);
            }
        });

        app.MapPost("/api/v2/logs/analyze", async (
            AnalyzeRequest req, IConfiguration cfg, CancellationToken ct) =>
        {
            try
            {
                if (req.Logs is null || req.Logs.Count == 0)
                    return Results.BadRequest(new { error = "no logs to analyse" });
                if (string.IsNullOrWhiteSpace(req.Description))
                    return Results.BadRequest(new { error = "description is required" });

                var openaiKey = cfg["OPENAI_API_KEY"]
                                ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
                if (string.IsNullOrWhiteSpace(openaiKey))
                    return Results.Json(new { error = "OPENAI_API_KEY is not configured" }, statusCode: 500);

                var analyzer = new LogAnalyzer(openaiKey);
                var result = await analyzer.AnalyzeAsync(
                    req.Description, req.TicketId, req.Logs, ct);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[v2/analyze] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                return Results.Json(new { error = $"{ex.GetType().Name}: {ex.Message}" }, statusCode: 500);
            }
        });
    }

    // ---- request types ----

    public sealed record FilterRequest(
        IReadOnlyList<string>? Services,
        string? Environment,
        string? FilterText,
        DateTimeOffset? StartTime,
        DateTimeOffset? EndTime,
        int? Limit);

    public sealed record ExtendRequest(
        IReadOnlyList<string>? Services,
        string? Environment,
        DateTimeOffset? Around,
        int? WindowMinutes,
        int? Limit);

    public sealed record AnalyzeRequest(
        string? Description,
        string? TicketId,
        IReadOnlyList<LogRecord>? Logs);

    // ---- helpers ----

    private static string? ValidateFilter(FilterRequest r) =>
        (r.Services is null || r.Services.Count == 0) ? "services is required" : null;

    private static string? ValidateExtend(ExtendRequest r) =>
        r.Around is null ? "around (timestamp) is required"
        : (r.Services is null || r.Services.Count == 0) ? "services is required"
        : null;

    /// <summary>
    /// Defaults a missing range to "last 1 hour from now" so the form is
    /// usable without configuring a time picker for the simple case.
    /// </summary>
    private static (DateTimeOffset start, DateTimeOffset end) ResolveRange(
        DateTimeOffset? start, DateTimeOffset? end)
    {
        if (start is not null && end is not null) return (start.Value, end.Value);
        var now = DateTimeOffset.UtcNow;
        return (now.AddHours(-1), now);
    }

    /// <summary>
    /// Same normalisation rule as the V1 enforcing tool: multi-word plain
    /// text gets double-quoted so AWS treats it as a single literal phrase
    /// rather than space-separated AND'd terms. CloudWatch syntax users
    /// (strings starting with " { ? -) pass through untouched.
    /// </summary>
    internal static string NormaliseFilterPattern(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0) return trimmed;
        var first = trimmed[0];
        if (first == '"' || first == '{' || first == '?' || first == '-') return trimmed;
        if (!trimmed.Contains(' ')) return trimmed;
        return "\"" + trimmed.Replace("\"", "\\\"") + "\"";
    }
}
