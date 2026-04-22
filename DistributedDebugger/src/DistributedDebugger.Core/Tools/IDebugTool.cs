using System.Text.Json;

namespace DistributedDebugger.Core.Tools;

/// <summary>
/// A tool the investigator agent can invoke to gather evidence. Each tool
/// corresponds to one source of truth in your distributed system — Jira,
/// Datadog, CloudWatch, Kafka, Mongo, etc.
///
/// JSON Schema is passed verbatim to the model as the tool's input definition.
/// </summary>
public interface IDebugTool
{
    string Name { get; }
    string Description { get; }
    JsonElement InputSchema { get; }

    Task<ToolExecutionResult> ExecuteAsync(JsonElement input, CancellationToken ct);
}

public sealed record ToolExecutionResult(string Output, bool IsError);

public interface IToolRegistry
{
    IDebugTool Get(string name);
    IReadOnlyList<IDebugTool> GetMany(IEnumerable<string> names);
    IReadOnlyList<IDebugTool> All { get; }
}

public sealed class ToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, IDebugTool> _tools;

    public ToolRegistry(IEnumerable<IDebugTool> tools)
    {
        _tools = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
    }

    public IReadOnlyList<IDebugTool> All => _tools.Values.ToList();

    public IDebugTool Get(string name) =>
        _tools.TryGetValue(name, out var t)
            ? t
            : throw new KeyNotFoundException($"Tool '{name}' is not registered.");

    public IReadOnlyList<IDebugTool> GetMany(IEnumerable<string> names) =>
        names.Select(Get).ToList();
}
