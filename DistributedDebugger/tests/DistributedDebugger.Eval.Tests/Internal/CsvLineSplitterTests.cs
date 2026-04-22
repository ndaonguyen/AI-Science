using AwesomeAssertions;
using DistributedDebugger.Eval.Internal;
using Xunit;

namespace DistributedDebugger.Eval.Tests.Internal;

public class CsvLineSplitterTests
{
    [Fact]
    public void Split_WhenLineHasPlainFields_Should_ReturnEachField()
    {
        // Arrange
        var line = "baseline,case-1,True,5";

        // Act
        var fields = CsvLineSplitter.Split(line);

        // Assert
        fields.Should().BeEquivalentTo(new[] { "baseline", "case-1", "True", "5" }, o => o.WithStrictOrdering());
    }

    [Fact]
    public void Split_WhenFieldIsQuoted_Should_RemoveSurroundingQuotes()
    {
        // Arrange
        var line = "baseline,\"simple note\",True";

        // Act
        var fields = CsvLineSplitter.Split(line);

        // Assert
        fields.Should().BeEquivalentTo(new[] { "baseline", "simple note", "True" }, o => o.WithStrictOrdering());
    }

    [Fact]
    public void Split_WhenQuotedFieldContainsComma_Should_PreserveComma()
    {
        // Arrange
        var line = "cfg,\"first, second, third\",done";

        // Act
        var fields = CsvLineSplitter.Split(line);

        // Assert
        fields.Should().BeEquivalentTo(new[] { "cfg", "first, second, third", "done" }, o => o.WithStrictOrdering());
    }

    [Fact]
    public void Split_WhenQuotedFieldContainsEscapedQuote_Should_UnescapeToSingleQuote()
    {
        // Arrange
        // CSV dialect: a literal double quote inside a quoted field is
        // written as "". The splitter should collapse it to a single ".
        var line = "cfg,\"he said \"\"hi\"\" then left\",ok";

        // Act
        var fields = CsvLineSplitter.Split(line);

        // Assert
        fields.Should().BeEquivalentTo(new[] { "cfg", "he said \"hi\" then left", "ok" }, o => o.WithStrictOrdering());
    }

    [Fact]
    public void Split_WhenLineEndsWithEmptyField_Should_ReturnTrailingEmpty()
    {
        // Arrange
        var line = "a,b,";

        // Act
        var fields = CsvLineSplitter.Split(line);

        // Assert
        fields.Should().BeEquivalentTo(new[] { "a", "b", "" }, o => o.WithStrictOrdering());
    }

    [Fact]
    public void Split_WhenLineStartsWithEmptyField_Should_ReturnLeadingEmpty()
    {
        // Arrange
        var line = ",b,c";

        // Act
        var fields = CsvLineSplitter.Split(line);

        // Assert
        fields.Should().BeEquivalentTo(new[] { "", "b", "c" }, o => o.WithStrictOrdering());
    }

    [Fact]
    public void Split_WhenLineIsEmpty_Should_ReturnSingleEmptyField()
    {
        // Arrange
        var line = "";

        // Act
        var fields = CsvLineSplitter.Split(line);

        // Assert
        fields.Should().ContainSingle().Which.Should().BeEmpty();
    }

    [Theory]
    [InlineData("\"\"", "")]                  // empty quoted field
    [InlineData("\"x\"", "x")]                // single-char quoted field
    [InlineData("\" spaces \"", " spaces ")]  // whitespace preserved inside quotes
    public void Split_WhenSingleQuotedField_Should_ParseCorrectly(string line, string expected)
    {
        // Act
        var fields = CsvLineSplitter.Split(line);

        // Assert
        fields.Should().ContainSingle().Which.Should().Be(expected);
    }

    [Fact]
    public void Split_WhenRealisticEvalRow_Should_ParseAllColumns()
    {
        // Arrange
        // This mirrors the exact output shape of EvalCommand.WriteCsvAsync.
        var line = "baseline,opensearch-indexing-dlq-missing,True,True,1.0000,8,11200,700,3100,2.34,\"Judge: correct root cause, all services named.\"";

        // Act
        var fields = CsvLineSplitter.Split(line);

        // Assert
        fields.Should().HaveCount(11);
        fields[0].Should().Be("baseline");
        fields[1].Should().Be("opensearch-indexing-dlq-missing");
        fields[2].Should().Be("True");
        fields[10].Should().Be("Judge: correct root cause, all services named.");
    }
}
