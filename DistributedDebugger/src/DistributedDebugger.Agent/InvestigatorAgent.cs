using System.ClientModel;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DistributedDebugger.Core.Models;
using DistributedDebugger.Core.Tools;
using OpenAI.Chat;

namespace DistributedDebugger.Agent;

/// <summary>
/// Agent configuration — the system prompt, model, and which tools are available
/// for a given investigation run. Analogous to HarnessArena's AgentConfig, but
/// simpler since we only have one "contestant" profile per investigation.
/// </summary>
public sealed record AgentConfig(
    string Model = "gpt-4o-mini",
    int MaxIterations = 12,
    double Temperature = 0.0,
    string? SystemPromptOverride = null
);

/// <summary>
/// The investigator agent. Given a <see cref="BugReport"/> and a tool registry,
/// it runs a ReAct loop:
///
///   reason → call a tool (logs/events/hypothesis) → read result → repeat
///     until finish_investigation is called or MaxIterations is hit.
///
/// This class is intentionally single-provider (OpenAI) for Phase 1. Swapping
/// in Claude or a mock provider would mean introducing an IInvestigatorAgent
/// interface like HarnessArena does — deferred until we actually need it.
/// </summary>
public sealed class InvestigatorAgent
{
    private readonly IToolRegistry _tools;
    private readonly string _apiKey;

    public InvestigatorAgent(IToolRegistry tools, string apiKey)
    {
        _tools = tools;
        _apiKey = apiKey;
    }

    public async Task<Investigation> InvestigateAsync(
        BugReport report,
        AgentConfig? config = null,
        Channel<(string, string)>? hypothesisChannel = null,
        Action<InvestigationEvent>? onEvent = null,
        CancellationToken ct = default)
    {
        config ??= new AgentConfig();

        var id = Guid.NewGuid();
        var started = DateTimeOffset.UtcNow;
        var trace = new List<InvestigationEvent>();
        var status = InvestigationStatus.Running;
        RootCauseReport? rootCause = null;
        int totalIn = 0, totalOut = 0;
        int iterationsUsed = 0;

        void Emit(InvestigationEvent ev)
        {
            trace.Add(ev);
            onEvent?.Invoke(ev);
        }

        try
        {
            var client = new ChatClient(model: config.Model, apiKey: _apiKey);
            var toolDefs = _tools.All.Select(ToOpenAITool).ToList();

            var systemPrompt = config.SystemPromptOverride ?? BuildSystemPrompt(report);

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(systemPrompt),
                new UserChatMessage(BuildInitialUserMessage(report)),
            };

            var options = new ChatCompletionOptions
            {
                Temperature = (float)config.Temperature,
            };
            foreach (var def in toolDefs)
            {
                options.Tools.Add(def);
            }

            for (int iter = 1; iter <= config.MaxIterations; iter++)
            {
                ct.ThrowIfCancellationRequested();
                iterationsUsed = iter;

                Emit(new ModelCallEvent(DateTimeOffset.UtcNow, iter, messages.Count));

                ClientResult<ChatCompletion> result =
                    await client.CompleteChatAsync(messages, options, ct);
                ChatCompletion completion = result.Value;

                totalIn += completion.Usage?.InputTokenCount ?? 0;
                totalOut += completion.Usage?.OutputTokenCount ?? 0;

                var assistantText = completion.Content.Count > 0
                    ? completion.Content[0].Text
                    : null;

                Emit(new ModelResponseEvent(
                    DateTimeOffset.UtcNow, iter,
                    assistantText,
                    completion.Usage?.OutputTokenCount ?? 0,
                    completion.FinishReason.ToString()));

                messages.Add(new AssistantChatMessage(completion));

                if (completion.ToolCalls.Count == 0)
                {
                    // Model stopped without calling finish — treat trailing text
                    // as a "best guess" and wrap it in a Low-confidence report.
                    // This keeps the pipeline useful even when the model gives up.
                    status = InvestigationStatus.Completed;
                    rootCause = FallbackReport(assistantText);
                    break;
                }

                bool finishedViaTool = false;
                foreach (var call in completion.ToolCalls)
                {
                    ct.ThrowIfCancellationRequested();

                    var inputJson = JsonDocument
                        .Parse(call.FunctionArguments).RootElement.Clone();

                    Emit(new ToolCallEvent(
                        DateTimeOffset.UtcNow, iter, call.Id, call.FunctionName, inputJson));

                    if (call.FunctionName == "finish_investigation")
                    {
                        rootCause = ParseRootCause(inputJson);
                        status = InvestigationStatus.Completed;
                        finishedViaTool = true;
                        break;
                    }

                    IDebugTool tool;
                    try
                    {
                        tool = _tools.Get(call.FunctionName);
                    }
                    catch (KeyNotFoundException)
                    {
                        var msg = $"Unknown tool: {call.FunctionName}";
                        Emit(new ToolResultEvent(
                            DateTimeOffset.UtcNow, iter, call.Id, msg, IsError: true));
                        messages.Add(new ToolChatMessage(call.Id, msg));
                        continue;
                    }

                    ToolExecutionResult toolResult;
                    try
                    {
                        toolResult = await tool.ExecuteAsync(inputJson, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        toolResult = new ToolExecutionResult(
                            $"Tool threw: {ex.Message}", IsError: true);
                    }

                    Emit(new ToolResultEvent(
                        DateTimeOffset.UtcNow, iter, call.Id,
                        toolResult.Output, toolResult.IsError));
                    messages.Add(new ToolChatMessage(call.Id, toolResult.Output));

                    // If this was a hypothesis, drain it from the channel and
                    // emit a HypothesisEvent. Non-blocking — if nothing's queued,
                    // we simply don't emit, which is fine.
                    if (call.FunctionName == "record_hypothesis" && hypothesisChannel != null)
                    {
                        while (hypothesisChannel.Reader.TryRead(out var h))
                        {
                            Emit(new HypothesisEvent(
                                DateTimeOffset.UtcNow, iter, h.Item1, h.Item2));
                        }
                    }
                }

                if (finishedViaTool) break;
            }

            if (status == InvestigationStatus.Running)
            {
                status = InvestigationStatus.MaxIterationsHit;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            status = InvestigationStatus.Error;
            Emit(new ErrorEvent(
                DateTimeOffset.UtcNow, iterationsUsed, ex.Message, ex.ToString()));
        }

        var finished = DateTimeOffset.UtcNow;
        return new Investigation(
            Id: id,
            Report: report,
            StartedAt: started,
            FinishedAt: finished,
            Status: status,
            RootCause: rootCause,
            Trace: trace,
            Usage: new InvestigationUsage(totalIn, totalOut, iterationsUsed, finished - started));
    }

    private static string BuildSystemPrompt(BugReport report) => """
        You are a distributed systems debugger investigating a bug in a microservices
        platform (content services backed by MongoDB, OpenSearch, and Kafka).

        Your job: find the root cause by gathering evidence from available tools.
        Follow this approach:

        1. Read the bug description carefully. Identify key facts: affected entity IDs,
           rough timestamps, symptoms, which user or feature is impacted.
        2. Form a hypothesis early and record it with record_hypothesis. It's fine
           to be wrong — record a new one when evidence contradicts the old one.
        3. Gather evidence using search_logs and fetch_kafka_events. Be targeted:
           narrow by service + keyword, or by entity id. Do NOT dump broad queries.
        4. Look for what's MISSING as much as what's present. A missing event or log
           line is often the key to the bug.
        5. Once you have enough evidence to explain the symptom, call
           finish_investigation with a structured report.

        Be concise in your reasoning. Prefer specific, cited evidence over speculation.
        If you can't determine the root cause with confidence, say so — set confidence
        to Low and list what additional evidence would help.
        """;

    private static string BuildInitialUserMessage(BugReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Bug report:");
        sb.AppendLine();
        sb.AppendLine(report.Description);

        if (!string.IsNullOrWhiteSpace(report.TicketId))
        {
            sb.AppendLine();
            sb.AppendLine($"Source ticket: {report.TicketId}" +
                          (report.TicketSource is null ? "" : $" ({report.TicketSource})"));
        }

        if (report.ReportedAt.HasValue)
        {
            sb.AppendLine();
            sb.AppendLine($"Reported at: {report.ReportedAt.Value:yyyy-MM-dd HH:mm} UTC");
        }

        sb.AppendLine();
        sb.AppendLine("Begin your investigation. Start by identifying key entities " +
                      "(IDs, timestamps) from the description.");

        return sb.ToString();
    }

    /// <summary>
    /// Parse the finish_investigation tool call arguments into a strongly-typed
    /// RootCauseReport. Tolerant to missing optional fields.
    /// </summary>
    private static RootCauseReport ParseRootCause(JsonElement input)
    {
        string Get(string key) =>
            input.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? ""
                : "";

        IReadOnlyList<string> GetArray(string key) =>
            input.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray()
                   .Select(e => e.GetString() ?? "")
                   .Where(s => !string.IsNullOrWhiteSpace(s))
                   .ToList()
                : new List<string>();

        ConfidenceLevel confidence = ConfidenceLevel.Low;
        if (input.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.String)
        {
            Enum.TryParse(c.GetString(), ignoreCase: true, out confidence);
        }

        return new RootCauseReport(
            Summary: Get("summary"),
            LikelyCause: Get("likelyCause"),
            AffectedServices: GetArray("affectedServices"),
            Evidence: GetArray("evidence"),
            SuggestedFix: string.IsNullOrWhiteSpace(Get("suggestedFix")) ? null : Get("suggestedFix"),
            Confidence: confidence);
    }

    private static RootCauseReport FallbackReport(string? trailingText) =>
        new(
            Summary: "Investigation ended without an explicit root cause call.",
            LikelyCause: trailingText ?? "(no reasoning produced)",
            AffectedServices: Array.Empty<string>(),
            Evidence: new[] { "Agent did not call finish_investigation; this is a fallback." },
            SuggestedFix: null,
            Confidence: ConfidenceLevel.Low);

    private static ChatTool ToOpenAITool(IDebugTool tool) =>
        ChatTool.CreateFunctionTool(
            functionName: tool.Name,
            functionDescription: tool.Description,
            functionParameters: BinaryData.FromString(tool.InputSchema.GetRawText()));
}
