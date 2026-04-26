using DistributedDebugger.Eval;
using DistributedDebugger.Eval.V3;
using DistributedDebugger.Web.V3;

namespace DistributedDebugger.Cli;

/// <summary>
/// The `debugger eval-v3` subcommand. Loads V3 eval cases from YAML, runs
/// them through <see cref="LogAnalyzer"/> against one or more named configs,
/// then prints a small leaderboard (pass-rate, average tokens, RAG usage).
///
/// Default cases dir: eval-cases-v3/. Default configs: baseline, no-rag.
/// Useful workflow:
///   debugger eval-v3                                   # both configs, default cases
///   debugger eval-v3 --config baseline                 # just baseline
///   debugger eval-v3 --config baseline --config no-rag # explicit A/B
/// </summary>
public static class EvalV3Command
{
    public static async Task<int> RunAsync(string[] args, string openAiKey, CancellationToken ct)
    {
        var casesPath = ArgValue(args, "--cases") ?? ResolveDefaultPath("eval-cases-v3");
        var judgeModel = ArgValue(args, "--judge-model") ?? "gpt-4o";
        var requestedConfigs = args
            .Select((a, i) => (a, i))
            .Where(t => t.a == "--config" && t.i + 1 < args.Length)
            .Select(t => args[t.i + 1])
            .ToList();

        var configs = BuildConfigs(requestedConfigs);
        if (configs.Count == 0)
        {
            Console.Error.WriteLine(
                "No known configs matched. Available: baseline, no-rag");
            return 1;
        }

        Console.WriteLine($"Loading V3 eval cases from {casesPath}...");
        var loader = new V3CaseLoader();
        IReadOnlyList<EvalCaseV3> cases;
        try
        {
            cases = await loader.LoadAsync(casesPath, ct);
        }
        catch (DirectoryNotFoundException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        if (cases.Count == 0)
        {
            Console.Error.WriteLine($"No .yaml or .yml cases found in {casesPath}.");
            return 1;
        }

        Console.WriteLine($"Loaded {cases.Count} V3 case(s). " +
                          $"Running against {configs.Count} config(s). " +
                          $"Judge model: {judgeModel}.");
        Console.WriteLine();

        // Schemas are bundled the same way the live endpoint does it — load
        // once at startup from AppContext.BaseDirectory/schemas. Eval results
        // include the same context the user sees in production.
        var schemas = new SchemaLoader();
        Console.WriteLine($"Loaded {schemas.All.Count} schema doc(s) " +
                          $"(prepended to every analyzer prompt).");
        Console.WriteLine();

        var grader = new LlmAsJudgeGrader(openAiKey, judgeModel);
        var runner = new V3RegressionRunner(grader, openAiKey, schemas.All);

        var rows = await runner.RunAsync(
            cases, configs,
            onRowCompleted: PrintRow,
            ct: ct);

        Console.WriteLine();
        PrintLeaderboard(rows, configs);
        return rows.Any(r => !r.Passed) ? 2 : 0;
    }

    private static IReadOnlyList<V3RegressionConfig> BuildConfigs(IReadOnlyList<string> requested)
    {
        var known = new Dictionary<string, V3RegressionConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["baseline"] = V3RegressionConfig.Baseline,
            ["no-rag"]   = V3RegressionConfig.NoRag,
        };

        if (requested.Count == 0)
            return known.Values.ToList();

        var picked = new List<V3RegressionConfig>();
        foreach (var id in requested)
        {
            if (known.TryGetValue(id, out var cfg))
                picked.Add(cfg);
            else
                Console.Error.WriteLine($"Unknown config '{id}', skipping.");
        }
        return picked;
    }

    private static void PrintRow(V3RegressionRow r)
    {
        var status = r.Passed ? "✓ PASS" : "✗ FAIL";
        var rag = r.RagUsed ? $"RAG {r.RagKeptCount}/{r.RagFromCount}" : "no RAG";
        Console.WriteLine(
            $"  [{r.ConfigId}] {status}  {r.CaseId}  " +
            $"({rag}, {r.InputTokens}+{r.OutputTokens} tok, {r.WallTime.TotalSeconds:F1}s)");
        if (r.Error is not null) Console.WriteLine($"      ! {r.Error}");
        if (r.Grade is { } g && !string.IsNullOrWhiteSpace(g.JudgeRationale))
            Console.WriteLine($"      → {g.JudgeRationale}");
    }

    private static void PrintLeaderboard(
        IReadOnlyList<V3RegressionRow> rows,
        IReadOnlyList<V3RegressionConfig> configs)
    {
        Console.WriteLine("=== Leaderboard ===");
        foreach (var cfg in configs)
        {
            var configRows = rows.Where(r => r.ConfigId == cfg.Id).ToList();
            if (configRows.Count == 0) continue;
            var passes = configRows.Count(r => r.Passed);
            var totalIn = configRows.Sum(r => r.InputTokens);
            var totalOut = configRows.Sum(r => r.OutputTokens);
            var avgWall = configRows.Average(r => r.WallTime.TotalSeconds);
            Console.WriteLine(
                $"  {cfg.Id,-10}  {passes}/{configRows.Count} pass  " +
                $"({totalIn:N0} in / {totalOut:N0} out, avg {avgWall:F1}s/case)");
        }
    }

    private static string? ArgValue(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == flag) return args[i + 1];
        return null;
    }

    private static string ResolveDefaultPath(string folder)
    {
        // Walk up from cwd until we find the folder OR hit the root. Lets
        // `dotnet run -- eval-v3` work from anywhere in the repo, not just
        // the repo root.
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, folder);
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return folder;
    }
}
