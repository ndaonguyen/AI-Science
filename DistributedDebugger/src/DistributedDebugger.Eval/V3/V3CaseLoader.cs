using DistributedDebugger.Web.V3;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DistributedDebugger.Eval.V3;

/// <summary>
/// Loads V3 eval cases from YAML. Schema is intentionally flat: bug context
/// at the top, then a sequence of log entries, then optional evidence, then
/// truth fields. Mirrors the prompt the LogAnalyzer would normally see, so
/// authoring a case is mostly copy-paste from a real investigation.
///
/// Example case shape:
///   id: assetid-guid-deserialization
///   description: |
///     Unexpected Execution Error rendering activity blocks
///   ticketId: COCO-689
///   logs:
///     - timestamp: 2026-04-22T01:50:15.161Z
///       service: authoring-service
///       logGroup: /aws/ecs/authoring-service
///       message: "Unexpected Execution Error at /contentRenderingActivity/blocks"
///   evidence:
///     - kind: mongo
///       title: ComponentBlockModel — _id 67abcd
///       command: db.blocks.findOne({_id: UUID('67abcd...')})
///       content: |
///         { "_t": "ComponentBlockModel", "components": [{ ... }] }
///   truth:
///     expectedCause: ImageComponentModel.assetId is null but declared non-nullable Guid
///     expectedServices: [authoring-service]
///     mustMention: [assetId, ImageComponentModel, non-nullable]
///     mustNotClaim: [network, authentication, permission]
/// </summary>
public sealed class V3CaseLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public async Task<IReadOnlyList<EvalCaseV3>> LoadAsync(string path, CancellationToken ct)
    {
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"V3 eval case folder not found: {path}");
        }

        var files = Directory.GetFiles(path, "*.yaml")
            .Concat(Directory.GetFiles(path, "*.yml"))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var cases = new List<EvalCaseV3>(files.Count);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var yaml = await File.ReadAllTextAsync(file, ct);
            var dto = Deserializer.Deserialize<CaseYamlV3>(yaml)
                ?? throw new InvalidDataException($"Empty case file: {file}");

            cases.Add(ToCase(dto, Path.GetFileNameWithoutExtension(file), file));
        }

        return cases;
    }

    private static EvalCaseV3 ToCase(CaseYamlV3 dto, string fallbackId, string file)
    {
        if (string.IsNullOrWhiteSpace(dto.Description))
            throw new InvalidDataException($"Case '{file}' missing required field 'description'.");
        if (dto.Logs is null || dto.Logs.Count == 0)
            throw new InvalidDataException($"Case '{file}' must include at least one log entry.");
        if (dto.Truth is null)
            throw new InvalidDataException($"Case '{file}' missing required 'truth' section.");

        var logs = dto.Logs.Select(l => new LogRecord(
                Service: l.Service ?? "(unknown)",
                LogGroup: l.LogGroup ?? "",
                Timestamp: l.Timestamp,
                Message: l.Message ?? "",
                EventId: null))
            .ToList();

        var evidence = (dto.Evidence ?? new List<EvidenceYaml>())
            .Select(e => new EvidenceItem(
                Kind: e.Kind ?? "note",
                Title: e.Title ?? "",
                Command: e.Command,
                Content: e.Content ?? ""))
            .ToList();

        var truth = new GroundTruth(
            ExpectedCause: dto.Truth.ExpectedCause ?? "",
            ExpectedServices: dto.Truth.ExpectedServices ?? new List<string>(),
            MustMentionKeywords: dto.Truth.MustMention ?? new List<string>(),
            MustNotClaim: dto.Truth.MustNotClaim ?? new List<string>(),
            MinConfidence: dto.Truth.MinConfidence ?? "Medium");

        return new EvalCaseV3(
            Id: dto.Id ?? fallbackId,
            Description: dto.Description.Trim(),
            TicketId: dto.TicketId,
            Logs: logs,
            Evidence: evidence,
            Truth: truth);
    }

    // ---- YAML DTOs ----
    // Separate from EvalCaseV3 so the YAML schema can evolve without breaking
    // the in-memory shape (or vice versa).

    private sealed class CaseYamlV3
    {
        public string? Id { get; set; }
        public string? Description { get; set; }
        public string? TicketId { get; set; }
        public List<LogYaml>? Logs { get; set; }
        public List<EvidenceYaml>? Evidence { get; set; }
        public TruthYaml? Truth { get; set; }
    }

    private sealed class LogYaml
    {
        public DateTimeOffset Timestamp { get; set; }
        public string? Service { get; set; }
        public string? LogGroup { get; set; }
        public string? Message { get; set; }
    }

    private sealed class EvidenceYaml
    {
        public string? Kind { get; set; }
        public string? Title { get; set; }
        public string? Command { get; set; }
        public string? Content { get; set; }
    }

    private sealed class TruthYaml
    {
        public string? ExpectedCause { get; set; }
        public List<string>? ExpectedServices { get; set; }
        public List<string>? MustMention { get; set; }
        public List<string>? MustNotClaim { get; set; }
        public string? MinConfidence { get; set; }
    }
}
