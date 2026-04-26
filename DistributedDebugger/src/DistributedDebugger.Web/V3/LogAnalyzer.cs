using OpenAI.Chat;

namespace DistributedDebugger.Web.V3;

/// <summary>
/// The only place an LLM is involved in the V2 flow.
///
/// Takes a curated set of log records the user has already gathered (via
/// Filter and Extend actions) plus the bug context they typed in the form,
/// and produces a structured analysis. No tool calls, no agent loop, no
/// chat history. Single completion, JSON output, deterministic prompt.
///
/// Because the input is bounded (the user picked the logs), token cost is
/// predictable and there's no opportunity for the model to fetch the wrong
/// data, paraphrase a filter, or rewrite a timestamp. The model's job is
/// the one thing LLMs are reliably good at: pattern recognition over text.
/// </summary>
public sealed class LogAnalyzer
{
    private readonly string _apiKey;
    private readonly string _model;

    public LogAnalyzer(string apiKey, string model = "gpt-4o-mini")
    {
        _apiKey = apiKey;
        _model = model;
    }

    public async Task<AnalysisResult> AnalyzeAsync(
        string bugDescription,
        string? ticketId,
        IReadOnlyList<LogRecord> logs,
        IReadOnlyList<V3Endpoints.EvidenceItem> evidence,
        IReadOnlyList<SchemaDoc> schemas,
        CancellationToken ct)
    {
        if (logs.Count == 0)
        {
            return new AnalysisResult(
                Summary: "No logs were provided to analyze.",
                Suspicious: Array.Empty<string>(),
                Hypothesis: "(empty input)",
                SuggestedFollowups: Array.Empty<string>(),
                SchemasIncluded: Array.Empty<string>(),
                InputTokens: 0,
                OutputTokens: 0);
        }

        // System prompt acknowledges schemas (when present) and evidence so
        // the model knows it has BOTH 'reference shape' and 'concrete logs +
        // documents' to reason over. When schemas are bundled the model can
        // spot field-level mismatches (e.g. the well-known
        // ImageComponentModel.assetId-as-non-nullable-Guid bug) without the
        // user having to re-explain the data model in every bug report.
        var systemPrompt =
            "You are a senior backend engineer analysing CloudWatch logs from a " +
            "distributed microservice system (CoCo at Education Perfect: MongoDB, " +
            "OpenSearch, Kafka, ECS). The user has already filtered the logs down " +
            "to a specific set they want analysed, may have pasted supporting " +
            "evidence from MongoDB, OpenSearch, Kafka, or other sources, and the " +
            "prompt may also include reference SCHEMAS for the relevant services. " +
            "Use the schemas as ground truth for field names, types, and " +
            "nullability — if evidence shows a value that violates the schema " +
            "(e.g. null where the model declares a non-nullable Guid), call it " +
            "out as a likely cause. Read every log line. Look for exceptions, " +
            "stack traces, error/warn lines, timeouts, connection failures, null " +
            "references, retries, and any behaviour inconsistent with the bug " +
            "description. When evidence is provided, cross-reference it against " +
            "the logs (e.g. 'the log claims X but the Mongo document shows Y'). " +
            "Quote actual log lines and evidence snippets as support — never " +
            "invent details that aren't in the input. If the cause still isn't " +
            "clear, say so honestly and suggest what to investigate next " +
            "(Mongo? OpenSearch? Kafka? other services?).";

        var userPrompt = BuildUserPrompt(bugDescription, ticketId, logs, evidence, schemas);

        var client = new ChatClient(model: _model, apiKey: _apiKey);
        var options = new ChatCompletionOptions
        {
            Temperature = 0f,
            // The schema is tight enough that JSON-mode catches malformed
            // outputs cheaply. We still defensively parse on the C# side.
            ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat(),
        };

        var response = await client.CompleteChatAsync(
            new List<ChatMessage>
            {
                new SystemChatMessage(systemPrompt),
                new UserChatMessage(userPrompt),
            },
            options,
            ct);

        var text = response.Value.Content.Count > 0
            ? response.Value.Content[0].Text ?? "{}"
            : "{}";
        var parsed = ParseShape(text);

        return new AnalysisResult(
            Summary: parsed.Summary,
            Suspicious: parsed.Suspicious,
            Hypothesis: parsed.Hypothesis,
            SuggestedFollowups: parsed.SuggestedFollowups,
            SchemasIncluded: schemas.Select(s => s.Name).ToArray(),
            InputTokens: response.Value.Usage?.InputTokenCount ?? 0,
            OutputTokens: response.Value.Usage?.OutputTokenCount ?? 0);
    }

    private static string BuildUserPrompt(
        string bug,
        string? ticket,
        IReadOnlyList<LogRecord> logs,
        IReadOnlyList<V3Endpoints.EvidenceItem> evidence,
        IReadOnlyList<SchemaDoc> schemas)
    {
        var sb = new System.Text.StringBuilder();

        // Schemas FIRST so the model treats them as reference material it
        // applies to everything that follows. Each schema is fenced as
        // markdown — already-markdown content stays markdown; the LLM
        // handles nested fences fine. Skip the section entirely when no
        // schemas are loaded so we don't waste tokens on a placeholder.
        if (schemas is { Count: > 0 })
        {
            sb.AppendLine("## Reference: schemas");
            sb.AppendLine();
            sb.AppendLine(
                "These describe the MongoDB collection shapes for the relevant CoCo " +
                "services. Use them as ground truth when interpreting evidence: " +
                "field names, types, nullability, and the discriminator (`_t`) on " +
                "polymorphic collections. If a pasted document violates the schema " +
                "(e.g. `null` where the model declares a non-nullable Guid), say so.");
            sb.AppendLine();
            foreach (var s in schemas)
            {
                sb.Append("### ").AppendLine(s.Name);
                sb.AppendLine();
                sb.AppendLine(s.Content);
                sb.AppendLine();
            }
        }

        sb.AppendLine("## Bug context");
        if (!string.IsNullOrWhiteSpace(ticket))
            sb.AppendLine($"Ticket: {ticket}");
        sb.AppendLine(bug);
        sb.AppendLine();
        sb.AppendLine($"## Logs to analyse ({logs.Count} entries)");
        sb.AppendLine();
        foreach (var l in logs)
        {
            sb.Append('[').Append(l.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("Z] ");
            sb.Append('[').Append(l.Service).Append("] ");
            sb.AppendLine(TruncateForPrompt(l.Message));
        }
        sb.AppendLine();

        // Evidence section — only emitted if the user actually pasted
        // something. Each item is labelled with its kind (Mongo / OpenSearch /
        // Kafka / Note) and the title the user typed, followed by the raw
        // payload they pasted. Big payloads get the same per-item truncation
        // as logs so one fat document can't blow the context budget.
        if (evidence is { Count: > 0 })
        {
            sb.AppendLine($"## Supporting evidence ({evidence.Count} item{(evidence.Count == 1 ? "" : "s")})");
            sb.AppendLine();
            foreach (var item in evidence)
            {
                var label = (item.Kind ?? "note").ToLowerInvariant() switch
                {
                    "mongo"      => "Mongo document",
                    "opensearch" => "OpenSearch result",
                    "kafka"      => "Kafka event",
                    _            => "Note",
                };
                sb.Append("### ").Append(label);
                if (!string.IsNullOrWhiteSpace(item.Title)) sb.Append(" — ").Append(item.Title);
                sb.AppendLine();

                // Command goes BEFORE the content so the LLM reads 'here's
                // what was asked' first, then 'here's what came back' — the
                // natural reading order. Skipped for empty commands and for
                // 'note' evidence (which has no query). Bare-fenced so the
                // model can read it as a literal command without parsing
                // markdown emphasis or lists.
                if (!string.IsNullOrWhiteSpace(item.Command) &&
                    !string.Equals(item.Kind, "note", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine("Command:");
                    sb.AppendLine("```");
                    sb.AppendLine(TruncateForPrompt(item.Command!));
                    sb.AppendLine("```");
                    sb.AppendLine("Result:");
                }

                sb.AppendLine("```");
                sb.AppendLine(TruncateForPrompt(item.Content ?? ""));
                sb.AppendLine("```");
                sb.AppendLine();
            }
        }

        sb.AppendLine("## Output schema (return JSON object only)");
        sb.AppendLine("{");
        sb.AppendLine("  \"summary\": string,                    // 1-2 sentence answer to 'what's going on'");
        sb.AppendLine("  \"suspicious\": [string, ...],          // exact log lines or evidence snippets that stand out (quote them)");
        sb.AppendLine("  \"hypothesis\": string,                 // best theory — say 'unclear' if it's unclear");
        sb.AppendLine("  \"suggestedFollowups\": [string, ...]   // 0-3 next things to check (Mongo doc? OpenSearch index? other service's logs?)");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>
    /// Per-log truncation to keep one bad multi-MB log line from blowing
    /// the prompt budget. 4000 chars per line is generous — even fat stack
    /// traces fit, but a JSON dump of a 50KB content document gets cut off
    /// gracefully with a marker the LLM can still read context around.
    /// </summary>
    private static string TruncateForPrompt(string message)
    {
        const int maxChars = 4000;
        if (message.Length <= maxChars) return message;
        return message[..maxChars] + " …[truncated]";
    }

    private static ParsedShape ParseShape(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new ParsedShape(
                Summary: root.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "",
                Suspicious: ReadStringArray(root, "suspicious"),
                Hypothesis: root.TryGetProperty("hypothesis", out var h) ? h.GetString() ?? "" : "",
                SuggestedFollowups: ReadStringArray(root, "suggestedFollowups"));
        }
        catch
        {
            // If the model returned non-JSON, surface it as a summary so the
            // user still sees something rather than a silent empty card.
            return new ParsedShape(
                Summary: json.Length > 500 ? json[..500] : json,
                Suspicious: Array.Empty<string>(),
                Hypothesis: "(model returned non-JSON output)",
                SuggestedFollowups: Array.Empty<string>());
        }
    }

    private static IReadOnlyList<string> ReadStringArray(System.Text.Json.JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var arr) ||
            arr.ValueKind != System.Text.Json.JsonValueKind.Array)
            return Array.Empty<string>();
        var list = new List<string>(arr.GetArrayLength());
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var s = el.GetString();
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
            }
        }
        return list;
    }

    private sealed record ParsedShape(
        string Summary,
        IReadOnlyList<string> Suspicious,
        string Hypothesis,
        IReadOnlyList<string> SuggestedFollowups);
}

public sealed record AnalysisResult(
    string Summary,
    IReadOnlyList<string> Suspicious,
    string Hypothesis,
    IReadOnlyList<string> SuggestedFollowups,
    // Names of schema docs that were prepended to the prompt — surfaces in
    // the API response so the UI can show 'schemas: authoring-service,
    // content-search-service' next to the token cost. Useful for confirming
    // the wiring works without reading the prompt itself.
    IReadOnlyList<string> SchemasIncluded,
    int InputTokens,
    int OutputTokens);
