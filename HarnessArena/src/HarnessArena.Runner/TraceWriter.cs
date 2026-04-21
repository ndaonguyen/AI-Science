using System.Text.Json;
using System.Text.Json.Serialization;
using HarnessArena.Core.Grading;
using HarnessArena.Core.Models;

namespace HarnessArena.Runner;

/// <summary>
/// Writes one JSON file per run to the output folder. Filename pattern:
/// {yyyyMMdd-HHmmss}-{taskId}-{configId}.json so ls -l sorts chronologically.
/// </summary>
public sealed class TraceWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _outputDir;

    public TraceWriter(string outputDir)
    {
        _outputDir = outputDir;
        Directory.CreateDirectory(_outputDir);
    }

    public async Task<string> WriteAsync(
        Run run,
        GradeResult? grade,
        CancellationToken ct)
    {
        var ts = run.StartedAt.ToString("yyyyMMdd-HHmmss");
        var filename = $"{ts}-{run.TaskId}-{run.AgentConfigId}.json";
        var path = Path.Combine(_outputDir, filename);

        var payload = new RunOutput(run, grade);
        await using var fs = File.Create(path);
        await JsonSerializer.SerializeAsync(fs, payload, JsonOptions, ct);
        return path;
    }

    private sealed record RunOutput(Run Run, GradeResult? Grade);
}
