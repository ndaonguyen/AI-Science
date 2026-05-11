namespace BugMemory.Application.Abstractions;

public sealed class ServiceReposOptions
{
    public Dictionary<string, string> Paths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
