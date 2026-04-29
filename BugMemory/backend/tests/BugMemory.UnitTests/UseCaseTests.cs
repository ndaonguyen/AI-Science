using BugMemory.Application.Abstractions;
using BugMemory.Application.UseCases;
using BugMemory.Domain.Entities;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BugMemory.UnitTests;

public class CreateBugMemoryUseCaseTests
{
    private readonly Mock<IBugMemoryRepository> _repository = new();
    private readonly Mock<IEmbeddingService> _embeddings = new();
    private readonly Mock<IVectorStore> _vectorStore = new();
    private readonly Mock<IClock> _clock = new();
    private readonly CreateBugMemoryUseCase _sut;

    public CreateBugMemoryUseCaseTests()
    {
        _clock.Setup(c => c.UtcNow).Returns(new DateTimeOffset(2026, 4, 29, 10, 0, 0, TimeSpan.Zero));
        _embeddings.Setup(e => e.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new float[1536]);
        _sut = new CreateBugMemoryUseCase(
            _repository.Object,
            _embeddings.Object,
            _vectorStore.Object,
            _clock.Object,
            NullLogger<CreateBugMemoryUseCase>.Instance);
    }

    [Fact]
    public async Task ExecuteAsync_persists_entry_and_indexes_embedding()
    {
        var command = new CreateBugMemoryCommand(
            "Kafka retry causing duplicate key violation",
            new[] { "kafka", "opensearch" },
            "Producer retry triggered second insert before first ack",
            "Idempotency key not applied to upserts",
            "Added unique constraint on event id and switched to upsert");

        var result = await _sut.ExecuteAsync(command, CancellationToken.None);

        result.Title.Should().Be(command.Title);
        result.Tags.Should().BeEquivalentTo(new[] { "kafka", "opensearch" });

        _repository.Verify(r => r.AddAsync(It.IsAny<BugMemoryEntry>(), It.IsAny<CancellationToken>()), Times.Once);
        _vectorStore.Verify(v => v.UpsertAsync(
            It.Is<Guid>(g => g == result.Id),
            It.Is<float[]>(f => f.Length == 1536),
            It.IsAny<IReadOnlyDictionary<string, object>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_throws_when_title_missing()
    {
        var command = new CreateBugMemoryCommand("", new List<string>(), "ctx", "cause", "fix");

        var act = () => _sut.ExecuteAsync(command, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }
}

public class AskBugMemoryUseCaseTests
{
    private readonly Mock<IEmbeddingService> _embeddings = new();
    private readonly Mock<IVectorStore> _vectorStore = new();
    private readonly Mock<IBugMemoryRepository> _repository = new();
    private readonly Mock<ILlmService> _llm = new();
    private readonly AskBugMemoryUseCase _sut;

    public AskBugMemoryUseCaseTests()
    {
        _sut = new AskBugMemoryUseCase(
            _embeddings.Object,
            _vectorStore.Object,
            _repository.Object,
            _llm.Object,
            NullLogger<AskBugMemoryUseCase>.Instance);
    }

    [Fact]
    public async Task ExecuteAsync_returns_friendly_message_when_no_hits()
    {
        _embeddings.Setup(e => e.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new float[1536]);
        _vectorStore.Setup(v => v.SearchAsync(It.IsAny<float[]>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<VectorSearchHit>());

        var result = await _sut.ExecuteAsync(new AskBugMemoryQuery("how to fix duplicate keys", 5), CancellationToken.None);

        result.Citations.Should().BeEmpty();
        result.Answer.Should().Contain("don't have");
        _llm.Verify(l => l.AnswerWithContextAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievedContext>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_filters_citations_to_only_those_cited_by_llm()
    {
        var citedId = Guid.NewGuid();
        var uncitedId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        _embeddings.Setup(e => e.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new float[1536]);
        _vectorStore.Setup(v => v.SearchAsync(It.IsAny<float[]>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new VectorSearchHit(citedId, 0.9f),
                new VectorSearchHit(uncitedId, 0.7f),
            });
        _repository.Setup(r => r.GetByIdAsync(citedId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(BugMemoryEntry.Hydrate(citedId, "Cited bug", "ctx", "cause", "fix", new[] { "kafka" }, now, now));
        _repository.Setup(r => r.GetByIdAsync(uncitedId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(BugMemoryEntry.Hydrate(uncitedId, "Uncited bug", "ctx", "cause", "fix", new[] { "other" }, now, now));
        _llm.Setup(l => l.AnswerWithContextAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievedContext>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RagAnswer("The fix was the upsert change.", new[] { citedId }));

        var result = await _sut.ExecuteAsync(new AskBugMemoryQuery("how do I fix this", 5), CancellationToken.None);

        result.Citations.Should().HaveCount(1);
        result.Citations[0].Entry.Id.Should().Be(citedId);
    }
}
