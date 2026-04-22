using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DistributedDebugger.Eval;

/// <summary>
/// Loads eval cases from YAML files. Cases live under an `eval-cases/` folder
/// and are plain human-editable YAML — same approach HarnessArena takes for
/// its task suite.
///
/// The loader is non-recursive (flat folder) on purpose. If the suite grows
/// large enough to need sub-folders, we'll add grouping then; premature
/// structure just adds friction when adding a new case.
/// </summary>
public sealed class YamlCaseLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public async Task<IReadOnlyList<EvalCase>> LoadAsync(string path, CancellationToken ct)
    {
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"Eval case folder not found: {path}");
        }

        var files = Directory.GetFiles(path, "*.yaml")
            .Concat(Directory.GetFiles(path, "*.yml"))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var cases = new List<EvalCase>(files.Count);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var yaml = await File.ReadAllTextAsync(file, ct);
            var dto = Deserializer.Deserialize<CaseYaml>(yaml)
                ?? throw new InvalidDataException($"Empty case file: {file}");

            cases.Add(ToEvalCase(dto, Path.GetFileNameWithoutExtension(file), file));
        }

        return cases;
    }

    private static EvalCase ToEvalCase(CaseYaml dto, string fallbackId, string file)
    {
        if (string.IsNullOrWhiteSpace(dto.Description))
        {
            throw new InvalidDataException($"Case '{file}' missing required field 'description'.");
        }

        if (dto.Truth is null)
        {
            throw new InvalidDataException($"Case '{file}' missing required 'truth' section.");
        }

        var truth = new GroundTruth(
            ExpectedCause: dto.Truth.ExpectedCause
                ?? throw new InvalidDataException($"Case '{file}' truth.expectedCause missing."),
            ExpectedServices: dto.Truth.ExpectedServices ?? new List<string>(),
            MustMentionKeywords: dto.Truth.MustMentionKeywords ?? new List<string>(),
            MustNotClaim: dto.Truth.MustNotClaim ?? new List<string>(),
            MinConfidence: dto.Truth.MinConfidence ?? "Medium");

        var logs = (dto.ScriptedLogs ?? new List<ScriptedLogYaml>())
            .Select(l => new ScriptedLogResponse(
                Service: l.Service ?? "",
                MatchesKeyword: l.MatchesKeyword ?? "",
                Logs: l.Logs ?? ""))
            .ToList();

        var data = (dto.ScriptedData ?? new List<ScriptedDataYaml>())
            .Select(d => new ScriptedDataResponse(
                ToolName: d.Tool ?? "",
                MatchesAny: d.Matches ?? "",
                Response: d.Response ?? ""))
            .ToList();

        return new EvalCase(
            Id: dto.Id ?? fallbackId,
            Description: dto.Description,
            TicketId: dto.TicketId,
            Truth: truth,
            LogResponses: logs,
            DataResponses: data);
    }

    // ---- YAML DTO shapes ----

    private sealed class CaseYaml
    {
        public string? Id { get; set; }
        public string? Description { get; set; }
        public string? TicketId { get; set; }
        public TruthYaml? Truth { get; set; }
        public List<ScriptedLogYaml>? ScriptedLogs { get; set; }
        public List<ScriptedDataYaml>? ScriptedData { get; set; }
    }

    private sealed class TruthYaml
    {
        public string? ExpectedCause { get; set; }
        public List<string>? ExpectedServices { get; set; }
        public List<string>? MustMentionKeywords { get; set; }
        public List<string>? MustNotClaim { get; set; }
        public string? MinConfidence { get; set; }
    }

    private sealed class ScriptedLogYaml
    {
        public string? Service { get; set; }
        public string? MatchesKeyword { get; set; }
        public string? Logs { get; set; }
    }

    private sealed class ScriptedDataYaml
    {
        public string? Tool { get; set; }
        public string? Matches { get; set; }
        public string? Response { get; set; }
    }
}
