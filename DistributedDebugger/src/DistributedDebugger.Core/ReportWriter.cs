using System.Text;
using DistributedDebugger.Core.Models;

namespace DistributedDebugger.Core.Reporting;

/// <summary>
/// Renders a completed investigation as a markdown report. Designed to be
/// read either in the terminal or pasted into a Jira comment.
/// </summary>
public static class ReportWriter
{
    public static string Render(Investigation investigation)
    {
        var sb = new StringBuilder();
        var r = investigation;
        var rc = r.RootCause;

        sb.AppendLine($"# Bug Investigation {(r.Report.TicketId is null ? "" : $"— {r.Report.TicketId}")}");
        sb.AppendLine();
        sb.AppendLine($"- Status: **{r.Status}**");
        sb.AppendLine($"- Duration: {r.Usage.WallTime.TotalSeconds:0.0}s, {r.Usage.Iterations} iteration(s)");
        sb.AppendLine($"- Tokens: {r.Usage.InputTokens} in / {r.Usage.OutputTokens} out");
        sb.AppendLine();

        sb.AppendLine("## Bug description");
        sb.AppendLine();
        sb.AppendLine("> " + r.Report.Description.Replace("\n", "\n> "));
        sb.AppendLine();

        if (rc is not null)
        {
            sb.AppendLine("## Root cause");
            sb.AppendLine();
            sb.AppendLine($"**Summary:** {rc.Summary}");
            sb.AppendLine();
            sb.AppendLine($"**Likely cause:** {rc.LikelyCause}");
            sb.AppendLine();
            sb.AppendLine($"**Confidence:** {rc.Confidence}");
            sb.AppendLine();

            if (rc.AffectedServices.Count > 0)
            {
                sb.AppendLine("**Affected services:**");
                foreach (var s in rc.AffectedServices) sb.AppendLine($"- {s}");
                sb.AppendLine();
            }

            if (rc.Evidence.Count > 0)
            {
                sb.AppendLine("**Evidence:**");
                foreach (var e in rc.Evidence) sb.AppendLine($"- {e}");
                sb.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(rc.SuggestedFix))
            {
                sb.AppendLine("**Suggested fix:**");
                sb.AppendLine();
                sb.AppendLine(rc.SuggestedFix);
                sb.AppendLine();
            }
        }

        // Hypotheses recorded along the way — useful for seeing the agent's
        // evolving theories even when confidence ended up Low.
        var hypotheses = r.Trace.OfType<HypothesisEvent>().ToList();
        if (hypotheses.Count > 0)
        {
            sb.AppendLine("## Hypotheses explored");
            sb.AppendLine();
            foreach (var h in hypotheses)
            {
                sb.AppendLine($"- **{h.Hypothesis}** — {h.Reasoning}");
            }
            sb.AppendLine();
        }

        // Tool calls as a compact audit log.
        var toolCalls = r.Trace.OfType<ToolCallEvent>().ToList();
        if (toolCalls.Count > 0)
        {
            sb.AppendLine("## Tools used");
            sb.AppendLine();
            foreach (var t in toolCalls)
            {
                sb.AppendLine($"- `{t.ToolName}` — {Compact(t.Input.ToString())}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string Compact(string json)
    {
        // Strip newlines and collapse spaces so it's a single tidy line.
        var s = json.Replace('\n', ' ').Replace('\r', ' ');
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        return s.Trim();
    }
}
