using HarnessArena.Core.Models;

namespace HarnessArena.Core.Tasks;

public interface ITaskLoader
{
    Task<IReadOnlyList<TaskDefinition>> LoadAsync(string path, CancellationToken ct);
}
