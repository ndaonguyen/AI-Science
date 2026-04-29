using BugMemory.Application.Abstractions;
using BugMemory.Application.Dtos;

namespace BugMemory.Application.UseCases;

public sealed record ExtractBugFieldsCommand(string SourceText);

public sealed class ExtractBugFieldsUseCase
{
    private readonly ILlmService _llm;

    public ExtractBugFieldsUseCase(ILlmService llm) => _llm = llm;

    public async Task<ExtractionResultDto> ExecuteAsync(ExtractBugFieldsCommand command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.SourceText) || command.SourceText.Trim().Length < 30)
        {
            throw new ArgumentException("Source text is too short to extract from", nameof(command));
        }

        var fields = await _llm.ExtractBugFieldsAsync(command.SourceText, ct);
        return new ExtractionResultDto(fields.Title, fields.Tags, fields.Context, fields.RootCause, fields.Solution);
    }
}
