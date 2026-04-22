using System.ClientModel;
using System.Text;
using System.Text.Json;
using DistributedDebugger.Core.Models;
using DistributedDebugger.Core.Tools;
using OpenAI.Chat;

namespace DistributedDebugger.Agent;

/// <summary>
/// A step-by-step variant of the ReAct agent. Instead of looping until
/// finish_investigation is called (the autonomous mode), the guided agent
/// runs exactly ONE "turn" per <see cref="RunStepAsync"/> call, where a turn
/// is: send message to LLM, possibly execute 1+ tool calls in response, then
/// ask the LLM to produce a structured summary, then stop.
///
/// The caller (the web UI) drives the loop: it tells the agent what to do
/// next ("search logs in these services", "check mongo for this id",
/// "finalise"), the agent does it, returns a summary, and waits. This gives
/// the human full control at every step.
///
/// Why a separate class instead of adding a flag to InvestigatorAgent:
/// the two modes have genuinely different contracts (autonomous returns a
/// finished Run; guided returns one incremental step). Trying to stuff both
/// behaviours into one class would make each one harder to reason about.
/// </summary>
public sealed class GuidedAgent
{
    private readonly IToolRegistry _tools;
    private readonly string _apiKey;
    private readonly string _model;

    public GuidedAgent(
        IToolRegistry tools,
        string apiKey,
        string model = "gpt-4o-mini")
    {
        _tools = tools;
        _apiKey = apiKey;
        _model = model;
    }

    /// <summary>
    /// Run one turn. Caller supplies the accumulated message history and the
    /// human's next instruction; we append the instruction, call the model,
    /// execute any tool calls it emits, request a structured summary, and
    /// return everything the UI needs to render + the updated history to
    /// feed back next time.
    /// </summary>
    public async Task<GuidedStepResult> RunStepAsync(
        IList<ChatMessage> history,
        string humanInstruction,
        Action<InvestigationEvent>? onEvent = null,
        CancellationToken ct = default)
    {
        var toolDefs = _tools.All.Select(ToOpenAITool).ToList();
        var client = new ChatClient(model: _model, apiKey: _apiKey);

        var trace = new List<InvestigationEvent>();
        void Emit(InvestigationEvent ev)
        {
            trace.Add(ev);
            onEvent?.Invoke(ev);
        }

        // 1. Append the human's instruction as a user message.
        history.Add(new UserChatMessage(humanInstruction));

        // 2. Ask the model for a response. It will probably emit tool calls.
        var options = new ChatCompletionOptions { Temperature = 0f };
        foreach (var def in toolDefs) options.Tools.Add(def);

        var iter = CountIterations(history);
        Emit(new ModelCallEvent(DateTimeOffset.UtcNow, iter, history.Count));
        ClientResult<ChatCompletion> first = await client.CompleteChatAsync(history, options, ct);
        var response = first.Value;

        int totalIn = response.Usage?.InputTokenCount ?? 0;
        int totalOut = response.Usage?.OutputTokenCount ?? 0;

        var assistantText = response.Content.Count > 0 ? response.Content[0].Text : null;
        Emit(new ModelResponseEvent(DateTimeOffset.UtcNow, iter, assistantText,
            response.Usage?.OutputTokenCount ?? 0,
            response.FinishReason.ToString()));

        // Echo the assistant message back so tool results can be attached.
        history.Add(new AssistantChatMessage(response));

        // 3. Execute any tool calls and append each result back to history.
        //    Multiple related calls are allowed in a single turn — the LLM
        //    decides the bundling within the system prompt's constraints.
        foreach (var call in response.ToolCalls)
        {
            ct.ThrowIfCancellationRequested();

            var inputJson = JsonDocument.Parse(call.FunctionArguments).RootElement.Clone();
            Emit(new ToolCallEvent(DateTimeOffset.UtcNow, iter, call.Id, call.FunctionName, inputJson));

            var toolResult = await ExecuteToolAsync(call.FunctionName, inputJson, ct);
            Emit(new ToolResultEvent(DateTimeOffset.UtcNow, iter, call.Id,
                toolResult.Output, toolResult.IsError));

            history.Add(new ToolChatMessage(call.Id, toolResult.Output));
        }

        // 4. Ask for a structured summary of this turn. We use a separate
        //    model call (without tools available) so the LLM focuses purely
        //    on synthesising findings from whatever evidence it just gathered.
        //    Temperature 0 + JSON object response format keeps the shape stable.
        var summaryOptions = new ChatCompletionOptions { Temperature = 0f };

        var summaryPrompt =
            "Based on the tool results you just received (if any), produce a JSON object " +
            "matching this exact schema:\n" +
            "{\n" +
            "  \"findings\": [string, ...],     // 2-6 concise bullet points of what the evidence shows\n" +
            "  \"hypothesis\": string,          // your current best theory in one sentence\n" +
            "  \"suggestedNext\": string         // one of: \"mongo\", \"opensearch\", \"kafka\", \"more_logs\", \"finish\"\n" +
            "}\n" +
            "Return ONLY the JSON, no prose. If this turn produced no useful evidence, say so " +
            "in findings and suggest searching more logs.";
        history.Add(new UserChatMessage(summaryPrompt));

        Emit(new ModelCallEvent(DateTimeOffset.UtcNow, iter + 1, history.Count));
        var second = await client.CompleteChatAsync(history, summaryOptions, ct);
        var summaryText = second.Value.Content.Count > 0 ? second.Value.Content[0].Text ?? "{}" : "{}";
        totalIn += second.Value.Usage?.InputTokenCount ?? 0;
        totalOut += second.Value.Usage?.OutputTokenCount ?? 0;
        Emit(new ModelResponseEvent(DateTimeOffset.UtcNow, iter + 1, summaryText,
            second.Value.Usage?.OutputTokenCount ?? 0,
            second.Value.FinishReason.ToString()));
        history.Add(new AssistantChatMessage(second.Value));

        var summary = ParseSummary(summaryText);

        return new GuidedStepResult(
            Findings: summary.Findings,
            Hypothesis: summary.Hypothesis,
            SuggestedNext: summary.SuggestedNext,
            Trace: trace,
            InputTokens: totalIn,
            OutputTokens: totalOut);
    }

    /// <summary>
    /// The first message in a brand-new guided investigation: seeds the
    /// history with the system prompt + the bug report. Separate from the
    /// running constructor so the web layer can create a session and hold
    /// the history without immediately running a turn.
    /// </summary>
    public static List<ChatMessage> BuildInitialHistory(BugReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine(SystemPrompt);

        var history = new List<ChatMessage>
        {
            new SystemChatMessage(sb.ToString()),
            new UserChatMessage(BuildInitialUserMessage(report)),
        };
        return history;
    }

    private const string SystemPrompt = """
        You are a distributed systems debugger investigating a bug in EP's CoCo
        platform (content microservices backed by MongoDB, OpenSearch, and Kafka,
        running on AWS ECS).

        You are in GUIDED MODE: the human engineer drives the investigation and
        tells you exactly what to do next. Do ONLY what they ask — do not
        free-roam or add extra tool calls they didn't request. You MAY bundle
        obviously-related calls together (e.g. if they say "search these 3
        services", that's 3 tool calls in one turn). Never call more than one
        kind of tool per turn.

        After executing tools, you will be asked to produce a structured JSON
        summary. Keep findings concise and grounded in the evidence — do not
        speculate beyond what the tools showed. If the evidence is empty or
        contradictory, say so.

        For suggestedNext, pick the single most promising option from:
          - "mongo":       check document state in MongoDB
          - "opensearch":  check indexed state in OpenSearch
          - "kafka":       check whether an event was (or wasn't) emitted
          - "more_logs":   search additional services or keywords in CloudWatch
          - "finish":      you have enough evidence for a confident root cause

        The human may follow your suggestion or override it — that's fine.
        """;

    private static string BuildInitialUserMessage(BugReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("I'm going to investigate a bug with you step by step.");
        sb.AppendLine();
        sb.AppendLine("Bug description:");
        sb.AppendLine(report.Description);
        if (!string.IsNullOrWhiteSpace(report.TicketId))
        {
            sb.AppendLine();
            sb.AppendLine($"Ticket: {report.TicketId}");
        }
        sb.AppendLine();
        sb.AppendLine("Wait for my instructions before doing anything. I'll tell you which services to search, which data sources to check, and when to finish.");
        return sb.ToString();
    }

    private async Task<ToolExecutionResult> ExecuteToolAsync(
        string name, JsonElement input, CancellationToken ct)
    {
        IDebugTool tool;
        try
        {
            tool = _tools.Get(name);
        }
        catch (KeyNotFoundException)
        {
            return new ToolExecutionResult($"Unknown tool: {name}", IsError: true);
        }

        try
        {
            return await tool.ExecuteAsync(input, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ToolExecutionResult($"Tool threw: {ex.Message}", IsError: true);
        }
    }

    /// <summary>
    /// Rough iteration count — we treat every user-role message after the
    /// initial bug description as a new iteration. Used only for trace event
    /// labelling in the UI; no functional dependency.
    /// </summary>
    private static int CountIterations(IList<ChatMessage> history) =>
        history.OfType<UserChatMessage>().Count();

    private static ChatTool ToOpenAITool(IDebugTool tool) =>
        ChatTool.CreateFunctionTool(
            functionName: tool.Name,
            functionDescription: tool.Description,
            functionParameters: BinaryData.FromString(tool.InputSchema.GetRawText()));

    // ---- summary parsing ----

    private static SummaryShape ParseSummary(string raw)
    {
        // Tolerant of markdown fences and preamble, same approach as
        // JudgeResponseParser in the Eval project. The model doesn't always
        // respect "JSON only" instructions.
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("```"))
        {
            var nl = trimmed.IndexOf('\n');
            if (nl > 0) trimmed = trimmed[(nl + 1)..];
            if (trimmed.EndsWith("```")) trimmed = trimmed[..^3];
        }
        var firstBrace = trimmed.IndexOf('{');
        var lastBrace = trimmed.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            trimmed = trimmed[firstBrace..(lastBrace + 1)];
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;

            var findings = root.TryGetProperty("findings", out var f) && f.ValueKind == JsonValueKind.Array
                ? f.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList()
                : new List<string> { "(no findings returned)" };

            var hypothesis = root.TryGetProperty("hypothesis", out var h) ? h.GetString() ?? "" : "";
            var suggested = root.TryGetProperty("suggestedNext", out var s) ? s.GetString() ?? "more_logs" : "more_logs";

            return new SummaryShape(findings, hypothesis, suggested);
        }
        catch
        {
            // If the model produced something unparseable, we return a
            // degraded-but-useful summary so the user can still continue.
            return new SummaryShape(
                Findings: new[] { "(summary parse failed — the model returned invalid JSON)", $"raw: {raw[..Math.Min(raw.Length, 200)]}" },
                Hypothesis: "(parse failed)",
                SuggestedNext: "more_logs");
        }
    }

    private sealed record SummaryShape(
        IReadOnlyList<string> Findings,
        string Hypothesis,
        string SuggestedNext);
}

/// <summary>
/// Everything the UI needs to render one completed guided turn.
/// Trace is included so the web event feed can stream individual tool calls
/// as they happen (or replay them on reconnect).
/// </summary>
public sealed record GuidedStepResult(
    IReadOnlyList<string> Findings,
    string Hypothesis,
    string SuggestedNext,
    IReadOnlyList<InvestigationEvent> Trace,
    int InputTokens,
    int OutputTokens);
