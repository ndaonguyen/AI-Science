using AwesomeAssertions;
using DistributedDebugger.Eval.Internal;
using Xunit;

namespace DistributedDebugger.Eval.Tests.Internal;

public class JudgeResponseParserTests
{
    [Fact]
    public void Parse_WhenPlainJson_Should_ExtractAllFields()
    {
        // Arrange
        var raw = """
            {
              "causeCorrect": true,
              "serviceCoverageScore": 0.75,
              "confidenceAppropriate": true,
              "rationale": "agent named both expected services"
            }
            """;

        // Act
        var result = JudgeResponseParser.Parse(raw);

        // Assert
        result.ParseFailed.Should().BeFalse();
        result.CauseCorrect.Should().BeTrue();
        result.ServiceCoverageScore.Should().Be(0.75);
        result.ConfidenceAppropriate.Should().BeTrue();
        result.Rationale.Should().Be("agent named both expected services");
    }

    [Fact]
    public void Parse_WhenWrappedInJsonCodeFence_Should_StripFenceAndParse()
    {
        // Arrange
        // The model sometimes ignores "JSON only" instructions and wraps its
        // answer in ```json ... ```. The parser must recover.
        var raw = """
            ```json
            {
              "causeCorrect": true,
              "serviceCoverageScore": 1.0,
              "confidenceAppropriate": true,
              "rationale": "perfect match"
            }
            ```
            """;

        // Act
        var result = JudgeResponseParser.Parse(raw);

        // Assert
        result.ParseFailed.Should().BeFalse();
        result.CauseCorrect.Should().BeTrue();
        result.ServiceCoverageScore.Should().Be(1.0);
        result.Rationale.Should().Be("perfect match");
    }

    [Fact]
    public void Parse_WhenWrappedInPlainBacktickFence_Should_StripFenceAndParse()
    {
        // Arrange
        // Same issue without the `json` language tag.
        var raw = "```\n{\"causeCorrect\":false,\"serviceCoverageScore\":0.5,\"confidenceAppropriate\":false,\"rationale\":\"partial\"}\n```";

        // Act
        var result = JudgeResponseParser.Parse(raw);

        // Assert
        result.ParseFailed.Should().BeFalse();
        result.CauseCorrect.Should().BeFalse();
        result.ServiceCoverageScore.Should().Be(0.5);
    }

    [Fact]
    public void Parse_WhenPrecededByProsePreamble_Should_ExtractFirstJsonBlock()
    {
        // Arrange
        // Judge sometimes writes: "Here's my evaluation:\n\n{...}".
        var raw = "Here is my evaluation of the agent's report:\n\n{\"causeCorrect\":true,\"serviceCoverageScore\":0.9,\"confidenceAppropriate\":true,\"rationale\":\"good\"}";

        // Act
        var result = JudgeResponseParser.Parse(raw);

        // Assert
        result.ParseFailed.Should().BeFalse();
        result.CauseCorrect.Should().BeTrue();
        result.ServiceCoverageScore.Should().Be(0.9);
    }

    [Fact]
    public void Parse_WhenJsonIsMalformed_Should_ReturnParseFailedResult()
    {
        // Arrange
        var raw = "{ this is not valid json";

        // Act
        var result = JudgeResponseParser.Parse(raw);

        // Assert
        result.ParseFailed.Should().BeTrue();
        result.CauseCorrect.Should().BeFalse();          // safe default: fail the run
        result.ServiceCoverageScore.Should().Be(0.0);
        result.ConfidenceAppropriate.Should().BeFalse();
        result.Rationale.Should().Contain("malformed");
    }

    [Fact]
    public void Parse_WhenCauseCorrectFieldMissing_Should_DefaultToFalse()
    {
        // Arrange
        // If the judge forgets a field, we err on the side of "did not pass"
        // rather than silently awarding a pass.
        var raw = "{\"serviceCoverageScore\":1.0,\"confidenceAppropriate\":true,\"rationale\":\"ok\"}";

        // Act
        var result = JudgeResponseParser.Parse(raw);

        // Assert
        result.ParseFailed.Should().BeFalse();
        result.CauseCorrect.Should().BeFalse();
    }

    [Fact]
    public void Parse_WhenServiceCoverageMissing_Should_DefaultToZero()
    {
        // Arrange
        var raw = "{\"causeCorrect\":true,\"confidenceAppropriate\":true,\"rationale\":\"ok\"}";

        // Act
        var result = JudgeResponseParser.Parse(raw);

        // Assert
        result.ServiceCoverageScore.Should().Be(0.0);
    }

    [Fact]
    public void Parse_WhenRationaleMissing_Should_DefaultToEmptyString()
    {
        // Arrange
        var raw = "{\"causeCorrect\":true,\"serviceCoverageScore\":1.0,\"confidenceAppropriate\":true}";

        // Act
        var result = JudgeResponseParser.Parse(raw);

        // Assert
        result.Rationale.Should().BeEmpty();
    }

    [Theory]
    [InlineData("{\"causeCorrect\":\"true\"}")]      // string "true" — not boolean true
    [InlineData("{\"causeCorrect\":1}")]              // number 1 — not boolean true
    [InlineData("{\"causeCorrect\":null}")]
    public void Parse_WhenCauseCorrectIsNotBooleanTrue_Should_DefaultToFalse(string raw)
    {
        // The parser uses ValueKind == JsonValueKind.True so any non-boolean
        // value (even truthy in JS terms) correctly fails. This guards against
        // a judge that emits "true" (string) and accidentally passes a bad run.

        // Act
        var result = JudgeResponseParser.Parse(raw);

        // Assert
        result.CauseCorrect.Should().BeFalse();
    }

    [Fact]
    public void Parse_WhenResponseHasLeadingAndTrailingWhitespace_Should_StillParse()
    {
        // Arrange
        var raw = "   \n\n  {\"causeCorrect\":true,\"serviceCoverageScore\":0.5,\"confidenceAppropriate\":false,\"rationale\":\"x\"}  \n";

        // Act
        var result = JudgeResponseParser.Parse(raw);

        // Assert
        result.ParseFailed.Should().BeFalse();
        result.CauseCorrect.Should().BeTrue();
    }
}
