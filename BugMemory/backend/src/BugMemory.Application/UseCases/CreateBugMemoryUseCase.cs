using BugMemory.Application.Abstractions;
using BugMemory.Application.Dtos;
using BugMemory.Application.Mapping;
using BugMemory.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace BugMemory.Application.UseCases;

public sealed record CreateBugMemoryCommand(
    MemoryKind Kind,
    string Title,
    IReadOnlyList<string> Tags,
    string Context,
    string RootCause,
    string Solution,
    IReadOnlyList<string>? AffectedServices,
    IReadOnlyList<string>? Links,
    ReviewHistoryDto? ReviewHistory = null);

public sealed class CreateBugMemoryUseCase
{
    private readonly IBugMemoryRepository _repository;
    private readonly IEmbeddingService _embeddings;
    private readonly IVectorStore _vectorStore;
    private readonly IClock _clock;
    private readonly ILogger<CreateBugMemoryUseCase> _logger;

    public CreateBugMemoryUseCase(
        IBugMemoryRepository repository,
        IEmbeddingService embeddings,
        IVectorStore vectorStore,
        IClock clock,
        ILogger<CreateBugMemoryUseCase> logger)
    {
        _repository = repository;
        _embeddings = embeddings;
        _vectorStore = vectorStore;
        _clock = clock;
        _logger = logger;
    }

    public async Task<BugMemoryDto> ExecuteAsync(CreateBugMemoryCommand command, CancellationToken ct)
    {
        var entry = BugMemoryEntry.Create(
            command.Kind,
            command.Title,
            command.Context,
            command.RootCause,
            command.Solution,
            command.Tags,
            command.AffectedServices,
            command.Links,
            _clock.UtcNow);

        if (command.ReviewHistory is not null)
        {
            entry.SetReviewHistory(ToDomain(command.ReviewHistory, _clock.UtcNow));
        }

        await _repository.AddAsync(entry, ct);

        var embedding = await _embeddings.EmbedAsync(entry.ToEmbeddingText(), ct);
        await _vectorStore.UpsertAsync(
            entry.Id,
            embedding,
            new Dictionary<string, object> { ["entryId"] = entry.Id.ToString() },
            ct);

        _logger.LogInformation("Created {Kind} memory {Id}", entry.Kind, entry.Id);
        return entry.ToDto();
    }

    internal static ReviewHistory ToDomain(ReviewHistoryDto dto, DateTimeOffset now) => new()
    {
        Summary = dto.Summary ?? string.Empty,
        Clarifications = (dto.Clarifications ?? Array.Empty<ReviewClarificationDto>())
            .Select(c => new ReviewClarification
            {
                Question = c.Question ?? string.Empty,
                Answer = c.Answer ?? string.Empty,
                AiAnswer = c.AiAnswer ?? string.Empty,
                Evidence = c.Evidence ?? Array.Empty<string>(),
            })
            .ToList(),
        ScannedRepos = dto.ScannedRepos ?? Array.Empty<string>(),
        UnconfiguredServices = dto.UnconfiguredServices ?? Array.Empty<string>(),
        RewrittenContext = dto.RewrittenContext ?? string.Empty,
        ReviewedAt = dto.ReviewedAt == default ? now : dto.ReviewedAt,
    };
}
