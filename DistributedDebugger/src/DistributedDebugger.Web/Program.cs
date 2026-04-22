using System.Text.Json;
using DistributedDebugger.Agent;
using DistributedDebugger.Core.Models;
using DistributedDebugger.Web;

var builder = WebApplication.CreateBuilder(args);

// Default port is 5123 — picked at random to avoid clashing with common dev
// defaults (5000 / 5001 / 5173 / 3000). Overridable via normal ASP.NET Core
// config (e.g. urls=http://localhost:5400).
builder.WebHost.UseUrls("http://localhost:5123");

// One registry per process. Singleton is correct: the registry is thread-safe
// and only holds in-flight sessions, not long-lived state.
builder.Services.AddSingleton<SessionRegistry>();
builder.Services.AddSingleton<GuidedSessionRegistry>();

// Nicer JSON naming for browser consumption.
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

var app = builder.Build();

// Serve the SPA (wwwroot/index.html + app.js + styles.css).
app.UseDefaultFiles();
app.UseStaticFiles();

// ---- API endpoints ----

// Fetch raw matching CloudWatch log lines — no AI, no session needed.
// Used by the UI to preview log results before starting an investigation.
app.MapPost("/api/logs/raw", async (RawLogsRequest req, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Service) || string.IsNullOrWhiteSpace(req.Environment))
        return Results.BadRequest(new { error = "service and environment are required" });

    var logGroup = $"/{req.Environment.ToLowerInvariant()}/ecs/{req.Service.ToLowerInvariant()}";
    var region = req.Environment.EndsWith("-ca-central-1", StringComparison.OrdinalIgnoreCase)
        ? "ca-central-1" : "ap-southeast-2";
    var profile = req.Environment.ToLowerInvariant() switch
    {
        "test"              => "dev",
        "staging"           => "staging",
        "live"              => "live",
        "live-ca-central-1" => "live-ca",
        _                   => "dev",
    };

    var end   = DateTimeOffset.TryParse(req.EndTime,   out var pe) ? pe : DateTimeOffset.UtcNow;
    var start = DateTimeOffset.TryParse(req.StartTime, out var ps) ? ps : end.AddHours(-1);

    try
    {
        var events = await RawLogFetcher.FetchAsync(
            region, logGroup, profile, req.FilterText ?? "", start, end, limit: 200, ct);
        return Results.Ok(new { logGroup, start, end, count = events.Count, events });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

// Start an investigation. Returns { sessionId } which the browser then uses
// to subscribe to /api/stream/{id} and post paste answers to /api/paste/{id}.
app.MapPost("/api/investigate",
    (InvestigateRequest req, SessionRegistry reg, IConfiguration cfg) =>
    {
        if (string.IsNullOrWhiteSpace(req.Description))
        {
            return Results.BadRequest(new { error = "description is required" });
        }

        var session = reg.Create();
        _ = InvestigationLauncher.StartAsync(session, req, cfg);
        return Results.Ok(new { sessionId = session.Id });
    });

// SSE stream of events for a session. The browser reconnects automatically
// on drop; we replay the PendingDataRequest (if any) so a fresh tab can still
// answer a pending paste prompt.
app.MapGet("/api/stream/{sessionId}", async (
    string sessionId, SessionRegistry reg, HttpContext ctx, CancellationToken ct) =>
{
    var session = reg.Get(sessionId);
    if (session is null)
    {
        ctx.Response.StatusCode = 404;
        await ctx.Response.WriteAsync("unknown session", ct);
        return;
    }

    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"] = "no"; // disable proxy buffering if any

    // Replay a pending paste request so a reconnecting browser sees it.
    if (session.PendingDataRequest is { } pending)
    {
        await WriteSseAsync(ctx, new SessionEvent("paste_request", new
        {
            sourceName = pending.SourceName,
            renderedQuery = pending.RenderedQuery,
            reason = pending.Reason,
            suggestedEnv = pending.SuggestedEnv,
        }), ct);
    }

    // Replay the final report if we're already done (late-tab case).
    if (session.IsComplete && session.FinalReportMarkdown is not null)
    {
        await WriteSseAsync(ctx, new SessionEvent("completed",
            new { markdown = session.FinalReportMarkdown, status = "Completed" }), ct);
        return;
    }

    try
    {
        await foreach (var ev in session.AgentEvents.Reader.ReadAllAsync(ct))
        {
            await WriteSseAsync(ctx, ev, ct);
        }
    }
    catch (OperationCanceledException) { /* normal on browser close */ }
});

// Browser submits an answer to a pending paste prompt.
//   mode = "paste" → use Text as the response
//   mode = "empty" → empty string ("no match")
//   mode = "skip"  → null ("engineer declined")
app.MapPost("/api/paste/{sessionId}",
    async (string sessionId, PasteSubmission sub, SessionRegistry reg, GuidedSessionRegistry greg) =>
{
    // Try the autonomous registry first; fall back to guided. A paste can
    // belong to either kind of session.
    var autonomous = reg.Get(sessionId);
    if (autonomous is not null)
    {
        var v = MapPasteValue(sub);
        await autonomous.PasteResponses.Writer.WriteAsync(v);
        return Results.Ok();
    }
    var guided = greg.Get(sessionId);
    if (guided is not null)
    {
        var v = MapPasteValue(sub);
        await guided.PasteResponses.Writer.WriteAsync(v);
        return Results.Ok();
    }
    return Results.NotFound(new { error = "unknown session" });
});


// ---- Guided mode endpoints ----

// Start a guided session. Unlike /api/investigate, this does NOT kick off
// the agent. It creates a session in a waiting state; the first /api/step
// call drives the first turn.
app.MapPost("/api/guided/start",
    (GuidedStartRequest req, GuidedSessionRegistry reg, IConfiguration cfg) =>
{
    if (string.IsNullOrWhiteSpace(req.Description))
    {
        return Results.BadRequest(new { error = "description is required" });
    }
    try
    {
        var session = GuidedSessionLauncher.Create(reg, req, cfg);
        return Results.Ok(new { sessionId = session.Id });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// Subscribe to a guided session's event stream. Shape is identical to the
// autonomous /api/stream endpoint so the browser can reuse the same client.
app.MapGet("/api/guided/stream/{sessionId}", async (
    string sessionId, GuidedSessionRegistry reg, HttpContext ctx, CancellationToken ct) =>
{
    var session = reg.Get(sessionId);
    if (session is null)
    {
        ctx.Response.StatusCode = 404;
        await ctx.Response.WriteAsync("unknown session", ct);
        return;
    }

    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";

    if (session.PendingDataRequest is { } pending)
    {
        await WriteSseAsync(ctx, new SessionEvent("paste_request", new
        {
            sourceName = pending.SourceName,
            renderedQuery = pending.RenderedQuery,
            reason = pending.Reason,
            suggestedEnv = pending.SuggestedEnv,
        }), ct);
    }

    if (session.IsComplete && session.FinalReportMarkdown is not null)
    {
        await WriteSseAsync(ctx, new SessionEvent("completed",
            new { markdown = session.FinalReportMarkdown, status = "Completed" }), ct);
        return;
    }

    try
    {
        await foreach (var ev in session.AgentEvents.Reader.ReadAllAsync(ct))
        {
            await WriteSseAsync(ctx, ev, ct);
        }
    }
    catch (OperationCanceledException) { /* normal on browser close */ }
});

// Advance a guided session by one turn. Translates the browser's high-level
// action into the natural-language instruction the agent expects.
app.MapPost("/api/guided/step/{sessionId}",
    async (string sessionId, GuidedStepRequest req, GuidedSessionRegistry reg, CancellationToken ct) =>
{
    var session = reg.Get(sessionId);
    if (session is null) return Results.NotFound(new { error = "unknown session" });
    if (session.IsComplete) return Results.Ok(new { status = "already_complete" });

    // Simple turn serialisation. If a second POST arrives while the first is
    // running we reject — the UI should disable buttons during a turn anyway.
    if (session.TurnInProgress)
    {
        return Results.Conflict(new { error = "a turn is already in progress" });
    }
    session.TurnInProgress = true;

    try
    {
        var instruction = BuildInstruction(req);

        // Push a user-visible "turn_started" event so the event feed has
        // context for the tool calls that follow.
        await session.AgentEvents.Writer.WriteAsync(new SessionEvent("turn_started",
            new { action = req.Action, instruction }));

        var step = await session.Agent.RunStepAsync(
            session.History,
            instruction,
            onEvent: ev =>
            {
                // Forward structured trace events to the browser. Same mapping
                // as in the autonomous launcher.
                session.AgentEvents.Writer.TryWrite(MapTraceEvent(ev));
            },
            ct: ct);

        await session.AgentEvents.Writer.WriteAsync(new SessionEvent("turn_summary",
            new
            {
                findings = step.Findings,
                hypothesis = step.Hypothesis,
                suggestedNext = step.SuggestedNext,
                inputTokens = step.InputTokens,
                outputTokens = step.OutputTokens,
            }));

        // If the turn's action was "finish", also persist a markdown report.
        if (string.Equals(req.Action, "finish", StringComparison.OrdinalIgnoreCase))
        {
            var md = BuildGuidedReport(session, step);
            session.Complete(md);
            await session.AgentEvents.Writer.WriteAsync(new SessionEvent("completed",
                new { markdown = md, status = "Completed" }));
        }

        return Results.Ok(new
        {
            findings = step.Findings,
            hypothesis = step.Hypothesis,
            suggestedNext = step.SuggestedNext,
        });
    }
    catch (Exception ex)
    {
        await session.AgentEvents.Writer.WriteAsync(new SessionEvent("error",
            new { message = ex.Message }));
        return Results.Problem(ex.Message);
    }
    finally
    {
        session.TurnInProgress = false;
    }
});

app.Run();


// ---- helpers ----

static string? MapPasteValue(PasteSubmission sub) => sub.Mode switch
{
    "paste" => string.IsNullOrWhiteSpace(sub.Text) ? "" : sub.Text,
    "empty" => "",
    "skip"  => null,
    _       => null,
};

/// <summary>
/// Translate the browser's high-level action into a natural-language
/// instruction the LLM will see. This is the "prompt engineering" for
/// guided mode — it's what turns a button click into an agent turn.
/// </summary>
static string BuildInstruction(GuidedStepRequest req)
{
    var ctx = req.Context;
    var services = ctx?.Services is { Count: > 0 } s ? string.Join(", ", s) : null;
    var env = string.IsNullOrWhiteSpace(ctx?.Environment) ? null : ctx!.Environment;

    return req.Action?.ToLowerInvariant() switch
    {
        "dig_errors" when services is not null =>
            $"Search CloudWatch logs in these services: {services}" +
            (env is not null ? $" (environment: {env})" : "") +
            BuildTimeRangeClause(ctx) +
            ". Fetch ALL logs in this time window — do NOT apply any filterPattern. " +
            "Then analyze everything returned and identify anything suspicious: exceptions, stack traces, warnings, timeouts, connection errors, null references, or anything that could explain the error we already found. " +
            "The `query` field should be a natural language summary of the bug we are investigating. " +
            "Use exactly the startTime and endTime provided. " +
            "After retrieving logs, provide: 1) Root cause or suspicious patterns found, 2) Key log lines that stand out, 3) Updated hypothesis.",

        "search_logs" or "more_logs" when services is not null =>
            $"Search CloudWatch logs in these services: {services}" +
            (env is not null ? $" (environment: {env})" : "") +
            BuildTimeRangeClause(ctx) +
            (string.IsNullOrWhiteSpace(ctx?.FilterText) ? "" : $" with filterPattern \"{ctx!.FilterText}\"") +
            ". IMPORTANT: " +
            "1) If a filterPattern is specified above, use it exactly as the `filterPattern` field — do NOT put it into the `query` field. " +
            "2) The `query` field must be a natural language description of what you are looking for based on the bug description, e.g. 'content rendering error in authoring service'. " +
            "3) Use exactly the startTime and endTime provided — do NOT invent or adjust them. " +
            "If multiple services are listed, make one search_logs call per service.",

        "mongo" =>
            "Check MongoDB for relevant documents. Formulate a request_mongo_query " +
            "call using the evidence you already have (activity id, user id, etc). " +
            "Only one query.",

        "opensearch" =>
            "Check OpenSearch indexed state. Formulate a request_opensearch_query " +
            "call using the evidence you already have. Only one query.",

        "kafka" =>
            "Check Kafka for whether a relevant event was emitted (or not). " +
            "Formulate a request_kafka_events call. Only one query.",

        "finish" =>
            "You have enough evidence. Call finish_investigation now with a " +
            "structured root cause. Also produce a concise final summary.",

        _ => string.IsNullOrWhiteSpace(ctx?.FreeText)
            ? "Continue the investigation based on what makes sense next."
            : ctx!.FreeText!,
    };
}

static string BuildTimeRangeClause(GuidedStepContext? ctx)
{
    if (ctx is null) return "";
    var start = ctx.StartTime;
    var end   = ctx.EndTime;
    if (!string.IsNullOrWhiteSpace(start) && !string.IsNullOrWhiteSpace(end))
        return $" for the time window {start} to {end}";
    if (!string.IsNullOrWhiteSpace(start))
        return $" starting from {start}";
    if (!string.IsNullOrWhiteSpace(end))
        return $" up to {end}";
    return "";
}

/// <summary>
/// Build a final markdown report when the user hits "finish". We draw from
/// the session history rather than the agent's structured RootCauseReport —
/// in guided mode the agent may have finished without calling the
/// finish_investigation tool, so a history-based render is more reliable.
/// </summary>
static string BuildGuidedReport(GuidedSession session, GuidedStepResult lastStep)
{
    var sb = new System.Text.StringBuilder();
    sb.AppendLine("# Bug Investigation (guided)");
    if (!string.IsNullOrWhiteSpace(session.Report.TicketId))
        sb.AppendLine($"Ticket: **{session.Report.TicketId}**");
    sb.AppendLine();
    sb.AppendLine("## Description");
    sb.AppendLine(session.Report.Description);
    sb.AppendLine();
    sb.AppendLine("## Final summary");
    sb.AppendLine();
    sb.AppendLine("**Hypothesis:** " + (string.IsNullOrWhiteSpace(lastStep.Hypothesis) ? "(none)" : lastStep.Hypothesis));
    sb.AppendLine();
    if (lastStep.Findings.Count > 0)
    {
        sb.AppendLine("**Key findings:**");
        foreach (var f in lastStep.Findings) sb.AppendLine($"- {f}");
    }
    return sb.ToString();
}

/// <summary>
/// Map agent-level trace events to the compact SSE shape the browser expects.
/// Kept in sync with the autonomous ForwardEventToSse mapper so the frontend
/// can share rendering code across both modes.
/// </summary>
static SessionEvent MapTraceEvent(InvestigationEvent ev) => ev switch
{
    ModelCallEvent mc => new SessionEvent("model_call",
        new { iteration = mc.Iteration, messageCount = mc.PromptMessageCount }),
    ModelResponseEvent mr => new SessionEvent("model_response",
        new { iteration = mr.Iteration, text = mr.Text, outputTokens = mr.OutputTokens }),
    ToolCallEvent tc => new SessionEvent("tool_call",
        new { iteration = tc.Iteration, toolName = tc.ToolName,
              input = System.Text.Json.JsonDocument.Parse(tc.Input.GetRawText()).RootElement }),
    ToolResultEvent tr => new SessionEvent("tool_result",
        new { iteration = tr.Iteration, output = tr.Output, isError = tr.IsError }),
    HypothesisEvent h => new SessionEvent("hypothesis",
        new { iteration = h.Iteration, hypothesis = h.Hypothesis, reasoning = h.Reasoning }),
    ErrorEvent e => new SessionEvent("error",
        new { iteration = e.Iteration, message = e.Message }),
    _ => new SessionEvent("unknown", null),
};

static async Task WriteSseAsync(HttpContext ctx, SessionEvent ev, CancellationToken ct)
{
    // Standard SSE wire format: `event: <kind>\ndata: <json>\n\n`
    // The browser's EventSource picks up `event` and uses addEventListener
    // with the matching name — see app.js for the receiving side.
    var json = JsonSerializer.Serialize(ev.Data,
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    await ctx.Response.WriteAsync($"event: {ev.Kind}\n", ct);
    await ctx.Response.WriteAsync($"data: {json}\n\n", ct);
    await ctx.Response.Body.FlushAsync(ct);
}
