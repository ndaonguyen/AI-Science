using BugMemory.Application.Abstractions;

namespace BugMemory.Infrastructure;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
