using System.Text.Json;
using Amazon;
using Amazon.CloudWatchLogs;
using Amazon.CloudWatchLogs.Model;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using DistributedDebugger.Core.Tools;
using InvalidOperationException = System.InvalidOperationException;

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

        Console.Error.WriteLine(
            $"[CloudWatch] search: service={service} env={environment} " +
            $"window={start:yyyy-MM-dd HH:mm:ss.fff}Z → {end:yyyy-MM-dd HH:mm:ss.fff}Z " +
            $"filter='{filterPattern}'");

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
                region, logGroup, service, environment, filterPattern, start, end, ct);
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
        string environment,
        string filterPattern,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken ct)
    {
        var client = GetClient(region, environment);

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
                    Timestamp: DateTimeOffset.FromUnixTimeMilliseconds(ev.Timestamp),
                    Text: ExtractLogText(ev.Message ?? "")));
            }

            request.NextToken = response.NextToken;

            // Bail once we have enough raw material; the retriever will trim.
            if (all.Count >= 2000) break;
        } while (!string.IsNullOrEmpty(request.NextToken));

        return all;
    }

    private AmazonCloudWatchLogsClient GetClient(string region, string environment)
    {
        var profileName = ResolveProfile(environment);
        var cacheKey = $"{region}:{profileName}";

        if (!_clientsByRegion.TryGetValue(cacheKey, out var client))
        {
            var endpoint = RegionEndpoint.GetBySystemName(region);
            var credentials = LoadCredentialsViaCli(profileName);
            Console.Error.WriteLine(
                $"[CloudWatch] env='{environment}' → profile='{profileName}' → " +
                $"{credentials?.GetType().Name ?? "null (CLI failed, falling back to default chain)"}");

            client = credentials is not null
                ? new AmazonCloudWatchLogsClient(credentials, endpoint)
                : new AmazonCloudWatchLogsClient(endpoint);

            _clientsByRegion[cacheKey] = client;
        }
        return client;
    }

    /// <summary>
    /// Maps the environment name the AI uses to the matching AWS CLI profile.
    /// This means the user only needs to run `aws sso login --profile {profile}`
    /// once per session — no manual profile switching needed.
    /// </summary>
    private static string ResolveProfile(string environment) =>
        environment.ToLowerInvariant() switch
        {
            "test"                => "dev",
            "staging"             => "staging",
            "live"                => "live",
            "live-ca-central-1"   => "live-ca",
            _                     => Environment.GetEnvironmentVariable("AWS_PROFILE")?.Trim('"', '\'', ' ') ?? "dev",
        };

    private static string FormatResult(
        string logGroup,
        DateTimeOffset start,
        DateTimeOffset end,
        int totalFetched,
        IReadOnlyList<LogChunk> top)
    {
        var header =
            $"Log group: {logGroup}\n" +
            $"Window: {start:yyyy-MM-dd HH:mm:ss.fff}Z → {end:yyyy-MM-dd HH:mm:ss.fff}Z\n" +
            $"Fetched {totalFetched} events, showing top {top.Count} by relevance:\n";

        var body = string.Join("\n", top.Select(c => c.Render()));
        return header + "\n" + body;
    }

    /// <summary>
    /// Shells out to `aws configure export-credentials --profile X` to get
    /// short-lived temporary credentials. This works for every profile type
    /// (SSO, assume-role, MFA) and handles malformed config files gracefully
    /// because the CLI does the heavy lifting.
    /// Run `aws sso login --profile X` first if you get an auth error.
    /// </summary>
    private static AWSCredentials? LoadCredentialsViaCli(string profileName)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("aws",
                $"configure export-credentials --profile {profileName}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            using var proc = System.Diagnostics.Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();

            if (proc.ExitCode != 0)
            {
                var err = proc.StandardError.ReadToEnd();
                throw new InvalidOperationException(
                    $"aws configure export-credentials failed (exit {proc.ExitCode}): {err.Trim()}\n" +
                    $"Run: aws sso login --profile {profileName}");
            }

            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            var accessKey  = root.GetProperty("AccessKeyId").GetString()!;
            var secretKey  = root.GetProperty("SecretAccessKey").GetString()!;
            var token      = root.TryGetProperty("SessionToken", out var t)
                             ? t.GetString() : null;

            return string.IsNullOrWhiteSpace(token)
                ? new BasicAWSCredentials(accessKey, secretKey)
                : new SessionAWSCredentials(accessKey, secretKey, token);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CloudWatch] CLI credential load failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// EP services wrap every log line in a JSON envelope like:
    ///   {"container_id":"…","log":"  Unexpected Execution Error at /…","container_name":"authoring-service",…}
    ///
    /// The keyword/semantic retrievers work on the text content, so we want
    /// the clean inner "log" string — not the whole JSON blob. If the message
    /// isn't JSON or has no "log" field, fall back to the raw message.
    /// </summary>
    private static string ExtractLogText(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw[0] != '{') return raw;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("log", out var logProp) &&
                logProp.ValueKind == JsonValueKind.String)
            {
                return logProp.GetString()?.Trim() ?? raw;
            }
        }
        catch { /* not JSON — use raw */ }
        return raw;
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
