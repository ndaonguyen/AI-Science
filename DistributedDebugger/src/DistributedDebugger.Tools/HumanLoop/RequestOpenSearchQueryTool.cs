using System.Text.Json;
using DistributedDebugger.Core.Tools;

namespace DistributedDebugger.Tools.HumanLoop;

/// <summary>
/// Agent-facing tool for OpenSearch state inspection. The agent formulates a
/// Query DSL body and index pattern; the human runs it in Kibana/OpenSearch
/// Dashboards (or curl) and pastes back the response hits.
///
/// Unlike the Mongo tool, OpenSearch has many legitimate query shapes — term,
/// match, bool, exists, range — so the input schema accepts a free-form DSL
/// object rather than trying to constrain it. We rely on the human review
/// step to catch nonsense.
/// </summary>
public sealed class RequestOpenSearchQueryTool : IDebugTool
{
    private readonly IHumanDataProvider _provider;

    public RequestOpenSearchQueryTool(IHumanDataProvider provider)
    {
        _provider = provider;
    }

    public string Name => "request_opensearch_query";

    public string Description =>
        "Ask the engineer to run an OpenSearch query against a CoCo index and paste back " +
        "matching documents. Use this to check indexing state — e.g. 'is this document " +
        "indexed?', 'what does the search tier see?', 'are there stale duplicates?'. " +
        "Build the Query DSL body to be as narrow as possible. The engineer will see " +
        "the full query before running it.";

    public JsonElement InputSchema { get; } = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "index": {
              "type": "string",
              "description": "Index name or pattern, e.g. 'content-activities' or 'content-*'."
            },
            "queryDsl": {
              "type": "object",
              "description": "OpenSearch Query DSL body. Example: { \"query\": { \"term\": { \"_id\": \"act-789\" } } }"
            },
            "size": {
              "type": "integer",
              "description": "Max hits to return. Keep small — usually 1-10 is enough for debugging.",
              "minimum": 1,
              "maximum": 50
            },
            "sourceFields": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Optional _source field allowlist. If you only need 3 fields, say so — saves the engineer from copying unrelated data."
            },
            "environment": {
              "type": "string",
              "description": "Environment suggestion: 'test', 'staging', or 'live'. The engineer can pick a different one.",
              "enum": ["test", "staging", "live"]
            },
            "reason": {
              "type": "string",
              "description": "One sentence: what hypothesis will this query confirm or rule out?"
            }
          },
          "required": ["index", "queryDsl", "size", "environment", "reason"]
        }
        """
    ).RootElement.Clone();

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement input, CancellationToken ct)
    {
        if (!input.TryGetProperty("index", out var index) ||
            !input.TryGetProperty("queryDsl", out var dsl) ||
            !input.TryGetProperty("size", out var size) ||
            !input.TryGetProperty("environment", out var env) ||
            !input.TryGetProperty("reason", out var reason))
        {
            return new ToolExecutionResult(
                "Error: index, queryDsl, size, environment, and reason are required.",
                IsError: true);
        }

        IReadOnlyList<string>? sourceFields = null;
        if (input.TryGetProperty("sourceFields", out var sf) && sf.ValueKind == JsonValueKind.Array)
        {
            sourceFields = sf.EnumerateArray()
                .Select(e => e.GetString() ?? "")
                .Where(s => s.Length > 0)
                .ToList();
        }

        var rendered = RenderQuery(
            index: index.GetString() ?? "",
            dsl: dsl,
            size: size.GetInt32(),
            sourceFields: sourceFields);

        var request = new HumanDataRequest(
            SourceName: "OpenSearch",
            RenderedQuery: rendered,
            Reason: reason.GetString() ?? "",
            SuggestedEnv: env.GetString());

        var result = await _provider.RequestDataAsync(request, ct);

        if (result is null)
        {
            return new ToolExecutionResult(
                "Engineer declined. Consider a different angle — maybe a Mongo check or a CloudWatch log search.",
                IsError: false);
        }

        if (string.IsNullOrWhiteSpace(result))
        {
            return new ToolExecutionResult(
                "No hits returned — the query matched zero documents. If you expected a doc to be indexed, this is strong evidence it's missing.",
                IsError: false);
        }

        return new ToolExecutionResult(
            $"OpenSearch result:\n{result}",
            IsError: false);
    }

    private static string RenderQuery(
        string index,
        JsonElement dsl,
        int size,
        IReadOnlyList<string>? sourceFields)
    {
        var opts = new JsonSerializerOptions { WriteIndented = true };

        // Compose a full search body so what the user pastes into their tool
        // is a ready-to-run payload, not something they have to stitch together.
        var body = new Dictionary<string, object?>
        {
            ["size"] = size,
        };

        // _source handling — if the agent asked for specific fields, include
        // them; otherwise leave the key out (returns full _source).
        if (sourceFields is not null && sourceFields.Count > 0)
        {
            body["_source"] = sourceFields;
        }

        // dsl is already a fully-formed "query" block — usually it IS the
        // root query. We merge its top-level keys so the user can paste the
        // final body into Kibana DevTools or a curl call directly.
        using var dslDoc = JsonDocument.Parse(dsl.GetRawText());
        foreach (var prop in dslDoc.RootElement.EnumerateObject())
        {
            body[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText());
        }

        var bodyJson = JsonSerializer.Serialize(body, opts);
        return $"POST /{index}/_search\n{bodyJson}";
    }
}
