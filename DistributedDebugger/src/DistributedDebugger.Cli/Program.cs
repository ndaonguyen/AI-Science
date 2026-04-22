using System.Threading.Channels;
using DistributedDebugger.Agent;
using DistributedDebugger.Cli;
using DistributedDebugger.Core.Models;
using DistributedDebugger.Core.Tools;
using DistributedDebugger.Tools;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    PrintUsage();
    return 0;
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
    // Ticket-only mode: user hasn't pre-fetched the ticket, so we ask them for
    // the key details interactively. Keeps us honest about what we know.
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

// Phase 1: only mock tools. Real Datadog/CloudWatch wiring comes in Phase 2.
var hypothesisChannel = Channel.CreateUnbounded<(string, string)>();
var registry = new ToolRegistry(new IDebugTool[]
{
    new MockLogSearchTool(),
    new MockKafkaEventTool(),
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

Console.WriteLine("=== DistributedDebugger — Phase 1 (mock tools) ===");
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

    // Persist the markdown and a JSON trace next to it for later inspection.
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
            Console.WriteLine($"  [iter {tc.Iteration}] 🔧 {tc.ToolName}({Compact(tc.Input.ToString())})");
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
    Console.WriteLine("Usage:");
    Console.WriteLine("  debugger investigate --desc \"<bug description>\"");
    Console.WriteLine("  debugger investigate --desc-file <path>");
    Console.WriteLine("  debugger investigate --ticket <id> [--desc \"...\"]");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --desc <text>       Bug description (quoted).");
    Console.WriteLine("  --desc-file <path>  Read description from a file.");
    Console.WriteLine("  --ticket <id>       Jira/ticket id for reporting. Paired with --desc.");
    Console.WriteLine("  --output <folder>   Where to write markdown reports (default: investigations).");
    Console.WriteLine();
    Console.WriteLine("Env vars:");
    Console.WriteLine("  OPENAI_API_KEY  (required)");
    Console.WriteLine();
    Console.WriteLine("Example:");
    Console.WriteLine("  debugger investigate --ticket COCO-1234 \\");
    Console.WriteLine("    --desc \"Activity act-789 published at 14:27 but not appearing in search.\"");
}
