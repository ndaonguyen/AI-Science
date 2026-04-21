using HarnessArena.Core.Models;

namespace HarnessArena.Agents;

public interface IAgent
{
    Task<Run> RunAsync(TaskDefinition task, AgentConfig config, CancellationToken ct);
}
