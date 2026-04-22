using AwesomeAssertions;
using Xunit;

namespace DistributedDebugger.Eval.Tests;

/// <summary>
/// Tests the YAML loader's validation, defaults, and discovery rules. Uses a
/// temp directory per test so tests are hermetic — they don't depend on each
/// other or on the repo's real eval-cases folder.
/// </summary>
public class YamlCaseLoaderTests : IAsyncLifetime
{
    private string _tempDir = string.Empty;
    private readonly YamlCaseLoader _loader = new();

    public Task InitializeAsync()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "yaml-case-loader-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task LoadAsync_WhenFolderMissing_Should_ThrowDirectoryNotFoundException()
    {
        // Arrange
        var nonExistent = Path.Combine(_tempDir, "does-not-exist");

        // Act
        Func<Task> act = () => _loader.LoadAsync(nonExistent, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<DirectoryNotFoundException>()
            .WithMessage("*not found*");
    }

    [Fact]
    public async Task LoadAsync_WhenFolderIsEmpty_Should_ReturnEmptyList()
    {
        // Act
        var cases = await _loader.LoadAsync(_tempDir, CancellationToken.None);

        // Assert
        cases.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_WhenValidCase_Should_PopulateAllFields()
    {
        // Arrange
        await WriteCaseAsync("valid", """
            id: my-case
            description: A bug happened
            ticketId: COCO-999
            truth:
              expectedCause: The thing broke
              expectedServices:
                - service-a
                - service-b
              mustMentionKeywords:
                - broken
              mustNotClaim:
                - Kafka issue
              minConfidence: High
            scriptedLogs:
              - service: service-a
                matchesKeyword: error
                logs: "log line"
            scriptedData:
              - tool: request_mongo_query
                matches: act-1
                response: "[]"
            """);

        // Act
        var cases = await _loader.LoadAsync(_tempDir, CancellationToken.None);

        // Assert
        cases.Should().ContainSingle();
        var c = cases[0];
        c.Id.Should().Be("my-case");
        c.Description.Should().Be("A bug happened");
        c.TicketId.Should().Be("COCO-999");
        c.Truth.ExpectedCause.Should().Be("The thing broke");
        c.Truth.ExpectedServices.Should().BeEquivalentTo(new[] { "service-a", "service-b" });
        c.Truth.MustMentionKeywords.Should().BeEquivalentTo(new[] { "broken" });
        c.Truth.MustNotClaim.Should().BeEquivalentTo(new[] { "Kafka issue" });
        c.Truth.MinConfidence.Should().Be("High");
        c.LogResponses.Should().ContainSingle();
        c.DataResponses.Should().ContainSingle();
    }

    [Fact]
    public async Task LoadAsync_WhenIdMissing_Should_FallBackToFileName()
    {
        // Arrange
        await WriteCaseAsync("fallback-from-file", """
            description: thing
            truth:
              expectedCause: root
            """);

        // Act
        var cases = await _loader.LoadAsync(_tempDir, CancellationToken.None);

        // Assert
        cases.Should().ContainSingle();
        cases[0].Id.Should().Be("fallback-from-file");
    }

    [Fact]
    public async Task LoadAsync_WhenMinConfidenceMissing_Should_DefaultToMedium()
    {
        // Arrange
        await WriteCaseAsync("default-conf", """
            id: default-conf
            description: x
            truth:
              expectedCause: y
            """);

        // Act
        var cases = await _loader.LoadAsync(_tempDir, CancellationToken.None);

        // Assert
        cases[0].Truth.MinConfidence.Should().Be("Medium");
    }

    [Fact]
    public async Task LoadAsync_WhenOptionalListsMissing_Should_DefaultToEmpty()
    {
        // Arrange
        // No scriptedLogs, scriptedData, mustMentionKeywords, mustNotClaim.
        await WriteCaseAsync("minimal", """
            id: minimal
            description: x
            truth:
              expectedCause: y
            """);

        // Act
        var cases = await _loader.LoadAsync(_tempDir, CancellationToken.None);

        // Assert
        var c = cases[0];
        c.LogResponses.Should().BeEmpty();
        c.DataResponses.Should().BeEmpty();
        c.Truth.MustMentionKeywords.Should().BeEmpty();
        c.Truth.MustNotClaim.Should().BeEmpty();
        c.Truth.ExpectedServices.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_WhenDescriptionMissing_Should_ThrowInvalidDataException()
    {
        // Arrange
        await WriteCaseAsync("no-desc", """
            id: no-desc
            truth:
              expectedCause: y
            """);

        // Act
        Func<Task> act = () => _loader.LoadAsync(_tempDir, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*description*");
    }

    [Fact]
    public async Task LoadAsync_WhenTruthSectionMissing_Should_ThrowInvalidDataException()
    {
        // Arrange
        await WriteCaseAsync("no-truth", """
            id: no-truth
            description: something broke
            """);

        // Act
        Func<Task> act = () => _loader.LoadAsync(_tempDir, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*truth*");
    }

    [Fact]
    public async Task LoadAsync_WhenExpectedCauseMissing_Should_ThrowInvalidDataException()
    {
        // Arrange
        await WriteCaseAsync("no-cause", """
            id: no-cause
            description: something broke
            truth:
              expectedServices:
                - a
            """);

        // Act
        Func<Task> act = () => _loader.LoadAsync(_tempDir, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*expectedCause*");
    }

    [Fact]
    public async Task LoadAsync_WhenMultipleFiles_Should_ReturnInOrdinalFilenameOrder()
    {
        // Arrange
        // Deliberately create them out of order to confirm sorting.
        await WriteCaseAsync("z-last", "id: z\ndescription: z\ntruth:\n  expectedCause: z\n");
        await WriteCaseAsync("a-first", "id: a\ndescription: a\ntruth:\n  expectedCause: a\n");
        await WriteCaseAsync("m-middle", "id: m\ndescription: m\ntruth:\n  expectedCause: m\n");

        // Act
        var cases = await _loader.LoadAsync(_tempDir, CancellationToken.None);

        // Assert
        cases.Select(c => c.Id).Should().BeEquivalentTo(new[] { "a", "m", "z" }, o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task LoadAsync_WhenYmlExtension_Should_AlsoBePickedUp()
    {
        // Arrange
        // Our discovery glob covers both *.yaml and *.yml.
        var path = Path.Combine(_tempDir, "short.yml");
        await File.WriteAllTextAsync(path, """
            id: yml-case
            description: x
            truth:
              expectedCause: y
            """);

        // Act
        var cases = await _loader.LoadAsync(_tempDir, CancellationToken.None);

        // Assert
        cases.Should().ContainSingle().Which.Id.Should().Be("yml-case");
    }

    private Task WriteCaseAsync(string name, string body)
    {
        var path = Path.Combine(_tempDir, name + ".yaml");
        return File.WriteAllTextAsync(path, body);
    }
}
