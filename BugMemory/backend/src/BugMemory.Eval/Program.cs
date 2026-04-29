using BugMemory.Eval;
using Microsoft.Extensions.Configuration;

// CLI:
//   dotnet run --project src/BugMemory.Eval -- [--cases <dir>] [--seed <file>] [--config <id>]
//
// Default cases dir: eval/cases (relative to repo root). Default seed
// file: eval/seed-bugs.yaml. Default configs: all three (baseline,
// cheap-model, narrow-retrieval).
//
// OpenAI API key resolution mirrors the API project's: configuration
// system reads OpenAi:ApiKey from appsettings.Development.json, user
// secrets, or environment variables (prefixed with OpenAi__ApiKey).

var (casesDir, seedPath, configIds) = ParseArgs(args);

var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile("appsettings.Development.json", optional: true)
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables()
    .Build();

var openAiKey = configuration["OpenAi:ApiKey"];
if (string.IsNullOrWhiteSpace(openAiKey))
{
    Console.Error.WriteLine(
        "ERROR: OpenAi:ApiKey not found. Set it via appsettings.Development.json, " +
        "dotnet user-secrets, or the OpenAi__ApiKey environment variable.");
    return 1;
}
var qdrantBaseUrl = configuration["Qdrant:BaseUrl"] ?? "http://localhost:6333";

// Load the seed corpus and cases up front. Loading errors should fail
// the run before we spend any tokens.
IReadOnlyList<SeedBug> seed;
IReadOnlyList<EvalCase> cases;
try
{
    seed = EvalLoader.LoadSeed(seedPath);
    cases = EvalLoader.LoadCases(casesDir);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR loading harness inputs: {ex.GetType().Name}: {ex.Message}");
    return 1;
}
Console.WriteLine($"[harness] loaded {seed.Count} seed bugs, {cases.Count} cases.");

var configs = ResolveConfigs(configIds);
if (configs.Count == 0)
{
    Console.Error.WriteLine("ERROR: no valid configs specified. Available: baseline, cheap-model, narrow-retrieval.");
    return 1;
}

var runner = new HarnessRunner(openAiKey, qdrantBaseUrl);
IReadOnlyList<HarnessRow> rows;
try
{
    rows = await runner.RunAsync(seed, cases, configs, PrintRow);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR during harness run: {ex.GetType().Name}: {ex.Message}");
    return 1;
}

PrintLeaderboard(rows, configs);
return 0;

// ---- helpers ----

static (string CasesDir, string SeedPath, IReadOnlyList<string> ConfigIds) ParseArgs(string[] args)
{
    var casesDir = "eval/cases";
    var seedPath = "eval/seed-bugs.yaml";
    var configIds = new List<string>();
    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--cases" when i + 1 < args.Length:
                casesDir = args[++i];
                break;
            case "--seed" when i + 1 < args.Length:
                seedPath = args[++i];
                break;
            case "--config" when i + 1 < args.Length:
                configIds.Add(args[++i]);
                break;
        }
    }
    return (casesDir, seedPath, configIds);
}

static IReadOnlyList<EvalConfig> ResolveConfigs(IReadOnlyList<string> requested)
{
    var known = new Dictionary<string, EvalConfig>(StringComparer.OrdinalIgnoreCase)
    {
        ["baseline"]         = EvalConfig.Baseline,
        ["cheap-model"]      = EvalConfig.CheapModel,
        ["narrow-retrieval"] = EvalConfig.NarrowRetrieval,
    };
    if (requested.Count == 0) return known.Values.ToList();

    var picked = new List<EvalConfig>();
    foreach (var id in requested)
    {
        if (known.TryGetValue(id, out var cfg))
            picked.Add(cfg);
        else
            Console.Error.WriteLine(
                $"Unknown config '{id}', skipping. Available: {string.Join(", ", known.Keys)}");
    }
    return picked;
}

static void PrintRow(HarnessRow r)
{
    var ret = r.Retrieval.Passed ? "ret OK " : "ret BAD";
    var ans = r.Answer.Passed ? "ans OK " : "ans BAD";
    Console.WriteLine(
        $"  [{r.ConfigId,-20}] {r.CaseId,-30} {ret} (P={r.Retrieval.Precision:F2} R={r.Retrieval.Recall:F2})  {ans}");
    if (!string.IsNullOrWhiteSpace(r.Answer.Rationale))
        Console.WriteLine($"      → {r.Answer.Rationale}");
    if (r.Error is not null)
        Console.WriteLine($"      ! {r.Error}");
}

static void PrintLeaderboard(
    IReadOnlyList<HarnessRow> rows,
    IReadOnlyList<EvalConfig> configs)
{
    Console.WriteLine();
    Console.WriteLine("=== Leaderboard ===");
    Console.WriteLine($"{"config",-20} {"retrieval-pass",-15} {"answer-pass",-15} {"avg-recall",-12} {"avg-precision",-15}");
    foreach (var cfg in configs)
    {
        var configRows = rows.Where(r => r.ConfigId == cfg.Id).ToList();
        if (configRows.Count == 0) continue;

        var retPass = configRows.Count(r => r.Retrieval.Passed);
        var ansPass = configRows.Count(r => r.Answer.Passed);
        var avgRecall = configRows.Average(r => r.Retrieval.Recall);
        var avgPrec = configRows.Average(r => r.Retrieval.Precision);

        Console.WriteLine(
            $"{cfg.Id,-20} {retPass}/{configRows.Count,-13} {ansPass}/{configRows.Count,-13} " +
            $"{avgRecall,-12:F2} {avgPrec,-15:F2}");
    }
}

public partial class Program { }
