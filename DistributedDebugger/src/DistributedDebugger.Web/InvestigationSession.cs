using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using DistributedDebugger.Agent;
using DistributedDebugger.Core.Models;
using DistributedDebugger.Core.Reporting;
using DistributedDebugger.Core.Tools;
using DistributedDebugger.Tools;
using DistributedDebugger.Tools.CloudWatch;
using DistributedDebugger.Tools.HumanLoop;

namespace DistributedDebugger.Web;

/// <summary>
/// One in-flight investigation. The server spins up a Task to run the agent
/// loop; meanwhile the browser connects via Server-Sent Events (SSE) to stream
/// events as they happen and POSTs paste responses back when the agent pauses.
///
/// Concurrency model:
///   - AgentEvents       : unbounded Channel — writer is the agent's onEvent
///                         callback, reader is the SSE endpoint for this session.
///   - PendingDataRequest: nullable HumanDataRequest — set when the agent's
///                         tool asks for data, unset once the browser answers.
///   - PasteResponses    : unbounded Channel — writer is the POST endpoint,
///                         reader is the WebHumanDataProvider.
///
/// A session is single-agent-run. Restarting an investigation creates a new
/// session; this keeps lifecycle reasoning simple.
/// </summary>
public sealed class InvestigationSession
{
    public string Id { get; }
    public Channel<SessionEvent> AgentEvents { get; } = Channel.CreateUnbounded<SessionEvent>();
    public Channel<string?> PasteResponses { get; } = Channel.CreateUnbounded<string?>();

    // Set when a tool is awaiting user paste; cleared when an answer is submitted.
    // Reading this from the SSE loop lets us resend the prompt if the browser
    // reconnects mid-investigation.
    public HumanDataRequest? PendingDataRequest { get; set; }

    // Once the agent finishes we keep the final markdown around so a late
    // reconnect can still render the report.
    public string? FinalReportMarkdown { get; private set; }
    public bool IsComplete { get; private set; }

    public InvestigationSession(string id) { Id = id; }

    public void Complete(string? markdown)
    {
        FinalReportMarkdown = markdown;
        IsComplete = true;
        AgentEvents.Writer.TryComplete();
    }
}

/// <summary>
/// Shape of everything we push to the browser over SSE. Kept as a small
/// discriminated-union-like record with a Kind field so the JS side can
/// switch on it.
/// </summary>
public sealed record SessionEvent(string Kind, object? Data);

/// <summary>
/// Process-wide registry of in-flight investigations. Entries are created by
/// POST /api/investigate and looked up by the SSE endpoint + paste POST. We
/// prune completed sessions opportunistically so memory stays bounded.
/// </summary>
public sealed class SessionRegistry
{
    private readonly ConcurrentDictionary<string, InvestigationSession> _sessions = new();

    public InvestigationSession Create()
    {
        // Short id is enough — this is a local dev tool, not a public service.
        var id = Guid.NewGuid().ToString("N")[..8];
        var s = new InvestigationSession(id);
        _sessions[id] = s;
        PruneOld();
        return s;
    }

    public InvestigationSession? Get(string id) =>
        _sessions.TryGetValue(id, out var s) ? s : null;

    /// <summary>
    /// Drop completed sessions older than 30 min. Opportunistic: runs inside
    /// Create() so we don't need a background timer. Good enough for local
    /// dev; a production service would do this on a timer.
    /// </summary>
    private void PruneOld()
    {
        if (_sessions.Count < 32) return;
        foreach (var kv in _sessions)
        {
            if (kv.Value.IsComplete) _sessions.TryRemove(kv.Key, out _);
        }
    }
}

/// <summary>
/// The IHumanDataProvider the agent sees. Instead of blocking on Console.ReadLine,
/// it parks on the session's PasteResponses channel. The browser POSTs an answer,
/// which writes to that channel; ReadAsync returns and the agent continues.
///
/// Null from the browser maps to "engineer declined" — same semantic as the
/// CLI provider, so the tool behaviour is identical either way.
/// </summary>
public sealed class WebHumanDataProvider : IHumanDataProvider
{
    private readonly InvestigationSession _session;

    public WebHumanDataProvider(InvestigationSession session)
    {
        _session = session;
    }

    public async Task<string?> RequestDataAsync(HumanDataRequest request, CancellationToken ct)
    {
        // Record the pending request so the browser (including a late reconnect)
        // knows to show the paste box.
        _session.PendingDataRequest = request;
        await _session.AgentEvents.Writer.WriteAsync(
            new SessionEvent("paste_request", new
            {
                sourceName = request.SourceName,
                renderedQuery = request.RenderedQuery,
                reason = request.Reason,
                suggestedEnv = request.SuggestedEnv,
            }),
            ct);

        // Block until the browser submits. The POST handler writes a null
        // for 'skip', an empty string for 'empty', or the pasted text.
        var response = await _session.PasteResponses.Reader.ReadAsync(ct);

        _session.PendingDataRequest = null;
        await _session.AgentEvents.Writer.WriteAsync(
            new SessionEvent("paste_received", new { length = response?.Length ?? -1 }),
            ct);

        return response;
    }
}

/// <summary>
/// Kicks off the investigation on a background Task and wires the session up.
/// Mirrors the CLI Program.cs flow — same tools, same configs, same agent —
/// but with the web provider in place of the console one.
/// </summary>
public static class InvestigationLauncher
{
    public static Task StartAsync(
        InvestigationSession session,
        InvestigateRequest request,
        IConfiguration config)
    {
        // Fire-and-forget — the session object is the handle. Swallowing
        // exceptions into the event stream keeps the browser informed without
        // tearing down the server.
        return Task.Run(async () =>
        {
            try
            {
                var openaiKey = config["OPENAI_API_KEY"]
                    ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
                if (string.IsNullOrWhiteSpace(openaiKey))
                {
                    await session.AgentEvents.Writer.WriteAsync(new SessionEvent("error",
                        new { message = "OPENAI_API_KEY is not set on the server." }));
                    session.Complete(null);
                    return;
                }

                // Build the tool registry. Identical to CLI except for the
                // human provider.
                var hypothesisChannel = Channel.CreateUnbounded<(string, string)>();
                var humanProvider = new WebHumanDataProvider(session);

                IDebugTool logTool = request.Mock
                    ? new MockLogSearchTool()
                    : new CloudWatchLogSearchTool(
                        retriever: new HybridLogRetriever(
                            new KeywordLogRetriever(),
                            new SemanticLogRetriever(openaiKey)),
                        defaultRegion: request.Region ?? "ap-southeast-2");

                var registry = new ToolRegistry(new[]
                {
                    logTool,
                    new RequestMongoQueryTool(humanProvider),
                    new RequestOpenSearchQueryTool(humanProvider),
                    new RequestKafkaEventsTool(humanProvider),
                    new RecordHypothesisTool(hypothesisChannel),
                    new FinishInvestigationTool(),
                });

                var agent = new InvestigatorAgent(registry, openaiKey);

                var bugReport = new BugReport(
                    Description: request.Description,
                    TicketId: string.IsNullOrWhiteSpace(request.TicketId) ? null : request.TicketId,
                    TicketSource: string.IsNullOrWhiteSpace(request.TicketId) ? null : "Jira",
                    ReportedAt: DateTimeOffset.UtcNow);

                var investigation = await agent.InvestigateAsync(
                    bugReport,
                    config: new AgentConfig(MaxIterations: 12),
                    hypothesisChannel: hypothesisChannel,
                    onEvent: ev => ForwardEventToSse(session, ev),
                    ct: CancellationToken.None);

                var markdown = ReportWriter.Render(investigation);
                await session.AgentEvents.Writer.WriteAsync(new SessionEvent("completed",
                    new { markdown, status = investigation.Status.ToString() }));
                session.Complete(markdown);

                // Persist the report alongside what the CLI would have written
                // so the user can still find reports in the usual place.
                try
                {
                    Directory.CreateDirectory("investigations");
                    var slug = (bugReport.TicketId ?? investigation.Id.ToString("N")[..8])
                        .Replace("/", "_").Replace("\\", "_");
                    var path = Path.Combine("investigations",
                        $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{slug}.md");
                    await File.WriteAllTextAsync(path, markdown);
                }
                catch { /* don't fail the session if disk is unwritable */ }

                if (logTool is IDisposable d) d.Dispose();
            }
            catch (Exception ex)
            {
                await session.AgentEvents.Writer.WriteAsync(new SessionEvent("error",
                    new { message = ex.Message }));
                session.Complete(null);
            }
        });
    }

    /// <summary>
    /// Convert every InvestigationEvent into a compact JSON shape the browser
    /// can render directly. Keeping this one-way-translation in the server
    /// (rather than sending raw record types) means the JS layer never needs
    /// to know about C# type discriminators.
    /// </summary>
    private static void ForwardEventToSse(InvestigationSession session, InvestigationEvent ev)
    {
        SessionEvent payload = ev switch
        {
            ModelCallEvent mc => new SessionEvent("model_call",
                new { iteration = mc.Iteration, messageCount = mc.PromptMessageCount }),
            ModelResponseEvent mr => new SessionEvent("model_response",
                new { iteration = mr.Iteration, text = mr.Text, outputTokens = mr.OutputTokens }),
            ToolCallEvent tc => new SessionEvent("tool_call",
                new { iteration = tc.Iteration, toolName = tc.ToolName,
                      input = JsonDocument.Parse(tc.Input.GetRawText()).RootElement }),
            ToolResultEvent tr => new SessionEvent("tool_result",
                new { iteration = tr.Iteration, output = tr.Output, isError = tr.IsError }),
            HypothesisEvent h => new SessionEvent("hypothesis",
                new { iteration = h.Iteration, hypothesis = h.Hypothesis, reasoning = h.Reasoning }),
            ErrorEvent e => new SessionEvent("error",
                new { iteration = e.Iteration, message = e.Message }),
            _ => new SessionEvent("unknown", null),
        };
        session.AgentEvents.Writer.TryWrite(payload);
    }
}

/// <summary>
/// Payload the browser POSTs to start an investigation. Mirrors the CLI flags
/// a user would pass to `debugger investigate`.
/// </summary>
public sealed record InvestigateRequest(
    string Description,
    string? TicketId = null,
    bool Mock = false,
    string? Region = null);

/// <summary>
/// Payload for a paste-box submission. Mode is one of: "paste", "empty", "skip".
/// The server translates these into the null/empty/string convention the
/// IHumanDataProvider contract expects.
/// </summary>
public sealed record PasteSubmission(string Mode, string? Text = null);
