using System.Text.Json;
using Amazon;
using Amazon.CloudWatchLogs;
using Amazon.CloudWatchLogs.Model;
using DistributedDebugger.Core.Tools;

namespace DistributedDebugger.Tools.CloudWatch;

/// <summary>
/// Real CloudWatch Logs search tool. Replaces MockLogSearchTool in Phase 2.
///
/// Pipeline:
///
///   [1] Time window narrowing — only fetch logs in the window the bug
///       happened (free, deterministic, biggest reduction).
///   [2] Server-side filter pattern — CloudWatch's filter-log-events filters
///       by substring BEFORE sending over the network (free).
///   [3] Chunking into LogChunk records (one per event).
///   [4] Top-K retrieval via ILogRetriever — keyword, semantic, or hybrid
///       (tiny cost for semantic; zero for keyword).
///
/// Steps 1 and 2 together kill 90%+ of log volume before we spend a cent on
/// retrieval. That's the core lesson of RAG: spend deterministic filters
/// first, AI filters last.
///
/// Auth: uses the default AWS credential chain — picks up your SSO session
/// from ~/.aws/config automatically. If you get an UnauthorizedException at
/// runtime, run `aws sso login` and try again.
/// </summary>
public sealed class CloudWatchLogSearchTool : IDebugTool, IDisposable
{
    private readonly ILogRetriever _retriever;
    private readonly string _defaultRegion;
    private readonly int _topK;
    private readonly int _maxContextTokens;

    // Cache clients per region — creating one is cheap but not free.
    private readonly Dictionary<string, AmazonCloudWatchLogsClient> _clientsByRegion = new();

    public CloudWatchLogSearchTool(
        ILogRetriever retriever,
        string defaultRegion = "ap-southeast-2",
        int topK = 25,
        int maxContextTokens = 4000)
    {
        _retriever = retriever;
        _defaultRegion = defaultRegion;
        _topK = topK;
        _maxContextTokens = maxContextTokens;
    }

    public string Name => "search_logs";

    public string Description =>
        "Search real CloudWatch logs for an EP service in a given environment and " +
        "time window. Uses RAG: first narrows by time and keyword on the server, " +
        "then re-ranks by relevance to your query. Returns a concise set of the " +
        "most relevant log lines — not a raw dump. " +
        $"Known services: {string.Join(", ", ServiceLogGroupResolver.KnownServices)}. " +
        $"Known environments: {string.Join(", ", ServiceLogGroupResolver.KnownEnvironments)}.";

    public JsonElement InputSchema { get; } = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "service": {
              "type": "string",
              "description": "Service name, e.g. 'content-media-service'."
            },
            "environment": {
              "type": "string",
              "description": "Environment: 'test', 'staging', 'live', or 'live-ca-central-1'."
            },
            "query": {
              "type": "string",
              "description": "What you're looking for — e.g. 'OpenSearch timeout for act-789'. Used for semantic retrieval ranking."
            },
            "filterPattern": {
              "type": "string",
              "description": "Optional CloudWatch filter pattern for cheap server-side filtering. Example: 'ERROR' or 'act-789'. Omit to fetch all logs in the window."
            },
            "startTime": {
              "type": "string",
              "description": "ISO-8601 start time (e.g. '2024-01-15T14:00:00Z'). Defaults to 1 hour before endTime."
            },
            "endTime": {
              "type": "string",
              "description": "ISO-8601 end time. Defaults to now."
            }
          },
          "required": ["service", "environment", "query"]
        }
        """
    ).RootElement.Clone();

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement input, CancellationToken ct)
    {
        string Get(string key) =>
            input.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? ""
                : "";

        var service = Get("service");
        var environment = Get("environment");
        var query = Get("query");
        var filterPattern = Get("filterPattern");
        var startStr = Get("startTime");
        var endStr = Get("endTime");

        if (string.IsNullOrWhiteSpace(service) ||
            string.IsNullOrWhiteSpace(environment) ||
            string.IsNullOrWhiteSpace(query))
        {
            return new ToolExecutionResult(
                "Error: 'service', 'environment', and 'query' are all required.",
                IsError: true);
        }

        // Resolve log group + region from our EP naming convention.
        string logGroup, region;
        try
        {
            logGroup = ServiceLogGroupResolver.Resolve(service, environment);
            region = ServiceLogGroupResolver.ResolveRegion(environment, _defaultRegion);
        }
        catch (ArgumentException ex)
        {
            return new ToolExecutionResult($"Error: {ex.Message}", IsError: true);
        }

        // Parse / default the time window.
        var end = DateTimeOffset.TryParse(endStr, out var parsedEnd)
            ? parsedEnd : DateTimeOffset.UtcNow;
        var start = DateTimeOffset.TryParse(startStr, out var parsedStart)
            ? parsedStart : end.AddHours(-1);

        if (end <= start)
        {
            return new ToolExecutionResult(
                $"Error: endTime ({end:o}) must be after startTime ({start:o}).",
                IsError: true);
        }

        // Call AWS.
        IReadOnlyList<LogChunk> chunks;
        try
        {
            chunks = await FetchLogsAsync(
                region, logGroup, service, filterPattern, start, end, ct);
        }
        catch (AmazonCloudWatchLogsException ex)
        {
            // Auth errors are the common case — point the user at `aws sso login`.
            return new ToolExecutionResult(
                $"CloudWatch error ({ex.ErrorCode ?? "unknown"}): {ex.Message}. " +
                "If this is an auth issue, run `aws sso login` and retry.",
                IsError: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ToolExecutionResult(
                $"Unexpected error fetching logs: {ex.Message}",
                IsError: true);
        }

        if (chunks.Count == 0)
        {
            return new ToolExecutionResult(
                $"No log events in {logGroup} between {start:yyyy-MM-dd HH:mm}Z " +
                $"and {end:yyyy-MM-dd HH:mm}Z" +
                (string.IsNullOrEmpty(filterPattern) ? "." : $" matching '{filterPattern}'.") +
                " This is itself a signal — the expected log may not have been emitted.",
                IsError: false);
        }

        // Rank/select via the retriever.
        var top = await _retriever.RetrieveAsync(query, chunks, _topK, _maxContextTokens, ct);

        return new ToolExecutionResult(
            FormatResult(logGroup, start, end, chunks.Count, top),
            IsError: false);
    }

    private async Task<IReadOnlyList<LogChunk>> FetchLogsAsync(
        string region,
        string logGroup,
        string service,
        string filterPattern,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken ct)
    {
        var client = GetClient(region);

        var request = new FilterLogEventsRequest
        {
            LogGroupName = logGroup,
            StartTime = start.ToUnixTimeMilliseconds(),
            EndTime = end.ToUnixTimeMilliseconds(),
            Limit = 500, // hard cap — protects us from an over-eager window
        };

        if (!string.IsNullOrWhiteSpace(filterPattern))
        {
            request.FilterPattern = filterPattern;
        }

        // Paginate, but cap total fetched to avoid runaway costs/time.
        var all = new List<LogChunk>();
        do
        {
            var response = await client.FilterLogEventsAsync(request, ct);
            foreach (var ev in response.Events)
            {
                all.Add(new LogChunk(
                    Service: service,
                    LogGroup: logGroup,
                    Timestamp: DateTimeOffset.FromUnixTimeMilliseconds(ev.Timestamp ?? 0),
                    Text: ev.Message ?? ""));
            }

            request.NextToken = response.NextToken;

            // Bail once we have enough raw material; the retriever will trim.
            if (all.Count >= 2000) break;
        } while (!string.IsNullOrEmpty(request.NextToken));

        return all;
    }

    private AmazonCloudWatchLogsClient GetClient(string region)
    {
        if (!_clientsByRegion.TryGetValue(region, out var client))
        {
            var endpoint = RegionEndpoint.GetBySystemName(region);
            client = new AmazonCloudWatchLogsClient(endpoint);
            _clientsByRegion[region] = client;
        }
        return client;
    }

    private static string FormatResult(
        string logGroup,
        DateTimeOffset start,
        DateTimeOffset end,
        int totalFetched,
        IReadOnlyList<LogChunk> top)
    {
        var header =
            $"Log group: {logGroup}\n" +
            $"Window: {start:yyyy-MM-dd HH:mm}Z → {end:yyyy-MM-dd HH:mm}Z\n" +
            $"Fetched {totalFetched} events, showing top {top.Count} by relevance:\n";

        var body = string.Join("\n", top.Select(c => c.Render()));
        return header + "\n" + body;
    }

    public void Dispose()
    {
        foreach (var client in _clientsByRegion.Values)
        {
            client.Dispose();
        }
        _clientsByRegion.Clear();
    }
}
