using System.Text;
using System.Text.Json;
using OpenAI.Chat;

namespace DistributedDebugger.Web.V3;

/// <summary>
/// Optional follow-up to <see cref="LogAnalyzer"/>. Takes the analysis output
/// (summary, suspicious lines, hypothesis, followups) plus the original
/// description, evidence, and bundled schemas, and asks the LLM to produce
/// a list of CONCRETE QUERIES the user could run next to confirm or rule
/// out the hypothesis.
///
/// The intent is to close the loop: the analyzer says 'block 67xyz looks
/// wrong' and stops. Without help the user has to write a Mongo or
/// OpenSearch query themselves — figuring out the right collection, the
/// right id format (ObjectId vs string), the right field. The LLM already
/// has all that context (via schemas) so let it draft the queries instead.
///
/// Why a separate class and not part of <see cref="LogAnalyzer"/>:
///   - On-demand (user opts in by clicking 'Suggest queries'). Avoids
///     paying for it on every analyze.
///   - Doesn't re-process the raw logs (~8K tokens on a typical analyze).
///     The analysis output already distills what's relevant. Cost per
///     suggest call is ~1-2K input, ~300-500 output.
///   - Keeps the analyzer's response shape stable. Adding a new
///     SuggestedQueries field there would force every caller (eval harness
///     included) to consider it.
/// </summary>
public sealed class QuerySuggester
{
    private readonly string _apiKey;
    private readonly string _model;

    public QuerySuggester(string apiKey, string model = "gpt-4o-mini")
    {
        _apiKey = apiKey;
        _model = model;
    }

    public async Task<QuerySuggestionResult> SuggestAsync(
        string bugDescription,
        string? ticketId,
        AnalysisInput analysis,
        IReadOnlyList<V3Endpoints.EvidenceItem> evidence,
        IReadOnlyList<SchemaDoc> schemas,
        CancellationToken ct)
    {
        // Defensive: if there's no hypothesis to follow up on, return empty
        // rather than spending a call on 'I don't know what to suggest'.
        if (string.IsNullOrWhiteSpace(analysis.Hypothesis))
        {
            return new QuerySuggestionResult(
                Suggestions: Array.Empty<QuerySuggestion>(),
                InputTokens: 0,
                OutputTokens: 0);
        }

        var systemPrompt =
            "You are a senior backend engineer at Education Perfect helping a " +
            "colleague debug a distributed system bug (CoCo: MongoDB, " +
            "OpenSearch, Kafka). They've just completed an LLM-assisted log " +
            "analysis and now need to confirm or rule out the hypothesis by " +
            "running concrete queries against the underlying systems. " +
            "Your job: produce a SHORT list (2-5) of executable queries they " +
            "could run NEXT, each one targeted at confirming a specific claim " +
            "in the analysis or filling a gap. " +
            "Use the bundled SCHEMAS as ground truth for collection names, " +
            "field names, types, and id shapes (ObjectId vs string vs Guid). " +
            "If the SCHEMAS document includes a 'Querying' section with worked " +
            "examples, follow those patterns precisely — they encode subtle " +
            "MongoDB conventions (e.g. discriminator field shape, nested array " +
            "matching with $elemMatch) that are easy to get wrong. " +
            "CRITICAL — common mistakes to avoid: " +
            "  1. Querying a NESTED field at the WRONG level. If a field lives " +
            "     inside an array (e.g. `block.components[].assetId`), do NOT " +
            "     write `db.blocks.find({ assetId: ... })` — that matches a " +
            "     top-level field that doesn't exist. Use `$elemMatch` to " +
            "     match an array element: " +
            "     `db.blocks.find({ components: { $elemMatch: { _t: ..., " +
            "     assetId: ... } } })`. " +
            "  2. Treating a discriminator field as a single value when it's " +
            "     an array. MongoDB.Bson serialises root-class discriminators " +
            "     as the full hierarchy array (e.g. `_t: [\"BlockModel\", " +
            "     \"ComponentBlockModel\"]`). Equality matching against a " +
            "     string still works (Mongo matches against any element), " +
            "     but be aware. " +
            "  3. Inventing field names not in the schema. If you can't write " +
            "     the query without guessing, say so in the rationale and " +
            "     use 'note' as the system instead of fabricating Mongo. " +
            "If the analysis names a specific id, use it verbatim in the " +
            "query — do not invent placeholders. If the analysis is vague " +
            "and you genuinely don't have enough to write a query, return " +
            "fewer suggestions rather than padding with guesses. " +
            "Each suggestion must include: " +
            "  - 'system': one of 'mongo', 'opensearch', 'kafka', 'note' " +
            "  - 'query': the executable command or query, ready to paste " +
            "    into the appropriate tool. For mongo: db.collection.findX() " +
            "    syntax. For opensearch: a JSON query body or the equivalent " +
            "    GET URL. For kafka: a kafkactl/kcat one-liner or topic + " +
            "    partition + offset reference. For note: a free-form " +
            "    instruction (e.g. 'check the activity-published topic for " +
            "    a message with key act-789 around 14:27 UTC') " +
            "  - 'rationale': one sentence explaining what running this would " +
            "    confirm or rule out. " +
            "Output STRICT JSON with shape: " +
            "{ \"suggestions\": [ { \"system\": ..., \"query\": ..., " +
            "\"rationale\": ... }, ... ] }. " +
            "No prose outside the JSON.";

        var userPrompt = BuildUserPrompt(bugDescription, ticketId, analysis, evidence, schemas);

        var client = new ChatClient(model: _model, apiKey: _apiKey);
        var options = new ChatCompletionOptions
        {
            Temperature = 0f,
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

        var suggestions = ParseSuggestions(text);
        var inputTokens = response.Value.Usage?.InputTokenCount ?? 0;
        var outputTokens = response.Value.Usage?.OutputTokenCount ?? 0;

        return new QuerySuggestionResult(suggestions, inputTokens, outputTokens);
    }

    // ---- prompt construction ----

    private static string BuildUserPrompt(
        string bug,
        string? ticket,
        AnalysisInput analysis,
        IReadOnlyList<V3Endpoints.EvidenceItem> evidence,
        IReadOnlyList<SchemaDoc> schemas)
    {
        var sb = new StringBuilder();

        // Schemas FIRST — same as the analyzer. The model needs to know
        // collection shapes to write valid queries (e.g. activities uses
        // ObjectId, but assetId is a Guid, etc.).
        if (schemas is { Count: > 0 })
        {
            sb.AppendLine("## Reference: schemas");
            sb.AppendLine();
            sb.AppendLine(
                "These describe the MongoDB collection shapes for the relevant " +
                "CoCo services. Use them to write valid queries: correct " +
                "collection names, correct field names, correct id types " +
                "(ObjectId vs Guid vs string).");
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
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(ticket))
            sb.Append("**Ticket:** ").AppendLine(ticket);
        sb.Append("**Description:** ").AppendLine(bug);
        sb.AppendLine();

        // The analysis output IS the input here. Feed it back so the model
        // can write queries that target the specific entities and concerns
        // the analysis raised.
        sb.AppendLine("## Analysis output (what the previous step concluded)");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(analysis.Summary))
        {
            sb.AppendLine("**Summary:**");
            sb.AppendLine(analysis.Summary);
            sb.AppendLine();
        }
        if (analysis.Suspicious.Count > 0)
        {
            sb.AppendLine("**Suspicious log lines flagged:**");
            foreach (var line in analysis.Suspicious)
                sb.Append("- ").AppendLine(line);
            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(analysis.Hypothesis))
        {
            sb.AppendLine("**Hypothesis:**");
            sb.AppendLine(analysis.Hypothesis);
            sb.AppendLine();
        }
        if (analysis.SuggestedFollowups.Count > 0)
        {
            sb.AppendLine("**Suggested followups (prose):**");
            foreach (var f in analysis.SuggestedFollowups)
                sb.Append("- ").AppendLine(f);
            sb.AppendLine();
        }

        // Evidence already-pasted gives the model context about which
        // documents the user has eyes on. A good suggestion shouldn't
        // re-suggest a query that just produces something already pasted.
        if (evidence is { Count: > 0 })
        {
            sb.AppendLine("## Evidence already gathered");
            sb.AppendLine();
            foreach (var e in evidence)
            {
                var kind = string.IsNullOrWhiteSpace(e.Kind) ? "note" : e.Kind;
                var title = string.IsNullOrWhiteSpace(e.Title) ? "(no title)" : e.Title;
                sb.Append("- **").Append(kind).Append("** ").AppendLine(title);
                if (!string.IsNullOrWhiteSpace(e.Command))
                {
                    sb.Append("    command: `").Append(e.Command.ReplaceLineEndings(" ").Trim()).AppendLine("`");
                }
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Task");
        sb.AppendLine();
        sb.AppendLine(
            "Produce 2-5 concrete next queries that would CONFIRM or RULE OUT " +
            "the hypothesis above. Each must be runnable as-is. Use real ids " +
            "from the analysis output where present. Don't suggest a query " +
            "that just reproduces evidence already gathered. Output only the " +
            "JSON object specified in the system prompt.");

        return sb.ToString();
    }

    // ---- parsing ----

    private static IReadOnlyList<QuerySuggestion> ParseSuggestions(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("suggestions", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<QuerySuggestion>();
            }

            var list = new List<QuerySuggestion>(arr.GetArrayLength());
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;

                // Defensive: any of these may be missing / null / wrong type.
                // Don't drop the whole list because of one malformed entry —
                // skip the bad one and keep the rest.
                var system = el.TryGetProperty("system", out var sysEl) &&
                             sysEl.ValueKind == JsonValueKind.String
                    ? (sysEl.GetString() ?? "note")
                    : "note";
                var query = el.TryGetProperty("query", out var qEl) &&
                            qEl.ValueKind == JsonValueKind.String
                    ? (qEl.GetString() ?? "")
                    : "";
                var rationale = el.TryGetProperty("rationale", out var rEl) &&
                                rEl.ValueKind == JsonValueKind.String
                    ? (rEl.GetString() ?? "")
                    : "";

                if (string.IsNullOrWhiteSpace(query)) continue;

                list.Add(new QuerySuggestion(
                    System: NormaliseSystem(system),
                    Query: query.Trim(),
                    Rationale: rationale.Trim()));
            }
            return list;
        }
        catch (JsonException)
        {
            // Malformed JSON despite response_format=json_object. Treat as
            // empty rather than crash — caller will show 'no suggestions'.
            return Array.Empty<QuerySuggestion>();
        }
    }

    /// <summary>
    /// Restrict 'system' values to the four we render UI for. Anything else
    /// (the model getting creative) collapses to 'note' which renders as
    /// generic text. Keeps the UI from having to handle arbitrary strings.
    /// </summary>
    private static string NormaliseSystem(string raw) =>
        raw.Trim().ToLowerInvariant() switch
        {
            "mongo" or "mongodb"        => "mongo",
            "opensearch" or "elastic"   => "opensearch",
            "kafka"                     => "kafka",
            _                           => "note",
        };
}

/// <summary>
/// Subset of <see cref="AnalysisResult"/> the suggester actually needs.
/// Decoupled so the suggester doesn't depend on AnalysisResult's full
/// shape — keeps the new endpoint flexible if the analyzer's response
/// changes later.
/// </summary>
public sealed record AnalysisInput(
    string? Summary,
    IReadOnlyList<string> Suspicious,
    string Hypothesis,
    IReadOnlyList<string> SuggestedFollowups);

public sealed record QuerySuggestion(
    string System,      // "mongo" | "opensearch" | "kafka" | "note"
    string Query,
    string Rationale);

public sealed record QuerySuggestionResult(
    IReadOnlyList<QuerySuggestion> Suggestions,
    int InputTokens,
    int OutputTokens);
