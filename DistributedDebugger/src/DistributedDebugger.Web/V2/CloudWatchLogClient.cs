using System.Collections.Concurrent;
using Amazon;
using Amazon.CloudWatchLogs;
using Amazon.CloudWatchLogs.Model;
using Amazon.Runtime;
using DistributedDebugger.Tools.CloudWatch;

namespace DistributedDebugger.Web.V2;

/// <summary>
/// Plain CloudWatch Logs client for the V2 (deterministic) flow.
///
/// Compared with <see cref="CloudWatchLogSearchTool"/> which is shaped as
/// an LLM tool (JSON schema, IDebugTool, ToolExecutionResult), this is just
/// a normal C# service with a normal API. No prompts, no tool registry,
/// no overrides — the user's filter text and time range go to AWS exactly
/// as supplied.
///
/// Two operations:
///   - SearchAsync:    keyword + time range → matching log lines
///   - ExtendAsync:    pivot timestamp + window → all logs around it
///
/// Both return <see cref="LogRecord"/> arrays (lightweight DTOs the browser
/// renders directly). No retrieval, no ranking, no truncation by relevance —
/// V2's analyse step does that on a curated set the user has already chosen.
///
/// AWS auth strategy is identical to the existing tool: shells out to
/// `aws configure export-credentials --profile X` so users only need to
/// `aws sso login --profile X` once per session. Per-environment profile
/// mapping is shared via <see cref="ResolveProfile"/>.
/// </summary>
public sealed class CloudWatchLogClient : IDisposable
{
    // One AmazonCloudWatchLogsClient per (region, profile) pair. AWS clients
    // are documented as thread-safe and meant to be reused; constructing
    // them is expensive enough that caching matters under load.
    private readonly ConcurrentDictionary<string, AmazonCloudWatchLogsClient> _clients = new();
    private readonly string _defaultRegion;

    public CloudWatchLogClient(string defaultRegion = "ap-southeast-2")
    {
        _defaultRegion = defaultRegion;
    }

    /// <summary>
    /// Filter logs in [start, end] matching the given filterPattern. Empty
    /// filter pattern = return everything in the window (capped). The
    /// filterPattern is passed through verbatim — callers should already
    /// have shaped it for CloudWatch (multi-word phrases double-quoted).
    /// </summary>
    public async Task<IReadOnlyList<LogRecord>> SearchAsync(
        string service,
        string environment,
        string filterPattern,
        DateTimeOffset start,
        DateTimeOffset end,
        int limit,
        CancellationToken ct)
    {
        var logGroup = ServiceLogGroupResolver.Resolve(service, environment);
        var client = GetClient(_defaultRegion, environment);

        var request = new FilterLogEventsRequest
        {
            LogGroupName = logGroup,
            StartTime = start.ToUnixTimeMilliseconds(),
            EndTime = end.ToUnixTimeMilliseconds(),
            // AWS caps Limit at 10k per page anyway; we keep the page
            // small and paginate so very-active log groups don't hammer
            // memory before we hit our overall cap below.
            Limit = Math.Min(limit, 1000),
        };
        if (!string.IsNullOrWhiteSpace(filterPattern))
        {
            request.FilterPattern = filterPattern;
        }

        // Trace request shape so the Rider console shows exactly what we
        // asked AWS for. Useful when the result is empty and you need to
        // confirm the filter / time / log group actually went through.
        Console.Error.WriteLine(
            $"[v2/cw] search → logGroup={logGroup} " +
            $"window={start:yyyy-MM-dd HH:mm:ss.fff}Z → {end:yyyy-MM-dd HH:mm:ss.fff}Z " +
            $"filter='{filterPattern}' limit={limit}");

        var results = new List<LogRecord>();
        var pageCount = 0;
        try
        {
            do
            {
                var response = await client.FilterLogEventsAsync(request, ct);
                pageCount++;
                foreach (var ev in response.Events)
                {
                    results.Add(new LogRecord(
                        Service: service,
                        LogGroup: logGroup,
                        Timestamp: DateTimeOffset.FromUnixTimeMilliseconds(ev.Timestamp),
                        Message: ev.Message ?? "",
                        EventId: ev.EventId));
                    if (results.Count >= limit)
                    {
                        Console.Error.WriteLine(
                            $"[v2/cw] hit limit={limit} after {pageCount} page(s); aborting pagination");
                        return results;
                    }
                }
                request.NextToken = response.NextToken;
            } while (!string.IsNullOrEmpty(request.NextToken));
        }
        catch (Amazon.CloudWatchLogs.Model.ResourceNotFoundException)
        {
            // Surface a friendlier message that names the actual log group
            // path and the most likely fix (wrong env name in the dropdown).
            // See V3/CloudWatchLogClient.cs for the same wrapper.
            throw new InvalidOperationException(
                $"CloudWatch log group '{logGroup}' does not exist. " +
                $"Check that environment '{environment}' is one of: " +
                $"{string.Join(", ", DistributedDebugger.Tools.CloudWatch.ServiceLogGroupResolver.KnownEnvironments)}, " +
                $"and that service '{service}' actually deploys to that environment. " +
                $"Also verify the AWS profile resolved for this environment has access to the right account.");
        }

        Console.Error.WriteLine(
            $"[v2/cw] done → {results.Count} events across {pageCount} page(s)");

        return results;
    }

    /// <summary>
    /// Fetch logs around a pivot timestamp without any filter. Used by the
    /// "Extend ± N min" action — the user picks a row and wants context.
    /// </summary>
    public Task<IReadOnlyList<LogRecord>> ExtendAsync(
        string service,
        string environment,
        DateTimeOffset pivot,
        TimeSpan windowEachSide,
        int limit,
        CancellationToken ct)
    {
        var start = pivot - windowEachSide;
        var end = pivot + windowEachSide;
        // Search with empty filter = "everything in the window".
        return SearchAsync(service, environment, "", start, end, limit, ct);
    }

    private AmazonCloudWatchLogsClient GetClient(string region, string environment)
    {
        var profile = ResolveProfile(environment);
        var key = $"{region}:{profile}";
        return _clients.GetOrAdd(key, _ =>
        {
            Console.Error.WriteLine(
                $"[v2/cw] GetClient: building new client region={region} profile={profile}");
            var endpoint = RegionEndpoint.GetBySystemName(region);
            var credentials = LoadCredentialsViaCli(profile);
            if (credentials is null)
            {
                // Fall back to SDK's default chain — but for a desktop run
                // this almost certainly means the request will fail. V1's
                // working behaviour proves the CLI shell-out can succeed,
                // so a null result here points at a real bug (PATH, CLI
                // version, expired SSO). Surface this prominently.
                Console.Error.WriteLine(
                    $"[v2/cw] WARNING: credential load returned null for profile '{profile}'. " +
                    "Falling back to SDK default chain — this will likely fail with 'Unable to " +
                    "get IAM security credentials from EC2 Instance Metadata Service' once the " +
                    "actual AWS call runs. Check the [v2/cw] message above for the cause.");
                return new AmazonCloudWatchLogsClient(endpoint);
            }
            Console.Error.WriteLine(
                $"[v2/cw] credentials loaded: {credentials.GetType().Name}");
            return new AmazonCloudWatchLogsClient(credentials, endpoint);
        });
    }

    /// <summary>
    /// Same env→profile mapping the existing tool uses. Keeping it duplicated
    /// here (rather than calling into CloudWatchLogSearchTool's private copy)
    /// because the V1 tool may be deleted later. Single source of truth can
    /// be unified once V2 is the only remaining flow.
    /// </summary>
    private static string ResolveProfile(string environment) =>
        environment.ToLowerInvariant() switch
        {
            "live" or "production" or "prod" => "live",
            "live-ca" or "live-ca-central-1"  => "live-ca",
            "staging" or "stg"                => "staging",
            "test" or "dev"                   => "dev",
            _ => Environment.GetEnvironmentVariable("AWS_PROFILE") ?? "dev",
        };

    /// <summary>
    /// Shells out to `aws configure export-credentials --profile X` to
    /// resolve credentials. Mirrors what the V1 tool does; SSO sessions
    /// stored under ~/.aws/sso/cache work without any extra setup.
    ///
    /// On failure, logs loudly to stderr — silent fallback was hiding the
    /// real error and letting the SDK fall through to the EC2 metadata
    /// service, which produced a baffling 'Unable to get IAM security
    /// credentials from EC2 Instance Metadata Service' on the user's
    /// laptop. Better to surface the actual aws CLI error message.
    /// </summary>
    private static SessionAWSCredentials? LoadCredentialsViaCli(string profileName)
    {
        try
        {
            // Use the default JSON output — '--format process-credentials'
            // doesn't exist in older AWS CLI builds and would silently fail.
            var psi = new System.Diagnostics.ProcessStartInfo("aws",
                $"configure export-credentials --profile {profileName}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null)
            {
                Console.Error.WriteLine(
                    "[v2/cw] could not start 'aws' — is the AWS CLI installed and on PATH? " +
                    "If running Rider via a Windows shortcut, the spawned process inherits " +
                    "the SYSTEM PATH, not your shell's. Restart Rider from a terminal where " +
                    "`aws --version` works, or add aws to the system PATH.");
                return null;
            }

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(5000);

            if (proc.ExitCode != 0)
            {
                Console.Error.WriteLine(
                    $"[v2/cw] aws CLI exited {proc.ExitCode} for profile '{profileName}': " +
                    $"{stderr.Trim()}\n" +
                    $"Run:  aws sso login --profile {profileName}");
                return null;
            }
            if (string.IsNullOrWhiteSpace(stdout))
            {
                Console.Error.WriteLine(
                    $"[v2/cw] aws CLI returned empty stdout for profile '{profileName}'");
                return null;
            }

            using var doc = System.Text.Json.JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            return new SessionAWSCredentials(
                root.GetProperty("AccessKeyId").GetString(),
                root.GetProperty("SecretAccessKey").GetString(),
                root.GetProperty("SessionToken").GetString());
        }
        catch (Exception ex)
        {
            // Catch-all so a malformed config doesn't crash the whole request,
            // but log the type and message so the cause is visible.
            Console.Error.WriteLine(
                $"[v2/cw] credential load threw {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var client in _clients.Values)
        {
            try { client.Dispose(); } catch { /* best-effort */ }
        }
        _clients.Clear();
    }
}

/// <summary>
/// Lightweight log row sent to the browser. Includes the AWS EventId so the
/// browser can deduplicate when the user runs Extend in overlapping windows
/// (the same physical log will then come back twice with the same EventId).
/// </summary>
public sealed record LogRecord(
    string Service,
    string LogGroup,
    DateTimeOffset Timestamp,
    string Message,
    string? EventId);
