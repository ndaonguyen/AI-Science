using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using HarnessArena.Core.Models;
using HarnessArena.Core.Tools;

namespace HarnessArena.Agents;

/// <summary>
/// Agent that talks to Anthropic Claude via the official Anthropic .NET SDK.
///
/// The ReAct loop mirrors OpenAIAgent exactly. The key differences from OpenAI:
///
///   - System prompt is a top-level field on the request, not a message role.
///   - Tool results go back in a NEW user message as ToolResultBlockParam blocks
///     (NOT a "tool" role like OpenAI — they are "user" role content blocks).
///   - The assistant turn must be echoed back using ContentBlockParam types
///     (TextBlockParam + ToolUseBlockParam), not the raw response object.
///   - Stop reason is "tool_use" when the model wants to call tools.
///   - Content blocks use TryPick* pattern to discriminate union types.
///
/// Everything else — the loop structure, tool registry, trace events, Run record —
/// is provider-agnostic and identical to OpenAIAgent.
/// </summary>
public sealed class ClaudeAgent : IAgent
{
    private readonly IToolRegistry _tools;
    private readonly string _apiKey;

    public ClaudeAgent(IToolRegistry tools, string apiKey)
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
            var client = new AnthropicClient(new AnthropicClientOptions
            {
                ApiKey = _apiKey,
            });

            var toolsForThisRun = _tools.GetMany(config.ToolNames).ToList();
            var toolDefs = toolsForThisRun.Select(ToClaudeTool).ToList();

            // Anthropic separates the system prompt from the messages list.
            var messages = new List<MessageParam>
            {
                new() { Role = Role.User, Content = task.Prompt },
            };

            var maxIter = Math.Min(task.MaxIterations, config.MaxIterations);

            for (int iter = 1; iter <= maxIter; iter++)
            {
                ct.ThrowIfCancellationRequested();
                iterationsUsed = iter;

                trace.Add(new ModelCallEvent(DateTimeOffset.UtcNow, iter, messages.Count));

                var request = new MessageCreateParams
                {
                    Model = config.Model,
                    MaxTokens = 4096,
                    Temperature = config.Temperature,
                    System = config.SystemPrompt,
                    Messages = messages,
                    Tools = toolDefs,
                };

                var response = await client.Messages.CreateAsync(request, cancellationToken: ct);

                totalIn += (int)(response.Usage?.InputTokens ?? 0);
                totalOut += (int)(response.Usage?.OutputTokens ?? 0);

                // Extract text from the response content blocks.
                string? assistantText = null;
                var assistantContentParams = new List<ContentBlockParam>();
                var toolUseBlocks = new List<ToolUseBlock>();

                foreach (var block in response.Content)
                {
                    if (block.TryPickText(out var textBlock))
                    {
                        assistantText = textBlock.Text;
                        assistantContentParams.Add(new TextBlockParam { Text = textBlock.Text });
                    }
                    else if (block.TryPickToolUse(out var toolUseBlock))
                    {
                        toolUseBlocks.Add(toolUseBlock);
                        // Echo tool use back — do NOT copy Caller (API rejects it)
                        assistantContentParams.Add(new ToolUseBlockParam
                        {
                            ID = toolUseBlock.ID,
                            Name = toolUseBlock.Name,
                            Input = toolUseBlock.Input,
                        });
                    }
                }

                trace.Add(new ModelResponseEvent(
                    DateTimeOffset.UtcNow, iter,
                    assistantText,
                    (int)(response.Usage?.OutputTokens ?? 0),
                    response.StopReason?.ToString() ?? "unknown"));

                // Echo the assistant turn back into history as typed content params.
                messages.Add(new MessageParam
                {
                    Role = Role.Assistant,
                    Content = assistantContentParams,
                });

                if (toolUseBlocks.Count == 0)
                {
                    // No tool calls — model finished. Treat trailing text as the answer.
                    status = RunStatus.Completed;
                    finalAnswer = assistantText;
                    break;
                }

                // Process each tool call and collect results into a single user message.
                // Anthropic requires ALL tool results for a turn in ONE user message.
                var toolResultParams = new List<ContentBlockParam>();
                bool finishedViaTool = false;

                foreach (var toolUse in toolUseBlocks)
                {
                    ct.ThrowIfCancellationRequested();

                    var inputJson = JsonDocument.Parse(toolUse.Input.ToString()!).RootElement.Clone();

                    trace.Add(new ToolCallEvent(
                        DateTimeOffset.UtcNow, iter, toolUse.ID, toolUse.Name, inputJson));

                    if (toolUse.Name == "finish")
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

                    string toolOutput;
                    bool isError;

                    try
                    {
                        var tool = _tools.Get(toolUse.Name);
                        var result = await tool.ExecuteAsync(inputJson, ct);
                        toolOutput = result.Output;
                        isError = result.IsError;
                    }
                    catch (KeyNotFoundException)
                    {
                        toolOutput = $"Unknown tool: {toolUse.Name}";
                        isError = true;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        toolOutput = $"Tool threw: {ex.Message}";
                        isError = true;
                    }

                    trace.Add(new ToolResultEvent(
                        DateTimeOffset.UtcNow, iter, toolUse.ID, toolOutput, isError));

                    toolResultParams.Add(new ToolResultBlockParam
                    {
                        ToolUseId = toolUse.ID,
                        Content = toolOutput,
                        IsError = isError,
                    });
                }

                if (finishedViaTool) break;

                // All tool results go back in a single user message.
                if (toolResultParams.Count > 0)
                {
                    messages.Add(new MessageParam
                    {
                        Role = Role.User,
                        Content = toolResultParams,
                    });
                }
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
    /// Convert our provider-agnostic ITool into an Anthropic Tool definition.
    /// The JSON Schema stored on ITool is passed through verbatim as the input schema.
    /// </summary>
    private static ToolParam ToClaudeTool(ITool tool) =>
        new()
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = new ToolParamInputSchema
            {
                Type = ToolParamInputSchemaType.Object,
                Properties = tool.InputSchema.TryGetProperty("properties", out var props)
                    ? props
                    : default,
            },
        };
}
