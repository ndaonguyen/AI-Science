using System.Text.Json;

namespace HarnessArena.Core.Tools;

/// <summary>
/// A tool the agent can invoke. The JSON Schema describes the expected input shape
/// and is forwarded verbatim to Claude as a tool definition.
/// </summary>
public interface ITool
{
    string Name { get; }
    string Description { get; }
    JsonElement InputSchema { get; }

    Task<ToolExecutionResult> ExecuteAsync(JsonElement input, CancellationToken ct);
}

public sealed record ToolExecutionResult(string Output, bool IsError);

public interface IToolRegistry
{
    ITool Get(string name);
    IReadOnlyList<ITool> GetMany(IEnumerable<string> names);
}

public sealed class ToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, ITool> _tools;

    public ToolRegistry(IEnumerable<ITool> tools)
    {
        _tools = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
    }

    public ITool Get(string name) =>
        _tools.TryGetValue(name, out var t)
            ? t
            : throw new KeyNotFoundException($"Tool '{name}' is not registered.");

    public IReadOnlyList<ITool> GetMany(IEnumerable<string> names) =>
        names.Select(Get).ToList();
}
