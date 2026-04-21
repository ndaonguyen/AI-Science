using HarnessArena.Agents;
using HarnessArena.Cli;
using HarnessArena.Core.Models;
using HarnessArena.Core.Tools;
using HarnessArena.Grading;
using HarnessArena.Runner;
using HarnessArena.Tools;

// Very small arg parser — no dep on Spectre or System.CommandLine for v0.
// Usage: harness run --tasks tasks/math --config baseline [--config strict ...] [--output runs]
if (args.Length == 0 || args[0] is "-h" or "--help")
{
    PrintUsage();
    return 0;
}

if (args[0] != "run")
{
    Console.Error.WriteLine($"Unknown command: {args[0]}");
    PrintUsage();
    return 1;
}

var tasksPath = ArgValue(args, "--tasks") ?? "tasks/math";
var outputPath = ArgValue(args, "--output") ?? "runs";
var configIds = args
    .Select((a, i) => (a, i))
    .Where(t => t.a == "--config" && t.i + 1 < args.Length)
    .Select(t => args[t.i + 1])
    .ToList();

if (configIds.Count == 0)
{
    configIds.Add(Configs.Baseline.Id);
}

var configs = new List<AgentConfig>();
foreach (var id in configIds)
{
    if (!Configs.All.TryGetValue(id, out var cfg))
    {
        Console.Error.WriteLine(
            $"Unknown config '{id}'. Available: {string.Join(", ", Configs.All.Keys)}");
        return 1;
    }
    configs.Add(cfg);
}

var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine("ANTHROPIC_API_KEY is not set. Export it and retry.");
    return 1;
}

// Wire dependencies.
var registry = new ToolRegistry(new ITool[]
{
    new CalculatorTool(),
    new FinishTool(),
});
var agent = new ClaudeAgent(registry);
var grader = new ExactMatchGrader();
var writer = new TraceWriter(outputPath);
var orchestrator = new RunOrchestrator(agent, grader, writer);
var loader = new YamlTaskLoader();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

Console.WriteLine($"Loading tasks from {tasksPath}...");
var tasks = await loader.LoadAsync(tasksPath, cts.Token);
Console.WriteLine($"Loaded {tasks.Count} task(s). Running against {configs.Count} config(s).");
Console.WriteLine();

try
{
    var summaries = await orchestrator.RunSuiteAsync(
        tasks, configs, cts.Token,
        onRunCompleted: s =>
        {
            var mark = (s.Grade?.Passed ?? false) ? "✓" : "✗";
            var answer = s.Run.FinalAnswer ?? "(none)";
            Console.WriteLine(
                $"  {mark} [{s.Config.Id}] {s.Task.Id}  " +
                $"answer=\"{answer}\"  " +
                $"iter={s.Run.Usage.Iterations}  " +
                $"in={s.Run.Usage.InputTokens} out={s.Run.Usage.OutputTokens}");
        });

    Console.WriteLine();
    PrintLeaderboard(summaries);
    Console.WriteLine();
    Console.WriteLine($"Traces written to: {Path.GetFullPath(outputPath)}");
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}

static void PrintUsage()
{
    Console.WriteLine("Harness Arena — agent evaluation harness");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  harness run --tasks <folder> [--config <id> ...] [--output <folder>]");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  harness run --tasks tasks/math --config baseline");
    Console.WriteLine("  harness run --tasks tasks/math --config baseline --config strict");
    Console.WriteLine();
    Console.WriteLine($"Configs: {string.Join(", ", Configs.All.Keys)}");
}

static string? ArgValue(string[] args, string flag)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == flag) return args[i + 1];
    }
    return null;
}

static void PrintLeaderboard(IReadOnlyList<RunSummary> summaries)
{
    Console.WriteLine("Leaderboard:");
    var byConfig = summaries
        .GroupBy(s => s.Config.Id)
        .Select(g => new
        {
            Config = g.Key,
            Total = g.Count(),
            Passed = g.Count(s => s.Grade?.Passed ?? false),
            AvgIter = g.Average(s => s.Run.Usage.Iterations),
            TotalInput = g.Sum(s => s.Run.Usage.InputTokens),
            TotalOutput = g.Sum(s => s.Run.Usage.OutputTokens),
        })
        .OrderByDescending(r => (double)r.Passed / r.Total)
        .ToList();

    Console.WriteLine($"  {"Config",-16} {"Pass",-8} {"Avg iter",-10} {"In tokens",-12} {"Out tokens",-12}");
    Console.WriteLine($"  {new string('-', 60)}");
    foreach (var r in byConfig)
    {
        var pct = r.Total == 0 ? 0 : 100.0 * r.Passed / r.Total;
        Console.WriteLine(
            $"  {r.Config,-16} {$"{r.Passed}/{r.Total} ({pct:0.}%)",-8} " +
            $"{r.AvgIter,-10:0.0} {r.TotalInput,-12} {r.TotalOutput,-12}");
    }
}
