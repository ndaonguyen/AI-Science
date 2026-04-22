using System.Text.Json;
using AwesomeAssertions;
using DistributedDebugger.Eval.Tools;
using Xunit;

namespace DistributedDebugger.Eval.Tests.Tools;

public class ScriptedLogToolTests
{
    [Fact]
    public async Task ExecuteAsync_WhenServiceAndKeywordMatch_Should_ReturnScriptedLogs()
    {
        // Arrange
        var tool = new ScriptedLogTool(new[]
        {
            new ScriptedLogResponse("content-media-service", "act-789", "ERROR timeout on act-789"),
        });

        // Act
        var result = await tool.ExecuteAsync(
            InputFrom(new { service = "content-media-service", query = "act-789" }),
            CancellationToken.None);

        // Assert
        result.IsError.Should().BeFalse();
        result.Output.Should().Contain("ERROR timeout on act-789");
    }

    [Fact]
    public async Task ExecuteAsync_WhenAgentQueryIsSubstringOfScriptedKeyword_Should_Match()
    {
        // Arrange
        // Bidirectional matching: the case pinned on "act-789-retry" but the
        // agent asks about just "act-789". We want a hit, not a false miss.
        var tool = new ScriptedLogTool(new[]
        {
            new ScriptedLogResponse("svc-a", "act-789-retry", "retry log"),
        });

        // Act
        var result = await tool.ExecuteAsync(
            InputFrom(new { service = "svc-a", query = "act-789" }),
            CancellationToken.None);

        // Assert
        result.Output.Should().Contain("retry log");
    }

    [Fact]
    public async Task ExecuteAsync_WhenScriptedKeywordIsSubstringOfAgentQuery_Should_Match()
    {
        // Arrange
        // Other direction: case pinned on "timeout", agent queries "OpenSearch timeout error".
        var tool = new ScriptedLogTool(new[]
        {
            new ScriptedLogResponse("svc-a", "timeout", "timeout log"),
        });

        // Act
        var result = await tool.ExecuteAsync(
            InputFrom(new { service = "svc-a", query = "OpenSearch timeout error" }),
            CancellationToken.None);

        // Assert
        result.Output.Should().Contain("timeout log");
    }

    [Fact]
    public async Task ExecuteAsync_WhenServiceCaseDiffers_Should_Match()
    {
        // Arrange
        var tool = new ScriptedLogTool(new[]
        {
            new ScriptedLogResponse("content-media-service", "foo", "log"),
        });

        // Act
        var result = await tool.ExecuteAsync(
            InputFrom(new { service = "CONTENT-MEDIA-SERVICE", query = "foo" }),
            CancellationToken.None);

        // Assert
        result.IsError.Should().BeFalse();
        result.Output.Should().Contain("log");
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoMatchingResponse_Should_ReturnNoMatchMessageNotError()
    {
        // Arrange
        // "No match" is itself a signal in debugging — we don't want the
        // agent treating this as a tool failure.
        var tool = new ScriptedLogTool(new[]
        {
            new ScriptedLogResponse("svc-a", "foo", "log"),
        });

        // Act
        var result = await tool.ExecuteAsync(
            InputFrom(new { service = "svc-a", query = "bar" }),
            CancellationToken.None);

        // Assert
        result.IsError.Should().BeFalse();
        result.Output.Should().Contain("No scripted logs");
    }

    [Fact]
    public async Task ExecuteAsync_WhenServiceMissing_Should_ReturnError()
    {
        // Arrange
        var tool = new ScriptedLogTool(Array.Empty<ScriptedLogResponse>());

        // Act
        var result = await tool.ExecuteAsync(
            InputFrom(new { query = "foo" }),  // no service
            CancellationToken.None);

        // Assert
        result.IsError.Should().BeTrue();
        result.Output.Should().Contain("service");
    }

    [Fact]
    public async Task ExecuteAsync_WhenKeywordFieldUsedInsteadOfQuery_Should_AcceptBoth()
    {
        // Arrange
        // Phase 1 mock tool uses `keyword`, Phase 2 CloudWatch tool uses `query`.
        // The scripted tool must accept both so any case + any agent works.
        var tool = new ScriptedLogTool(new[]
        {
            new ScriptedLogResponse("svc-a", "foo", "log"),
        });

        // Act
        var result = await tool.ExecuteAsync(
            InputFrom(new { service = "svc-a", keyword = "foo" }),
            CancellationToken.None);

        // Assert
        result.Output.Should().Contain("log");
    }

    [Fact]
    public async Task ExecuteAsync_WhenMultipleResponsesCouldMatch_Should_ReturnFirst()
    {
        // Arrange
        // FirstOrDefault semantics: deterministic and predictable. Case authors
        // should order their responses from most-specific to most-general.
        var tool = new ScriptedLogTool(new[]
        {
            new ScriptedLogResponse("svc-a", "foo", "FIRST"),
            new ScriptedLogResponse("svc-a", "foo", "SECOND"),
        });

        // Act
        var result = await tool.ExecuteAsync(
            InputFrom(new { service = "svc-a", query = "foo" }),
            CancellationToken.None);

        // Assert
        result.Output.Should().Be("FIRST");
    }

    private static JsonElement InputFrom(object shape) =>
        JsonDocument.Parse(JsonSerializer.Serialize(shape)).RootElement.Clone();
}
