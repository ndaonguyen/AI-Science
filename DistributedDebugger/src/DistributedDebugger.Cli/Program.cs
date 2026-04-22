using System.Threading.Channels;
using DistributedDebugger.Agent;
using DistributedDebugger.Cli;
using DistributedDebugger.Core.Models;
using DistributedDebugger.Core.Tools;
using DistributedDebugger.Tools;
using DistributedDebugger.Tools.CloudWatch;
using DistributedDebugger.Tools.HumanLoop;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    PrintUsage();
    return 0;
}

// Dispatch on subcommand. `investigate` is the default (real-run) path;
// `eval` replays recorded cases against the grader for regression testing.
if (args[0] == "eval")
{
    var openAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    if (string.IsNullOrWhiteSpace(openAiKey))
    {
        Console.Error.WriteLine("OPENAI_API_KEY is not set.");
        return 1;
    }

    using var evalCts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        evalCts.Cancel();
    };

    return await EvalCommand.RunAsync(args, openAiKey, evalCts.Token);
}

if (args[0] != "investigate")
{
    Console.Error.WriteLine($"Unknown command: {args[0]}");
    PrintUsage();
    return 1;
}

var description = ArgValue(args, "--desc");
var ticketId = ArgValue(args, "--ticket");
var descFile = ArgValue(args, "--desc-file");
var outputDir = ArgValue(args, "--output") ?? "investigations";
var defaultRegion = ArgValue(args, "--region") ?? "ap-southeast-2";
var retrieverMode = (ArgValue(args, "--retriever") ?? "hybrid").ToLowerInvariant();
var useMock = args.Contains("--mock");

if (descFile is not null)
{
    if (!File.Exists(descFile))
    {
        Console.Error.WriteLine($"Description file not found: {descFile}");
        return 1;
    }
    description = await File.ReadAllTextAsync(descFile);
}

if (description is null && ticketId is null)
{
    Console.Error.WriteLine("Provide either --desc \"bug description\", --desc-file <path>, or --ticket <id>.");
    PrintUsage();
    return 1;
}

if (description is null)
{
    Console.WriteLine($"No description provided for ticket {ticketId}. Paste ticket summary below, end with a blank line:");
    var lines = new List<string>();
    string? line;
    while ((line = Console.ReadLine()) is not null && line.Length > 0)
    {
        lines.Add(line);
    }
    description = string.Join("\n", lines);
    if (string.IsNullOrWhiteSpace(description))
    {
        Console.Error.WriteLine("No description entered — aborting.");
        return 1;
    }
}

var openaiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
if (string.IsNullOrWhiteSpace(openaiKey))
{
    Console.Error.WriteLine("OPENAI_API_KEY is not set.");
    return 1;
}

// Build the log-search tool: mock fixtures, or real CloudWatch with a retriever.
var hypothesisChannel = Channel.CreateUnbounded<(string, string)>();
IDebugTool logTool = useMock
    ? new MockLogSearchTool()
    : BuildCloudWatchTool(openaiKey, retrieverMode, defaultRegion);

// Phase 3 tools: human-in-the-loop data lookups. The agent formulates a query,
// the CLI prints it, you paste back the result (or 'empty'/'skip').
var humanProvider = new ConsoleHumanDataProvider();

var registry = new ToolRegistry(new[]
{
    logTool,
    new RequestMongoQueryTool(humanProvider),
    new RequestOpenSearchQueryTool(humanProvider),
    new RequestKafkaEventsTool(humanProvider),
    new RecordHypothesisTool(hypothesisChannel),
    new FinishInvestigationTool(),
});

var agent = new InvestigatorAgent(registry, openaiKey);

var report = new BugReport(
    Description: description!,
    TicketId: ticketId,
    TicketSource: ticketId is null ? null : "Jira",
    ReportedAt: DateTimeOffset.UtcNow);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var mode = useMock ? "mock" : $"real CloudWatch · {retrieverMode} retriever";
Console.WriteLine($"=== DistributedDebugger — Phase 4 ({mode}) ===");
Console.WriteLine();
Console.WriteLine("Investigating... (streaming steps below)");
Console.WriteLine();

try
{
    var investigation = await agent.InvestigateAsync(
        report,
        config: new AgentConfig(MaxIterations: 12),
        hypothesisChannel: hypothesisChannel,
        onEvent: PrintEvent,
        ct: cts.Token);

    Console.WriteLine();
    Console.WriteLine("=== Report ===");
    Console.WriteLine();

    var markdown = ReportWriter.Render(investigation);
    Console.WriteLine(markdown);

    Directory.CreateDirectory(outputDir);
    var slug = (investigation.Report.TicketId ?? investigation.Id.ToString("N")[..8])
        .Replace("/", "_").Replace("\\", "_");
    var mdPath = Path.Combine(outputDir,
        $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{slug}.md");
    await File.WriteAllTextAsync(mdPath, markdown);

    Console.WriteLine($"Report written to: {Path.GetFullPath(mdPath)}");
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}
finally
{
    if (logTool is IDisposable d) d.Dispose();
}

static IDebugTool BuildCloudWatchTool(string openaiKey, string mode, string defaultRegion)
{
    ILogRetriever retriever = mode switch
    {
        "keyword"  => new KeywordLogRetriever(),
        "semantic" => new SemanticLogRetriever(openaiKey),
        "hybrid"   => new HybridLogRetriever(
                          new KeywordLogRetriever(),
                          new SemanticLogRetriever(openaiKey)),
        _ => throw new ArgumentException(
                 $"Unknown --retriever '{mode}'. Options: keyword, semantic, hybrid.")
    };

    return new CloudWatchLogSearchTool(retriever, defaultRegion: defaultRegion);
}

static string? ArgValue(string[] args, string flag)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == flag) return args[i + 1];
    }
    return null;
}

static void PrintEvent(InvestigationEvent ev)
{
    switch (ev)
    {
        case ModelCallEvent mc:
            Console.WriteLine($"  [iter {mc.Iteration}] → model (messages: {mc.PromptMessageCount})");
            break;
        case ToolCallEvent tc:
            // For request_* tools the tool itself prints a full prompt box,
            // so we only show a one-liner here to avoid duplication.
            if (tc.ToolName.StartsWith("request_"))
            {
                Console.WriteLine($"  [iter {tc.Iteration}] 🔧 {tc.ToolName} (awaiting your input below)");
            }
            else
            {
                Console.WriteLine($"  [iter {tc.Iteration}] 🔧 {tc.ToolName}({Compact(tc.Input.ToString())})");
            }
            break;
        case ToolResultEvent tr:
            var preview = tr.Output.Length > 120 ? tr.Output[..120] + "..." : tr.Output;
            var marker = tr.IsError ? "✗" : "✓";
            Console.WriteLine($"  [iter {tr.Iteration}] {marker} {preview.Replace("\n", " ")}");
            break;
        case HypothesisEvent h:
            Console.WriteLine($"  [iter {h.Iteration}] 💡 Hypothesis: {h.Hypothesis}");
            break;
        case ErrorEvent e:
            Console.WriteLine($"  [iter {e.Iteration}] ⚠ Error: {e.Message}");
            break;
    }
}

static string Compact(string s)
{
    var x = s.Replace('\n', ' ').Replace('\r', ' ');
    while (x.Contains("  ")) x = x.Replace("  ", " ");
    return x.Length > 100 ? x[..100] + "..." : x;
}

static void PrintUsage()
{
    Console.WriteLine("DistributedDebugger — AI-powered bug investigator for distributed systems");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  investigate   Run a single investigation against real systems (or mocks).");
    Console.WriteLine("  eval          Replay recorded cases through the grader for regression testing.");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  debugger investigate --desc \"<bug description>\" [options]");
    Console.WriteLine("  debugger investigate --desc-file <path>           [options]");
    Console.WriteLine("  debugger investigate --ticket <id> [--desc \"...\"] [options]");
    Console.WriteLine("  debugger eval [--cases <dir>] [--config <id> ...] [--judge-model gpt-4o]");
    Console.WriteLine();
    Console.WriteLine("investigate options:");
    Console.WriteLine("  --desc <text>       Bug description (quoted).");
    Console.WriteLine("  --desc-file <path>  Read description from a file.");
    Console.WriteLine("  --ticket <id>       Jira/ticket id for reporting.");
    Console.WriteLine("  --output <folder>   Where to write markdown reports (default: investigations).");
    Console.WriteLine("  --mock              Use fixture logs instead of real CloudWatch.");
    Console.WriteLine("  --retriever <kind>  keyword | semantic | hybrid (default: hybrid).");
    Console.WriteLine("  --region <region>   Default AWS region (default: ap-southeast-2).");
    Console.WriteLine();
    Console.WriteLine("eval options:");
    Console.WriteLine("  --cases <dir>       Folder of .yaml eval cases (default: eval-cases).");
    Console.WriteLine("  --output <dir>      Where to write CSV results (default: eval-results).");
    Console.WriteLine("  --config <id>       Named config to run (repeat for multiple). Default: baseline.");
    Console.WriteLine("  --judge-model <m>   Model used for grading (default: gpt-4o).");
    Console.WriteLine();
    Console.WriteLine("Tools the agent will use during investigation:");
    Console.WriteLine("  search_logs              — CloudWatch Logs (auto)");
    Console.WriteLine("  request_mongo_query      — asks you to run a MongoDB find");
    Console.WriteLine("  request_opensearch_query — asks you to run an OpenSearch query");
    Console.WriteLine("  request_kafka_events     — asks you to check Kafka via your UI");
    Console.WriteLine("  record_hypothesis        — agent's working theory (auto)");
    Console.WriteLine("  finish_investigation     — final root-cause report (auto)");
    Console.WriteLine();
    Console.WriteLine("Env vars:");
    Console.WriteLine("  OPENAI_API_KEY  (required)");
    Console.WriteLine("  AWS credentials from default profile (~/.aws/config). Run `aws sso login` first.");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  # Free, fixture-based run for iterating on prompts");
    Console.WriteLine("  debugger investigate --mock --desc \"act-789 not indexed after publish\"");
    Console.WriteLine();
    Console.WriteLine("  # Real investigation with CloudWatch + human-loop queries");
    Console.WriteLine("  debugger investigate --ticket COCO-1234 \\");
    Console.WriteLine("    --desc \"Activity act-789 published at 14:27 UTC but not in search\"");
    Console.WriteLine();
    Console.WriteLine("  # Regression suite — compare two configs across all recorded cases");
    Console.WriteLine("  debugger eval --config baseline --config big-model");
}
