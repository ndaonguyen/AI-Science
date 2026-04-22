using System.Threading.Channels;
using DistributedDebugger.Agent;
using DistributedDebugger.Core.Models;
using DistributedDebugger.Core.Tools;
using DistributedDebugger.Tools;
using DistributedDebugger.Tools.CloudWatch;
using DistributedDebugger.Tools.HumanLoop;
using OpenAI.Chat;

namespace DistributedDebugger.Web;

/// <summary>
/// A guided-mode session. Parallel in shape to <see cref="InvestigationSession"/>
/// but with a key difference: the agent does NOT run autonomously. Each
/// /api/step POST from the browser triggers exactly one turn.
///
/// The session holds:
///   - The OpenAI chat history across turns (so the LLM remembers prior
///     findings without us having to re-serialise them).
///   - The agent + tool registry wired once at session start.
///   - An SSE event stream the browser subscribes to for live updates.
///
/// The human-loop tools (request_mongo_query etc.) still work the same way —
/// the UI shows a paste panel, the user pastes, the tool unblocks. That's why
/// we keep the same Channel-based WebHumanDataProvider from the autonomous
/// path and just reuse it here.
/// </summary>
public sealed class GuidedSession
{
    public string Id { get; }
    public Channel<SessionEvent> AgentEvents { get; } = Channel.CreateUnbounded<SessionEvent>();
    public Channel<string?> PasteResponses { get; } = Channel.CreateUnbounded<string?>();

    public HumanDataRequest? PendingDataRequest { get; set; }
    public string? FinalReportMarkdown { get; private set; }
    public bool IsComplete { get; private set; }

    // Per-session agent wiring — created once at session start so the tool
    // registry (and its disposable resources like AWS clients) live for the
    // whole conversation, not per turn.
    public GuidedAgent Agent { get; }
    public IToolRegistry ToolRegistry { get; }
    public List<ChatMessage> History { get; }
    public BugReport Report { get; }

    // Mutex-ish: only one turn may run at a time. Guarded via a simple bool
    // plus a lock inside the launcher. Prevents a fast double-click from
    // racing two concurrent model calls against the same history.
    public bool TurnInProgress { get; set; }

    public GuidedSession(
        string id,
        GuidedAgent agent,
        IToolRegistry toolRegistry,
        List<ChatMessage> history,
        BugReport report)
    {
        Id = id;
        Agent = agent;
        ToolRegistry = toolRegistry;
        History = history;
        Report = report;
    }

    public void Complete(string? markdown)
    {
        FinalReportMarkdown = markdown;
        IsComplete = true;
        AgentEvents.Writer.TryComplete();
    }
}

/// <summary>
/// Registry for guided sessions. Intentionally separate from the autonomous
/// registry so the two modes don't accidentally pick up each other's
/// sessions — the id spaces are independent.
/// </summary>
public sealed class GuidedSessionRegistry
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, GuidedSession> _sessions = new();

    public GuidedSession Register(GuidedSession session)
    {
        _sessions[session.Id] = session;
        PruneOld();
        return session;
    }

    public GuidedSession? Get(string id) =>
        _sessions.TryGetValue(id, out var s) ? s : null;

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
/// Creates and initialises a new guided session but does NOT run any turn.
/// First turn is driven by the browser's first /api/step POST.
/// </summary>
public static class GuidedSessionLauncher
{
    public static GuidedSession Create(
        GuidedSessionRegistry registry,
        GuidedStartRequest request,
        IConfiguration config)
    {
        var openaiKey = config["OPENAI_API_KEY"]
            ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            ?? throw new InvalidOperationException("OPENAI_API_KEY is not set on the server.");

        var id = Guid.NewGuid().ToString("N")[..8];

        // Build a session-local tool registry. The web human provider is
        // bound to this session, so paste_requests route correctly.
        // Create a placeholder; we'll overwrite once the session object exists.
        GuidedSession? pendingSession = null;
        var humanProvider = new LazyWebHumanDataProvider(() => pendingSession!);

        IDebugTool logTool = request.Mock
            ? new MockLogSearchTool()
            : new CloudWatchLogSearchTool(
                retriever: new HybridLogRetriever(
                    new KeywordLogRetriever(),
                    new SemanticLogRetriever(openaiKey)),
                defaultRegion: request.Region ?? "ap-southeast-2");

        var toolRegistry = new ToolRegistry(new[]
        {
            logTool,
            new RequestMongoQueryTool(humanProvider),
            new RequestOpenSearchQueryTool(humanProvider),
            new RequestKafkaEventsTool(humanProvider),
            new FinishInvestigationTool(),
        });

        var bugReport = new BugReport(
            Description: request.Description,
            TicketId: string.IsNullOrWhiteSpace(request.TicketId) ? null : request.TicketId,
            TicketSource: string.IsNullOrWhiteSpace(request.TicketId) ? null : "Jira",
            ReportedAt: DateTimeOffset.UtcNow);

        var history = GuidedAgent.BuildInitialHistory(bugReport);
        var agent = new GuidedAgent(toolRegistry, openaiKey);

        var session = new GuidedSession(id, agent, toolRegistry, history, bugReport);
        pendingSession = session;
        registry.Register(session);
        return session;
    }
}

/// <summary>
/// Wraps a GuidedSession reference that's resolved lazily — needed because
/// the session and the human provider have a circular dependency: the
/// provider needs the session to route paste prompts, but the session wants
/// the tool registry (which already contains the provider) at construction.
/// Resolved via a factory closure.
/// </summary>
internal sealed class LazyWebHumanDataProvider : IHumanDataProvider
{
    private readonly Func<GuidedSession> _get;
    public LazyWebHumanDataProvider(Func<GuidedSession> get) { _get = get; }

    public async Task<string?> RequestDataAsync(HumanDataRequest request, CancellationToken ct)
    {
        var session = _get();
        session.PendingDataRequest = request;
        await session.AgentEvents.Writer.WriteAsync(new SessionEvent("paste_request", new
        {
            sourceName = request.SourceName,
            renderedQuery = request.RenderedQuery,
            reason = request.Reason,
            suggestedEnv = request.SuggestedEnv,
        }), ct);

        var response = await session.PasteResponses.Reader.ReadAsync(ct);

        session.PendingDataRequest = null;
        await session.AgentEvents.Writer.WriteAsync(
            new SessionEvent("paste_received", new { length = response?.Length ?? -1 }),
            ct);
        return response;
    }
}

/// <summary>
/// Browser payload to start a guided session. Identical in shape to the
/// autonomous InvestigateRequest — the only real difference is which
/// endpoint the browser hits and which mode flag is set.
/// </summary>
public sealed record GuidedStartRequest(
    string Description,
    string? TicketId = null,
    bool Mock = false,
    string? Region = null);

/// <summary>
/// Browser payload to advance a guided session by one turn. The action
/// string drives the system prompt the agent sees for this turn.
/// </summary>
public sealed record GuidedStepRequest(string Action, GuidedStepContext? Context = null);

/// <summary>
/// Extra context specific to some actions:
///   - For "search_logs" (initial): the services + environment to search.
///   - For "more_logs": same fields, for additional searches.
///   - Other actions don't need context; the agent knows what to check from the
///     conversation history.
/// </summary>
public sealed record GuidedStepContext(
    IReadOnlyList<string>? Services = null,
    string? Environment = null,
    string? FreeText = null,
    string? StartTime = null,
    string? EndTime = null);
