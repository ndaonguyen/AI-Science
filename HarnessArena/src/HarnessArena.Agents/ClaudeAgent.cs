using HarnessArena.Core.Models;
using HarnessArena.Core.Tools;

namespace HarnessArena.Agents;

/// <summary>
/// Placeholder for a Claude-based agent. The Anthropic C# SDK is in beta and
/// I want to give you the OpenAI implementation to actually run first. Once
/// you have one provider working end-to-end, wiring the Anthropic SDK into
/// a sibling implementation is a straightforward copy-modify of OpenAIAgent.
///
/// Keeping this as an explicit NotImplementedException (rather than silently
/// returning a fake Run) so it surfaces clearly if accidentally selected.
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

    public Task<Run> RunAsync(TaskDefinition task, AgentConfig config, CancellationToken ct)
    {
        throw new NotImplementedException(
            "ClaudeAgent is not yet implemented. Use --agent openai or --agent mock for now. " +
            "See OpenAIAgent.cs for the reference ReAct loop — the Claude version follows " +
            "the same pattern with Anthropic SDK message types.");
    }
}
