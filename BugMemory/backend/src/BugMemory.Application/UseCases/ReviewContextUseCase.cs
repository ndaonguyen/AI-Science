using BugMemory.Application.Abstractions;
using BugMemory.Application.Dtos;

namespace BugMemory.Application.UseCases;

public sealed record ReviewContextCommand(
    string Context,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> AffectedServices);

public sealed class ReviewContextUseCase
{
    private readonly ILlmService _llm;
    private readonly IRepoCodeScanner _scanner;

    public ReviewContextUseCase(ILlmService llm, IRepoCodeScanner scanner)
    {
        _llm = llm;
        _scanner = scanner;
    }

    public async Task<ContextReviewDto> ExecuteAsync(ReviewContextCommand command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.Context) || command.Context.Trim().Length < 10)
        {
            throw new ArgumentException("Context is too short to review", nameof(command));
        }

        var serviceCandidates = command.AffectedServices
            .Concat(command.Tags)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var keywords = command.Tags
            .Concat(ExtractKeywords(command.Context))
            .ToList();

        var scan = _scanner.Scan(serviceCandidates, keywords, ct);

        var review = await _llm.ReviewContextAsync(
            command.Context,
            command.Tags,
            command.AffectedServices,
            scan.Snapshot,
            ct);

        return new ContextReviewDto(
            review.Summary,
            review.Suggestions,
            review.RewrittenContext,
            scan.Resolved.Select(r => r.ServiceName).ToList(),
            scan.Unresolved);
    }

    private static IEnumerable<string> ExtractKeywords(string text)
    {
        return text
            .Split(new[] { ' ', '\t', '\n', '\r', '.', ',', ';', ':', '(', ')', '"', '\'' },
                StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 5 && w.Any(char.IsLetter))
            .Where(w => char.IsUpper(w[0]) || w.Contains('-') || w.Contains('_'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10);
    }
}
