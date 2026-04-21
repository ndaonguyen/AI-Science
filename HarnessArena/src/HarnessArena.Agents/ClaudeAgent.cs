using System.Text.Json;
using HarnessArena.Core.Models;
using HarnessArena.Core.Tools;

namespace HarnessArena.Agents;

/// <summary>
/// The core ReAct-style agent loop.
///
///     while not done:
///         response = model.call(messages, tools)
///         append response to messages
///         if response has no tool calls:
///             done (implicit finish)
///         for each tool call:
///             if tool == "finish": record answer, done
///             else: execute tool, append tool_result to messages
///
/// The Anthropic C# SDK is in beta and some type names (Role, ContentBlock,
/// ToolUseBlock, ToolResultBlock, MessageCreateParams) may need adjustment on
/// first compile. The control flow below is what you're after — expect to
/// touch 2-3 import lines and possibly property names to match the SDK version
/// on your machine.
///
/// Suggested incremental path:
///   1. Get a no-tool "Hello, Claude" call round-tripping first.
///   2. Then uncomment the tool wiring and iterate from there.
/// </summary>
public sealed class ClaudeAgent : IAgent
{
    private readonly IToolRegistry _tools;

    // TODO: inject AnthropicClient once SDK types are confirmed on your machine.
    // private readonly Anthropic.AnthropicClient _client;

    public ClaudeAgent(IToolRegistry tools)
    {
        _tools = tools;
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
            // === Skeleton below. Wire the SDK calls in once types compile. ===
            //
            // var tools = _tools.GetMany(config.ToolNames).ToList();
            // var toolDefs = tools.Select(BuildAnthropicToolDefinition).ToList();
            //
            // var messages = new List<Anthropic.Models.Messages.Message>
            // {
            //     new() { Role = Role.User, Content = task.Prompt }
            // };
            //
            // for (int iter = 1; iter <= Math.Min(task.MaxIterations, config.MaxIterations); iter++)
            // {
            //     iterationsUsed = iter;
            //     trace.Add(new ModelCallEvent(DateTimeOffset.UtcNow, iter, messages.Count));
            //
            //     var response = await _client.Messages.Create(new MessageCreateParams
            //     {
            //         Model = config.Model,
            //         MaxTokens = 2048,
            //         System = config.SystemPrompt,
            //         Temperature = config.Temperature,
            //         Tools = toolDefs,
            //         Messages = messages,
            //     }, ct);
            //
            //     totalIn += response.Usage.InputTokens;
            //     totalOut += response.Usage.OutputTokens;
            //     trace.Add(new ModelResponseEvent(
            //         DateTimeOffset.UtcNow, iter,
            //         ExtractFirstText(response),
            //         response.Usage.OutputTokens,
            //         response.StopReason.ToString()));
            //
            //     // Echo assistant message back into history (including tool_use blocks).
            //     messages.Add(new Message { Role = Role.Assistant, Content = response.Content });
            //
            //     var toolUses = response.Content.OfType<ToolUseBlock>().ToList();
            //     if (toolUses.Count == 0)
            //     {
            //         status = RunStatus.Completed;
            //         finalAnswer = ExtractFirstText(response);
            //         break;
            //     }
            //
            //     var toolResults = new List<ContentBlock>();
            //     foreach (var use in toolUses)
            //     {
            //         trace.Add(new ToolCallEvent(
            //             DateTimeOffset.UtcNow, iter, use.Id, use.Name, use.Input));
            //
            //         if (use.Name == "finish")
            //         {
            //             finalAnswer = use.Input.GetProperty("answer").GetString();
            //             trace.Add(new AgentFinishedEvent(
            //                 DateTimeOffset.UtcNow, iter, finalAnswer ?? ""));
            //             status = RunStatus.Completed;
            //             goto done;
            //         }
            //
            //         var tool = _tools.Get(use.Name);
            //         var result = await tool.ExecuteAsync(use.Input, ct);
            //         trace.Add(new ToolResultEvent(
            //             DateTimeOffset.UtcNow, iter, use.Id, result.Output, result.IsError));
            //         toolResults.Add(new ToolResultBlock(use.Id, result.Output, result.IsError));
            //     }
            //
            //     messages.Add(new Message { Role = Role.User, Content = toolResults });
            // }
            //
            // if (status == RunStatus.Running) status = RunStatus.MaxIterationsHit;

            // ===== TEMPORARY: keep project compiling until SDK is wired =====
            await Task.Yield();
            throw new NotImplementedException(
                "Wire AnthropicClient into ClaudeAgent.RunAsync. See comments above.");
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

        // done: label is referenced by the goto above once SDK is wired.
        done:
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
            Usage: new RunUsage(
                InputTokens: totalIn,
                OutputTokens: totalOut,
                Iterations: iterationsUsed,
                WallTime: finished - started));
    }
}
