using System.Text.Json;
using DistributedDebugger.Web;

var builder = WebApplication.CreateBuilder(args);

// Default port is 5123 — picked at random to avoid clashing with common dev
// defaults (5000 / 5001 / 5173 / 3000). Overridable via normal ASP.NET Core
// config (e.g. urls=http://localhost:5400).
builder.WebHost.UseUrls("http://localhost:5123");

// One registry per process. Singleton is correct: the registry is thread-safe
// and only holds in-flight sessions, not long-lived state.
builder.Services.AddSingleton<SessionRegistry>();

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
    async (string sessionId, PasteSubmission sub, SessionRegistry reg) =>
{
    var session = reg.Get(sessionId);
    if (session is null) return Results.NotFound(new { error = "unknown session" });

    string? value = sub.Mode switch
    {
        "paste" => string.IsNullOrWhiteSpace(sub.Text) ? "" : sub.Text,
        "empty" => "",
        "skip"  => null,
        _       => null,
    };

    await session.PasteResponses.Writer.WriteAsync(value);
    return Results.Ok();
});

app.Run();


// ---- helpers ----

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
