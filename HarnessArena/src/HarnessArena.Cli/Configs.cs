using HarnessArena.Core.Models;

namespace HarnessArena.Cli;

/// <summary>
/// Baseline agent configs. Add new ones here to experiment — each is an
/// independent "contestant" in the arena.
/// </summary>
public static class Configs
{
    public static readonly AgentConfig Baseline = new(
        Id: "baseline",
        Model: "claude-sonnet-4-6",
        SystemPrompt:
            "You are a careful problem solver. For arithmetic, use the calculator tool " +
            "instead of computing in your head. When you have the final answer, call the " +
            "finish tool with the answer in its simplest form.",
        ToolNames: new[] { "calculator", "finish" },
        MaxIterations: 10,
        Temperature: 0.0
    );

    public static readonly AgentConfig Strict = new(
        Id: "strict",
        Model: "claude-sonnet-4-6",
        SystemPrompt:
            "Solve the problem step by step. You MUST use the calculator tool for every " +
            "arithmetic operation, even trivial ones like 2+2. You MUST call finish exactly " +
            "once with the final answer as a plain number, no units or extra text.",
        ToolNames: new[] { "calculator", "finish" },
        MaxIterations: 15,
        Temperature: 0.0
    );

    public static readonly AgentConfig NoCalculator = new(
        Id: "no-calculator",
        Model: "claude-sonnet-4-6",
        SystemPrompt:
            "Solve the problem step by step and call finish with the final answer.",
        ToolNames: new[] { "finish" },
        MaxIterations: 5,
        Temperature: 0.0
    );

    public static readonly IReadOnlyDictionary<string, AgentConfig> All =
        new Dictionary<string, AgentConfig>(StringComparer.OrdinalIgnoreCase)
        {
            [Baseline.Id] = Baseline,
            [Strict.Id] = Strict,
            [NoCalculator.Id] = NoCalculator,
        };
}
