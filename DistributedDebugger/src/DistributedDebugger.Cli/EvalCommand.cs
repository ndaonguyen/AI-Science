using DistributedDebugger.Agent;
using DistributedDebugger.Eval;

namespace DistributedDebugger.Cli;

/// <summary>
/// The `debugger eval` subcommand. Loads cases, runs them against one or
/// more named configs, then renders a leaderboard. Same basic shape as
/// HarnessArena's leaderboard — pass-rate, average iterations, token spend.
/// </summary>
public static class EvalCommand
{
    public static async Task<int> RunAsync(string[] args, string openAiKey, CancellationToken ct)
    {
        var casesPath = ArgValue(args, "--cases") ?? "eval-cases";
        var outputPath = ArgValue(args, "--output") ?? "eval-results";
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
                $"No known configs matched. Available: {string.Join(", ", KnownConfigs.Keys)}");
            return 1;
        }

        Console.WriteLine($"Loading eval cases from {casesPath}...");
        var loader = new YamlCaseLoader();
        IReadOnlyList<EvalCase> cases;
        try
        {
            cases = await loader.LoadAsync(casesPath, ct);
        }
        catch (DirectoryNotFoundException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        Console.WriteLine($"Loaded {cases.Count} case(s). " +
                          $"Running against {configs.Count} config(s). " +
                          $"Judge model: {judgeModel}.");
        Console.WriteLine();

        var grader = new LlmAsJudgeGrader(openAiKey, judgeModel);
        var runner = new RegressionRunner(grader, openAiKey);

        var rows = await runner.RunAsync(
            cases, configs,
            onRowCompleted: PrintRowAsStreaming,
            ct: ct);

        Console.WriteLine();
        PrintLeaderboard(rows);
        Console.WriteLine();

        // Persist to CSV so you can diff against a previous run — this is the
        // foundation for "is prompt change X a regression?" workflows.
        Directory.CreateDirectory(outputPath);
        var csvPath = Path.Combine(
            outputPath, $"eval-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.csv");
        await WriteCsvAsync(csvPath, rows, ct);
        Console.WriteLine($"Results written to: {Path.GetFullPath(csvPath)}");

        return 0;
    }

    /// <summary>
    /// Built-in configs for side-by-side regression. New ones can be added
    /// here; future iteration could move this to YAML.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, NamedConfig> KnownConfigs =
        new Dictionary<string, NamedConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["baseline"] = new NamedConfig(
                "baseline",
                new AgentConfig(Model: "gpt-4o-mini", MaxIterations: 12, Temperature: 0.0)),
            ["short"] = new NamedConfig(
                "short",
                new AgentConfig(Model: "gpt-4o-mini", MaxIterations: 6, Temperature: 0.0)),
            ["big-model"] = new NamedConfig(
                "big-model",
                new AgentConfig(Model: "gpt-4o", MaxIterations: 12, Temperature: 0.0)),
        };

    private static List<NamedConfig> BuildConfigs(List<string> requested)
    {
        if (requested.Count == 0)
        {
            return new List<NamedConfig> { KnownConfigs["baseline"] };
        }

        var list = new List<NamedConfig>();
        foreach (var id in requested)
        {
            if (KnownConfigs.TryGetValue(id, out var cfg))
            {
                list.Add(cfg);
            }
            else
            {
                Console.Error.WriteLine(
                    $"Unknown config '{id}'. Available: {string.Join(", ", KnownConfigs.Keys)}.");
            }
        }
        return list;
    }

    private static void PrintRowAsStreaming(RegressionRow row)
    {
        var mark = row.Passed ? "✓" : "✗";
        Console.WriteLine(
            $"  {mark} [{row.ConfigName,-12}] {row.CaseId,-40} " +
            $"cause={(row.CauseCorrect ? "yes" : "no"),-3} " +
            $"svc={row.ServiceCoverage:0.00} " +
            $"iter={row.Iterations,-2} " +
            $"in/out/judge={row.InputTokens}/{row.OutputTokens}/{row.JudgeTokens} " +
            $"{row.Duration.TotalSeconds:0.0}s");
    }

    private static void PrintLeaderboard(IReadOnlyList<RegressionRow> rows)
    {
        Console.WriteLine("Leaderboard (by config):");
        var byConfig = rows
            .GroupBy(r => r.ConfigName)
            .Select(g => new
            {
                Config = g.Key,
                Total = g.Count(),
                Passed = g.Count(r => r.Passed),
                AvgServiceCoverage = g.Average(r => r.ServiceCoverage),
                AvgIter = g.Average(r => r.Iterations),
                TotalIn = g.Sum(r => r.InputTokens),
                TotalOut = g.Sum(r => r.OutputTokens),
                TotalJudge = g.Sum(r => r.JudgeTokens),
            })
            .OrderByDescending(r => (double)r.Passed / r.Total)
            .ToList();

        Console.WriteLine(
            $"  {"Config",-14} {"Pass",-12} {"SvcCov",-7} {"AvgIter",-8} " +
            $"{"Tokens (in/out/judge)",-30}");
        Console.WriteLine("  " + new string('-', 74));
        foreach (var r in byConfig)
        {
            var pct = r.Total == 0 ? 0 : 100.0 * r.Passed / r.Total;
            Console.WriteLine(
                $"  {r.Config,-14} {$"{r.Passed}/{r.Total} ({pct:0.}%)",-12} " +
                $"{r.AvgServiceCoverage,-7:0.00} {r.AvgIter,-8:0.0} " +
                $"{r.TotalIn}/{r.TotalOut}/{r.TotalJudge}");
        }
    }

    private static async Task WriteCsvAsync(string path, IReadOnlyList<RegressionRow> rows, CancellationToken ct)
    {
        // Minimal CSV — escape double quotes in Notes only; other fields are
        // safe numeric / enum values. Adding a full CSV library for this would
        // be overkill.
        using var writer = new StreamWriter(path);
        await writer.WriteLineAsync(
            "configName,caseId,passed,causeCorrect,serviceCoverage,iterations," +
            "inputTokens,outputTokens,judgeTokens,durationSeconds,notes");
        foreach (var r in rows)
        {
            var notes = (r.Notes ?? "").Replace("\"", "\"\"").Replace("\n", " ");
            await writer.WriteLineAsync(
                $"{r.ConfigName},{r.CaseId},{r.Passed},{r.CauseCorrect}," +
                $"{r.ServiceCoverage:0.0000},{r.Iterations},{r.InputTokens}," +
                $"{r.OutputTokens},{r.JudgeTokens},{r.Duration.TotalSeconds:0.00}," +
                $"\"{notes}\"");
        }
    }

    private static string? ArgValue(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == flag) return args[i + 1];
        }
        return null;
    }
}
