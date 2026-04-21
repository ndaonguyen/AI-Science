using HarnessArena.Core.Models;
using HarnessArena.Core.Tasks;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace HarnessArena.Runner;

/// <summary>
/// Reads every *.yaml / *.yml file under the given folder as a task definition.
/// Non-recursive for v0 — flat folder per domain.
/// </summary>
public sealed class YamlTaskLoader : ITaskLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public async Task<IReadOnlyList<TaskDefinition>> LoadAsync(
        string path,
        CancellationToken ct)
    {
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"Task folder not found: {path}");
        }

        var files = Directory.GetFiles(path, "*.yaml")
            .Concat(Directory.GetFiles(path, "*.yml"))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var tasks = new List<TaskDefinition>(files.Count);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var yaml = await File.ReadAllTextAsync(file, ct);
            var dto = Deserializer.Deserialize<TaskYaml>(yaml)
                ?? throw new InvalidDataException($"Empty task file: {file}");

            tasks.Add(new TaskDefinition(
                Id: dto.Id ?? Path.GetFileNameWithoutExtension(file),
                Domain: dto.Domain ?? "unknown",
                Prompt: dto.Prompt ?? throw new InvalidDataException(
                    $"Task '{file}' missing required field 'prompt'."),
                ExpectedAnswer: dto.ExpectedAnswer ?? throw new InvalidDataException(
                    $"Task '{file}' missing required field 'expectedAnswer'."),
                Difficulty: dto.Difficulty ?? 1,
                MaxIterations: dto.MaxIterations ?? 10,
                Metadata: dto.Metadata));
        }

        return tasks;
    }

    private sealed class TaskYaml
    {
        public string? Id { get; set; }
        public string? Domain { get; set; }
        public string? Prompt { get; set; }
        public string? ExpectedAnswer { get; set; }
        public int? Difficulty { get; set; }
        public int? MaxIterations { get; set; }
        public Dictionary<string, string>? Metadata { get; set; }
    }
}
