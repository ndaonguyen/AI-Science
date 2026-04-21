using System.ClientModel;
using System.Text.Json;
using HarnessArena.Core.Models;
using HarnessArena.Core.Tools;
using OpenAI.Chat;

namespace HarnessArena.Agents;

/// <summary>
/// Agent that talks to OpenAI via the official OpenAI .NET SDK.
///
/// This is the same ReAct loop as the mock, but with real model calls and real
/// tool-use payloads. The shape is slightly different from Anthropic:
///
///   - Assistant messages carry a ToolCalls collection (not ToolUseBlocks).
///   - Tool results go in a ToolChatMessage (role "tool"), NOT a user message.
///   - Tool inputs/outputs are strings (JSON), not typed blocks.
///
/// Everything else — the loop, the tool registry, the trace events — is identical.
/// That's the payoff of a provider-agnostic IAgent: swapping providers touches
/// exactly one file.
/// </summary>
public sealed class OpenAIAgent : IAgent
{
    private readonly IToolRegistry _tools;
    private readonly string _apiKey;

    public OpenAIAgent(IToolRegistry tools, string apiKey)
    {
        _tools = tools;
        _apiKey = apiKey;
    }

    public async Task<Run> RunAsync(
        TaskDefinition task,
        AgentConfig config,
        CancellationToken ct)
    {
        var runId = Guid.NewGuid();
        var started = DateTimeOffset.UtcNow;
        var trace = new List<TraceEvent>();
        var status = RunStatus.Running;
        string? finalAnswer = null;
        int totalIn = 0, totalOut = 0;
        int iterationsUsed = 0;

        try
        {
            var client = new ChatClient(model: config.Model, apiKey: _apiKey);
            var toolsForThisRun = _tools.GetMany(config.ToolNames).ToList();
            var toolDefs = toolsForThisRun.Select(ToOpenAITool).ToList();

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(config.SystemPrompt),
                new UserChatMessage(task.Prompt),
            };

            var options = new ChatCompletionOptions
            {
                Temperature = (float)config.Temperature,
            };
            foreach (var def in toolDefs)
            {
                options.Tools.Add(def);
            }

            var maxIter = Math.Min(task.MaxIterations, config.MaxIterations);

            for (int iter = 1; iter <= maxIter; iter++)
            {
                ct.ThrowIfCancellationRequested();
                iterationsUsed = iter;

                trace.Add(new ModelCallEvent(DateTimeOffset.UtcNow, iter, messages.Count));

                ClientResult<ChatCompletion> result =
                    await client.CompleteChatAsync(messages, options, ct);
                ChatCompletion completion = result.Value;

                totalIn += completion.Usage?.InputTokenCount ?? 0;
                totalOut += completion.Usage?.OutputTokenCount ?? 0;

                var assistantText = completion.Content.Count > 0
                    ? completion.Content[0].Text
                    : null;

                trace.Add(new ModelResponseEvent(
                    DateTimeOffset.UtcNow, iter,
                    assistantText,
                    completion.Usage?.OutputTokenCount ?? 0,
                    completion.FinishReason.ToString()));

                // Echo the assistant message back into history — the API needs it
                // before we can send tool results.
                messages.Add(new AssistantChatMessage(completion));

                if (completion.ToolCalls.Count == 0)
                {
                    // No tool calls — model finished talking. Treat trailing text as the answer.
                    status = RunStatus.Completed;
                    finalAnswer = assistantText;
                    break;
                }

                bool finishedViaTool = false;
                foreach (var call in completion.ToolCalls)
                {
                    ct.ThrowIfCancellationRequested();

                    var inputJson = JsonDocument.Parse(call.FunctionArguments).RootElement.Clone();
                    trace.Add(new ToolCallEvent(
                        DateTimeOffset.UtcNow, iter, call.Id, call.FunctionName, inputJson));

                    if (call.FunctionName == "finish")
                    {
                        finalAnswer = inputJson.TryGetProperty("answer", out var a)
                            ? a.GetString()
                            : null;
                        trace.Add(new AgentFinishedEvent(
                            DateTimeOffset.UtcNow, iter, finalAnswer ?? ""));
                        status = RunStatus.Completed;
                        finishedViaTool = true;
                        break;
                    }

                    ITool tool;
                    try
                    {
                        tool = _tools.Get(call.FunctionName);
                    }
                    catch (KeyNotFoundException)
                    {
                        var msg = $"Unknown tool: {call.FunctionName}";
                        trace.Add(new ToolResultEvent(
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

                    trace.Add(new ToolResultEvent(
                        DateTimeOffset.UtcNow, iter, call.Id, toolResult.Output, toolResult.IsError));
                    messages.Add(new ToolChatMessage(call.Id, toolResult.Output));
                }

                if (finishedViaTool) break;
            }

            if (status == RunStatus.Running) status = RunStatus.MaxIterationsHit;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            status = RunStatus.Error;
            trace.Add(new ErrorEvent(
                DateTimeOffset.UtcNow, iterationsUsed, ex.Message, ex.ToString()));
        }

        var finished = DateTimeOffset.UtcNow;
        return new Run(
            Id: runId,
            TaskId: task.Id,
            AgentConfigId: config.Id,
            StartedAt: started,
            FinishedAt: finished,
            Status: status,
            FinalAnswer: finalAnswer,
            Trace: trace,
            Usage: new RunUsage(totalIn, totalOut, iterationsUsed, finished - started));
    }

    /// <summary>
    /// Convert our provider-agnostic ITool into an OpenAI ChatTool definition.
    /// The JSON Schema we stored on ITool is passed through verbatim.
    /// </summary>
    private static ChatTool ToOpenAITool(ITool tool) =>
        ChatTool.CreateFunctionTool(
            functionName: tool.Name,
            functionDescription: tool.Description,
            functionParameters: BinaryData.FromString(tool.InputSchema.GetRawText()));
}
