using BugMemory.Application.Dtos;
using BugMemory.Domain.Entities;

namespace BugMemory.Application.Mapping;

internal static class BugMemoryMapper
{
    public static BugMemoryDto ToDto(this BugMemoryEntry entry) =>
        new(
            entry.Id,
            entry.Kind,
            entry.Title,
            entry.Tags,
            entry.Context,
            entry.RootCause,
            entry.Solution,
            entry.AffectedServices,
            entry.Links,
            entry.CreatedAt,
            entry.UpdatedAt);
}
