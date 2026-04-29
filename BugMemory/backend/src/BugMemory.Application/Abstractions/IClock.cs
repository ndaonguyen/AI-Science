namespace BugMemory.Application.Abstractions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
