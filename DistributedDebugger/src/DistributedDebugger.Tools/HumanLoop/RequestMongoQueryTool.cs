using System.Text.Json;
using DistributedDebugger.Core.Tools;

namespace DistributedDebugger.Tools.HumanLoop;

/// <summary>
/// Agent-facing tool that asks the human engineer to run a MongoDB find query
/// and paste back the result. Deliberately constrained to read-only finds —
/// there is no write path, no aggregation pipeline execution, no admin
/// commands. The agent must not be able to ask the user to run something
/// destructive.
///
/// The tool renders a Mongo shell-style query so the human can copy-paste
/// straight into Compass, mongosh, or a read-replica tool.
/// </summary>
public sealed class RequestMongoQueryTool : IDebugTool
{
    private readonly IHumanDataProvider _provider;

    public RequestMongoQueryTool(IHumanDataProvider provider)
    {
        _provider = provider;
    }

    public string Name => "request_mongo_query";

    public string Description =>
        "Ask the engineer to run a MongoDB find query against the CoCo database and paste " +
        "back the result. Use this when you need to confirm document state — e.g. 'does " +
        "activity X exist?', 'what's its current status?', 'is the isEpContent flag set?'. " +
        "Be specific: narrow by _id when you can, project only the fields you need, limit " +
        "results tightly. The engineer will see your query before running it.";

    public JsonElement InputSchema { get; } = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "collection": {
              "type": "string",
              "description": "Collection name, e.g. 'activities', 'contents', 'users'."
            },
            "filter": {
              "type": "object",
              "description": "MongoDB filter document. Prefer { _id: '...' } or a small set of equality filters. Example: { _id: 'act-789' }."
            },
            "projection": {
              "type": "object",
              "description": "Fields to return. Example: { status: 1, isEpContent: 1, publishedAt: 1 }. Include only what's needed to test your hypothesis."
            },
            "limit": {
              "type": "integer",
              "description": "Max documents to return. Keep small — 1 for id lookups, 10 max for filter queries.",
              "minimum": 1,
              "maximum": 50
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
          "required": ["collection", "filter", "limit", "environment", "reason"]
        }
        """
    ).RootElement.Clone();

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement input, CancellationToken ct)
    {
        if (!input.TryGetProperty("collection", out var coll) ||
            !input.TryGetProperty("filter", out var filter) ||
            !input.TryGetProperty("limit", out var limit) ||
            !input.TryGetProperty("environment", out var env) ||
            !input.TryGetProperty("reason", out var reason))
        {
            return new ToolExecutionResult(
                "Error: collection, filter, limit, environment, and reason are required.",
                IsError: true);
        }

        JsonElement? projection = null;
        if (input.TryGetProperty("projection", out var p) && p.ValueKind == JsonValueKind.Object)
        {
            projection = p;
        }

        var query = RenderMongoQuery(
            collection: coll.GetString() ?? "",
            filter: filter,
            projection: projection,
            limit: limit.GetInt32());

        var request = new HumanDataRequest(
            SourceName: "MongoDB",
            RenderedQuery: query,
            Reason: reason.GetString() ?? "",
            SuggestedEnv: env.GetString());

        var result = await _provider.RequestDataAsync(request, ct);

        if (result is null)
        {
            return new ToolExecutionResult(
                "Engineer declined to run this query. Reason may be privacy, time, or access. " +
                "Try a different angle — look at CloudWatch logs or a narrower query.",
                IsError: false);
        }

        if (string.IsNullOrWhiteSpace(result))
        {
            return new ToolExecutionResult(
                "No data returned — the query matched zero documents. This is itself a signal " +
                "(maybe the document doesn't exist, or the filter was too strict).",
                IsError: false);
        }

        return new ToolExecutionResult(
            $"MongoDB result:\n{result}",
            IsError: false);
    }

    /// <summary>
    /// Render the tool-call args as a mongosh-style command the engineer can
    /// copy-paste. We re-serialize filter/projection with indentation so they
    /// read clearly.
    /// </summary>
    private static string RenderMongoQuery(
        string collection,
        JsonElement filter,
        JsonElement? projection,
        int limit)
    {
        var opts = new JsonSerializerOptions { WriteIndented = true };
        var filterJson = JsonSerializer.Serialize(filter, opts);
        var projJson = projection.HasValue
            ? JsonSerializer.Serialize(projection.Value, opts)
            : null;

        var findArgs = projJson is null
            ? filterJson
            : $"{filterJson},\n{projJson}";

        return $"db.{collection}.find(\n{Indent(findArgs)}\n).limit({limit})";
    }

    private static string Indent(string block) =>
        string.Join("\n", block.Split('\n').Select(line => "  " + line));
}
