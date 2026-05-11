using BugMemory.Application.Abstractions;
using BugMemory.Application.Dtos;

namespace BugMemory.Application.UseCases;

public sealed record AnswerClarificationCommand(
    string Question,
    string DraftContext,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> AffectedServices);

public sealed class AnswerClarificationUseCase
{
    private readonly ILlmService _llm;
    private readonly IRepoCodeScanner _scanner;

    public AnswerClarificationUseCase(ILlmService llm, IRepoCodeScanner scanner)
    {
        _llm = llm;
        _scanner = scanner;
    }

    public async Task<ClarificationAnswerDto> ExecuteAsync(AnswerClarificationCommand command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.Question) || command.Question.Trim().Length < 5)
        {
            throw new ArgumentException("Question is too short", nameof(command));
        }

        var serviceCandidates = command.AffectedServices
            .Concat(command.Tags)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var keywords = command.Tags
            .Concat(ExtractKeywords(command.Question))
            .Concat(ExtractKeywords(command.DraftContext))
            .ToList();

        var scan = _scanner.Scan(serviceCandidates, keywords, ct);

        var answer = await _llm.AnswerClarificationAsync(
            command.Question,
            command.DraftContext,
            command.Tags,
            command.AffectedServices,
            scan.Snapshot,
            ct);

        return new ClarificationAnswerDto(
            answer.Answer,
            answer.Evidence,
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
