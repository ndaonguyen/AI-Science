using BugMemory.Application.Dtos;
using BugMemory.Domain.Entities;

namespace BugMemory.Application.Mapping;

internal static class BugMemoryMapper
{
    public static BugMemoryDto ToDto(this BugMemoryEntry entry) =>
        new(entry.Id, entry.Title, entry.Tags, entry.Context, entry.RootCause, entry.Solution, entry.CreatedAt, entry.UpdatedAt);
}
