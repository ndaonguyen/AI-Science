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
            MemoryKind.Bug,
            "Kafka retry causing duplicate key violation",
            new[] { "kafka", "opensearch" },
            "Producer retry triggered second insert before first ack",
            "Idempotency key not applied to upserts",
            "Added unique constraint on event id and switched to upsert",
            null,
            null);

        var result = await _sut.ExecuteAsync(command, CancellationToken.None);

        result.Title.Should().Be(command.Title);
        result.Tags.Should().BeEquivalentTo(new[] { "kafka", "opensearch" });
        result.Kind.Should().Be(MemoryKind.Bug);

        _repository.Verify(r => r.AddAsync(It.IsAny<BugMemoryEntry>(), It.IsAny<CancellationToken>()), Times.Once);
        _vectorStore.Verify(v => v.UpsertAsync(
            It.Is<Guid>(g => g == result.Id),
            It.Is<float[]>(f => f.Length == 1536),
            It.IsAny<IReadOnlyDictionary<string, object>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_persists_feature_kind_with_links_and_services()
    {
        var command = new CreateBugMemoryCommand(
            MemoryKind.Feature,
            "Add MetadataJson to authoring entries",
            new[] { "publishing", "versioning" },
            "Publishing flow needs to detect metadata changes to cut a new version.",
            "Without a serialized metadata snapshot, the publisher can't diff metadata between saves and treats the item as already published.",
            "Add MetadataJson to authoring service entry; publisher diffs this on publish.",
            new[] { "authoring-service", "content-search-service" },
            new[] { "https://example.com/pr/123", "C:/repos/authoring-service" });

        var result = await _sut.ExecuteAsync(command, CancellationToken.None);

        result.Kind.Should().Be(MemoryKind.Feature);
        result.AffectedServices.Should().BeEquivalentTo(new[] { "authoring-service", "content-search-service" });
        result.Links.Should().BeEquivalentTo(new[] { "https://example.com/pr/123", "C:/repos/authoring-service" });
    }

    [Fact]
    public async Task ExecuteAsync_throws_when_title_missing()
    {
        var command = new CreateBugMemoryCommand(
            MemoryKind.Bug, "", new List<string>(), "ctx", "cause", "fix", null, null);

        var act = () => _sut.ExecuteAsync(command, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }
}

public class BugMemoryEntryTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Bug_embedding_text_keeps_original_format()
    {
        var entry = BugMemoryEntry.Create(
            MemoryKind.Bug,
            "Kafka retry duplicate key",
            "Producer retry triggered second insert.",
            "Idempotency key not applied to upserts.",
            "Switched to upsert with unique constraint.",
            new[] { "kafka", "opensearch" },
            null,
            null,
            Now);

        var text = entry.ToEmbeddingText();

        text.Should().Contain("Title: Kafka retry duplicate key");
        text.Should().Contain("Tags: kafka, opensearch");
        text.Should().Contain("Root cause:");
        text.Should().Contain("Solution:");
        text.Should().NotContain("Kind:");
        text.Should().NotContain("Why:");
    }

    [Fact]
    public void Feature_embedding_text_uses_why_decision_labels_and_includes_services_links()
    {
        var entry = BugMemoryEntry.Create(
            MemoryKind.Feature,
            "Add MetadataJson to authoring entries",
            "Publishing flow needs to detect metadata changes.",
            "Without a metadata snapshot the publisher can't diff metadata.",
            "Add MetadataJson; publisher diffs it on publish.",
            new[] { "publishing" },
            new[] { "authoring-service" },
            new[] { "https://example.com/pr/123" },
            Now);

        var text = entry.ToEmbeddingText();

        text.Should().Contain("Kind: Feature");
        text.Should().Contain("Why: Without a metadata snapshot");
        text.Should().Contain("Decision: Add MetadataJson");
        text.Should().Contain("Affected services: authoring-service");
        text.Should().Contain("Links: https://example.com/pr/123");
        text.Should().NotContain("Root cause:");
        text.Should().NotContain("Solution:");
    }

    [Fact]
    public void Feature_embedding_text_omits_services_and_links_lines_when_empty()
    {
        var entry = BugMemoryEntry.Create(
            MemoryKind.Feature,
            "Some feature",
            "ctx",
            "why",
            "decision",
            Array.Empty<string>(),
            null,
            null,
            Now);

        var text = entry.ToEmbeddingText();

        text.Should().NotContain("Affected services");
        text.Should().NotContain("Links:");
    }

    [Fact]
    public void Hydrate_with_null_services_and_links_yields_empty_lists()
    {
        var entry = BugMemoryEntry.Hydrate(
            Guid.NewGuid(),
            MemoryKind.Bug,
            "Old entry",
            "ctx", "cause", "fix",
            new[] { "kafka" },
            null,
            null,
            Now,
            Now);

        entry.AffectedServices.Should().BeEmpty();
        entry.Links.Should().BeEmpty();
    }

    [Fact]
    public void Links_are_deduped_case_insensitively_but_preserve_case()
    {
        var entry = BugMemoryEntry.Create(
            MemoryKind.Feature,
            "t", "ctx", "why", "decision",
            Array.Empty<string>(),
            null,
            new[] { "https://Example.com/A", "https://example.com/A", "  https://example.com/B  " },
            Now);

        entry.Links.Should().HaveCount(2);
        entry.Links.Should().Contain("https://Example.com/A");
        entry.Links.Should().Contain("https://example.com/B");
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
            Array.Empty<IExternalSearchProvider>(),
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
            .ReturnsAsync(BugMemoryEntry.Hydrate(citedId, MemoryKind.Bug, "Cited bug", "ctx", "cause", "fix", new[] { "kafka" }, null, null, now, now));
        _repository.Setup(r => r.GetByIdAsync(uncitedId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(BugMemoryEntry.Hydrate(uncitedId, MemoryKind.Bug, "Uncited bug", "ctx", "cause", "fix", new[] { "other" }, null, null, now, now));
        _llm.Setup(l => l.AnswerWithContextAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievedContext>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RagAnswer("The fix was the upsert change.", new[] { citedId }));

        var result = await _sut.ExecuteAsync(new AskBugMemoryQuery("how do I fix this", 5), CancellationToken.None);

        result.Citations.Should().HaveCount(1);
        result.Citations[0].Entry.Id.Should().Be(citedId);
    }
}
