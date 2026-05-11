using BugMemory.Application.Abstractions;
using BugMemory.Application.Dtos;

namespace BugMemory.Application.UseCases;

public sealed record ClarificationInput(string Question, string Answer);

public sealed record RewriteContextCommand(
    string OriginalContext,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> AffectedServices,
    IReadOnlyList<ClarificationInput> Clarifications);

public sealed class RewriteContextUseCase
{
    private readonly ILlmService _llm;
    private readonly IRepoCodeScanner _scanner;

    public RewriteContextUseCase(ILlmService llm, IRepoCodeScanner scanner)
    {
        _llm = llm;
        _scanner = scanner;
    }

    public async Task<RewrittenContextDto> ExecuteAsync(RewriteContextCommand command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.OriginalContext) || command.OriginalContext.Trim().Length < 10)
        {
            throw new ArgumentException("Original context is too short to rewrite", nameof(command));
        }

        var confirmed = command.Clarifications
            .Where(c => !string.IsNullOrWhiteSpace(c.Question) && !string.IsNullOrWhiteSpace(c.Answer))
            .Select(c => new ConfirmedClarification(c.Question.Trim(), c.Answer.Trim()))
            .ToList();

        if (confirmed.Count == 0)
        {
            throw new ArgumentException("Provide at least one answered clarification", nameof(command));
        }

        var serviceCandidates = command.AffectedServices
            .Concat(command.Tags)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var keywords = command.Tags
            .Concat(ExtractKeywords(command.OriginalContext))
            .Concat(confirmed.SelectMany(c => ExtractKeywords(c.Answer)))
            .ToList();

        var scan = _scanner.Scan(serviceCandidates, keywords, ct);

        var rewritten = await _llm.RewriteContextWithAnswersAsync(
            command.OriginalContext,
            command.Tags,
            command.AffectedServices,
            confirmed,
            scan.Snapshot,
            ct);

        return new RewrittenContextDto(
            rewritten,
            scan.Resolved.Select(r => r.ServiceName).ToList(),
            scan.Unresolved);
    }

    private static IEnumerable<string> ExtractKeywords(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        return text
            .Split(new[] { ' ', '\t', '\n', '\r', '.', ',', ';', ':', '(', ')', '"', '\'', '?', '!' },
                StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 4 && w.Any(char.IsLetter))
            .Where(w => char.IsUpper(w[0]) || w.Contains('-') || w.Contains('_'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10);
    }
}
