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
            entry.UpdatedAt,
            entry.ReviewHistory is null ? null : ToDto(entry.ReviewHistory));

    private static ReviewHistoryDto ToDto(ReviewHistory h) =>
        new(
            h.Summary,
            h.Clarifications.Select(c => new ReviewClarificationDto(
                c.Question, c.Answer, c.AiAnswer, c.Evidence)).ToList(),
            h.ScannedRepos,
            h.UnconfiguredServices,
            h.RewrittenContext,
            h.ReviewedAt);
}
