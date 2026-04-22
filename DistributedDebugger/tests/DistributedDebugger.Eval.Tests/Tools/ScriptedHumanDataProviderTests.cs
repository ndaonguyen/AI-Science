using AwesomeAssertions;
using DistributedDebugger.Core.Tools;
using DistributedDebugger.Eval.Tools;
using Xunit;

namespace DistributedDebugger.Eval.Tests.Tools;

public class ScriptedHumanDataProviderTests
{
    [Fact]
    public async Task RequestDataAsync_WhenMatchesAnyIsInRenderedQuery_Should_ReturnScriptedResponse()
    {
        // Arrange
        var provider = new ScriptedHumanDataProvider(new[]
        {
            new ScriptedDataResponse(
                ToolName: "request_mongo_query",
                MatchesAny: "act-789",
                Response: "[{\"_id\":\"act-789\"}]"),
        });

        var request = new HumanDataRequest(
            SourceName: "MongoDB",
            RenderedQuery: "db.activities.find({_id: \"act-789\"})",
            Reason: "check doc exists",
            SuggestedEnv: "staging");

        // Act
        var result = await provider.RequestDataAsync(request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Should().Contain("act-789");
    }

    [Fact]
    public async Task RequestDataAsync_WhenMatchesAnyIsInReason_Should_AlsoMatch()
    {
        // Arrange
        // Matching searches source + query + reason, so a distinctive word
        // in just the reason should still trigger a match.
        var provider = new ScriptedHumanDataProvider(new[]
        {
            new ScriptedDataResponse(
                ToolName: "request_mongo_query",
                MatchesAny: "imp-442",
                Response: "bulk import docs"),
        });

        var request = new HumanDataRequest(
            SourceName: "MongoDB",
            RenderedQuery: "db.activities.find({}).limit(10)",
            Reason: "Confirm imp-442 batch landed",
            SuggestedEnv: "test");

        // Act
        var result = await provider.RequestDataAsync(request, CancellationToken.None);

        // Assert
        result.Should().Be("bulk import docs");
    }

    [Fact]
    public async Task RequestDataAsync_WhenMatchIsCaseInsensitive_Should_StillMatch()
    {
        // Arrange
        // The case author wrote "ACT-789" but the agent queried "act-789".
        var provider = new ScriptedHumanDataProvider(new[]
        {
            new ScriptedDataResponse(
                ToolName: "request_mongo_query",
                MatchesAny: "ACT-789",
                Response: "matched"),
        });

        var request = new HumanDataRequest(
            SourceName: "MongoDB",
            RenderedQuery: "find act-789",
            Reason: "x",
            SuggestedEnv: null);

        // Act
        var result = await provider.RequestDataAsync(request, CancellationToken.None);

        // Assert
        result.Should().Be("matched");
    }

    [Fact]
    public async Task RequestDataAsync_WhenSourceMongoButResponseForOpenSearch_Should_NotMatch()
    {
        // Arrange
        // Source-to-tool-name mapping prevents cross-pollination: a Kafka
        // scripted response must not answer a Mongo query just because the
        // entity id happens to be in both.
        var provider = new ScriptedHumanDataProvider(new[]
        {
            new ScriptedDataResponse(
                ToolName: "request_opensearch_query",
                MatchesAny: "act-789",
                Response: "opensearch result"),
        });

        var request = new HumanDataRequest(
            SourceName: "MongoDB",   // wrong source
            RenderedQuery: "find act-789",
            Reason: "x",
            SuggestedEnv: null);

        // Act
        var result = await provider.RequestDataAsync(request, CancellationToken.None);

        // Assert
        // null = engineer declined, which is the correct "no match" semantic.
        result.Should().BeNull();
    }

    [Fact]
    public async Task RequestDataAsync_WhenNoResponseMatches_Should_ReturnNull()
    {
        // Arrange
        var provider = new ScriptedHumanDataProvider(new[]
        {
            new ScriptedDataResponse("request_mongo_query", "totally-different-id", "x"),
        });

        var request = new HumanDataRequest(
            SourceName: "MongoDB",
            RenderedQuery: "find act-789",
            Reason: "x",
            SuggestedEnv: null);

        // Act
        var result = await provider.RequestDataAsync(request, CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task RequestDataAsync_WhenMatchesAnyIsEmpty_Should_NotMatchEverything()
    {
        // Arrange
        // Defensive: an empty MatchesAny string must never succeed against
        // any haystack (otherwise every request would accidentally match it,
        // short-circuiting real responses).
        var provider = new ScriptedHumanDataProvider(new[]
        {
            new ScriptedDataResponse("request_mongo_query", "", "bogus"),
        });

        var request = new HumanDataRequest(
            SourceName: "MongoDB",
            RenderedQuery: "some query",
            Reason: "x",
            SuggestedEnv: null);

        // Act
        var result = await provider.RequestDataAsync(request, CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    [Theory]
    [InlineData("MongoDB", "request_mongo_query")]
    [InlineData("OpenSearch", "request_opensearch_query")]
    [InlineData("Kafka", "request_kafka_events")]
    public async Task RequestDataAsync_SourceShouldMapToCorrectToolName(string source, string toolName)
    {
        // Arrange
        var provider = new ScriptedHumanDataProvider(new[]
        {
            new ScriptedDataResponse(toolName, "anchor", "ok"),
        });

        var request = new HumanDataRequest(
            SourceName: source,
            RenderedQuery: "anchor",
            Reason: "x",
            SuggestedEnv: null);

        // Act
        var result = await provider.RequestDataAsync(request, CancellationToken.None);

        // Assert
        result.Should().Be("ok");
    }

    [Fact]
    public async Task RequestDataAsync_WhenMultipleMatchesExist_Should_ReturnFirst()
    {
        // Arrange
        // Same FirstOrDefault semantics as ScriptedLogTool — case authors get
        // deterministic ordering and should list specific matches before general.
        var provider = new ScriptedHumanDataProvider(new[]
        {
            new ScriptedDataResponse("request_mongo_query", "act-789", "FIRST"),
            new ScriptedDataResponse("request_mongo_query", "act-789", "SECOND"),
        });

        var request = new HumanDataRequest(
            SourceName: "MongoDB",
            RenderedQuery: "find act-789",
            Reason: "x",
            SuggestedEnv: null);

        // Act
        var result = await provider.RequestDataAsync(request, CancellationToken.None);

        // Assert
        result.Should().Be("FIRST");
    }
}
