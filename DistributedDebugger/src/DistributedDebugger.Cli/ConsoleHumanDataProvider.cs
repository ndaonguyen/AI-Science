using DistributedDebugger.Core.Tools;

namespace DistributedDebugger.Cli;

/// <summary>
/// Concrete IHumanDataProvider for the CLI. Renders the agent's query request
/// as a clearly-boxed prompt and collects a multi-line response from stdin.
///
/// Input conventions:
///   - "skip" or "decline" on a line by itself → return null (tool reports declined)
///   - "empty" on a line by itself            → return "" (tool reports no data)
///   - "END" on a line by itself              → finish the current paste
///   - Double blank line                      → also finishes the current paste
///     (helpful when pasting JSON that contains single blank lines)
/// </summary>
public sealed class ConsoleHumanDataProvider : IHumanDataProvider
{
    public Task<string?> RequestDataAsync(HumanDataRequest request, CancellationToken ct)
    {
        Console.WriteLine();
        Console.WriteLine("┌─ Manual data request ──────────────────────────────────");
        Console.WriteLine($"│ Source: {request.SourceName}" +
                          (request.SuggestedEnv is null ? "" : $"  (suggested env: {request.SuggestedEnv})"));
        Console.WriteLine($"│ Reason: {request.Reason}");
        Console.WriteLine("├─ Query ────────────────────────────────────────────────");
        foreach (var line in request.RenderedQuery.Split('\n'))
        {
            Console.WriteLine($"│ {line}");
        }
        Console.WriteLine("└────────────────────────────────────────────────────────");
        Console.WriteLine();
        Console.WriteLine("Paste the result below. Type 'END' on its own line when done,");
        Console.WriteLine("or 'empty' if no match, or 'skip' to decline. Ctrl+C to abort.");
        Console.Write("> ");

        var lines = new List<string>();
        int consecutiveBlanks = 0;

        while (!ct.IsCancellationRequested)
        {
            var line = Console.ReadLine();
            if (line is null) break; // stdin closed

            var trimmed = line.Trim();

            // Single-word terminators only valid on an otherwise-empty response
            // or as the first thing typed, so we don't eat them mid-JSON.
            if (lines.Count == 0)
            {
                if (string.Equals(trimmed, "skip", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(trimmed, "decline", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult<string?>(null);
                }
                if (string.Equals(trimmed, "empty", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult<string?>("");
                }
            }

            if (string.Equals(trimmed, "END", StringComparison.Ordinal))
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                consecutiveBlanks++;
                if (consecutiveBlanks >= 2) break;
            }
            else
            {
                consecutiveBlanks = 0;
            }

            lines.Add(line);
        }

        // Trim trailing blank lines (from the double-blank terminator).
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var joined = string.Join("\n", lines).Trim();
        return Task.FromResult<string?>(joined.Length == 0 ? "" : joined);
    }
}
