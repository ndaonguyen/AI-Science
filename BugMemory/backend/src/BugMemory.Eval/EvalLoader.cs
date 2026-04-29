using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace BugMemory.Eval;

/// <summary>
/// Loads YAML files from the eval/ folder. Two file kinds:
///
///   - seed-bugs.yaml : a list of <see cref="SeedBug"/> indexed once per run
///   - cases/*.yaml   : one <see cref="EvalCase"/> per file, with a
///                      stable id matching the file basename
///
/// Format choice: YAML over JSON because cases have multi-line prose
/// fields (Question, AnswerCriteria) that are noisy in JSON. Same shape
/// the DistributedDebugger eval harness uses — keeps muscle memory across
/// projects.
/// </summary>
public static class EvalLoader
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()  // forward-compatible: YAML can have
                                       // newer fields than the C# shape
        .Build();

    public static IReadOnlyList<SeedBug> LoadSeed(string path)
    {
        var text = File.ReadAllText(path);
        var seed = Yaml.Deserialize<SeedFile>(text)
                   ?? throw new InvalidOperationException(
                       $"Could not parse seed file at {path}");
        return seed.Bugs;
    }

    public static IReadOnlyList<EvalCase> LoadCases(string casesDir)
    {
        if (!Directory.Exists(casesDir))
            throw new DirectoryNotFoundException($"Cases directory not found: {casesDir}");

        var files = Directory.EnumerateFiles(casesDir, "*.yaml")
            .Concat(Directory.EnumerateFiles(casesDir, "*.yml"))
            .OrderBy(f => f)
            .ToList();

        var cases = new List<EvalCase>(files.Count);
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var c = Yaml.Deserialize<EvalCase>(text);
            if (c is null)
            {
                Console.Error.WriteLine($"[harness] skipping unparseable case: {file}");
                continue;
            }
            // If the case file forgot to set Id, default to the filename
            // without extension. Keeps cases findable in the leaderboard
            // even when the author forgets.
            if (string.IsNullOrWhiteSpace(c.Id))
                c.Id = Path.GetFileNameWithoutExtension(file);
            cases.Add(c);
        }
        return cases;
    }

    private sealed class SeedFile
    {
        public List<SeedBug> Bugs { get; set; } = new();
    }
}
