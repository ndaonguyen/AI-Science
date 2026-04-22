using System.Text.Json;
using Amazon;
using Amazon.CloudWatchLogs;
using Amazon.CloudWatchLogs.Model;
using Amazon.Runtime;

namespace DistributedDebugger.Web;

/// <summary>
/// Lightweight CloudWatch fetcher used by the /api/logs/raw endpoint.
/// Returns raw log lines with timestamps — no AI, no RAG, no session.
/// </summary>
public static class RawLogFetcher
{
    public static async Task<List<RawLogEvent>> FetchAsync(
        string region,
        string logGroup,
        string profile,
        string filterText,
        DateTimeOffset start,
        DateTimeOffset end,
        int limit,
        CancellationToken ct)
    {
        var credentials = LoadCredentialsViaCli(profile);
        var endpoint    = RegionEndpoint.GetBySystemName(region);

        using var client = credentials is not null
            ? new AmazonCloudWatchLogsClient(credentials, endpoint)
            : new AmazonCloudWatchLogsClient(endpoint);

        var request = new FilterLogEventsRequest
        {
            LogGroupName = logGroup,
            StartTime    = start.ToUnixTimeMilliseconds(),
            EndTime      = end.ToUnixTimeMilliseconds(),
            Limit        = Math.Min(limit, 500),
        };

        if (!string.IsNullOrWhiteSpace(filterText))
            request.FilterPattern = filterText;

        var results = new List<RawLogEvent>();
        do
        {
            var response = await client.FilterLogEventsAsync(request, ct);
            foreach (var ev in response.Events)
            {
                results.Add(new RawLogEvent(
                    Timestamp: DateTimeOffset.FromUnixTimeMilliseconds(ev.Timestamp)
                                            .ToString("yyyy-MM-dd HH:mm:ss.fff") + " UTC",
                    Message:   ExtractLogText(ev.Message ?? ""),
                    Stream:    ev.LogStreamName ?? ""));
            }
            request.NextToken = response.NextToken;
            if (results.Count >= limit) break;
        } while (!string.IsNullOrEmpty(request.NextToken));

        return results;
    }

    private static string ExtractLogText(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw[0] != '{') return raw;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("log", out var logProp) &&
                logProp.ValueKind == JsonValueKind.String)
                return logProp.GetString()?.Trim() ?? raw;
        }
        catch { /* not JSON */ }
        return raw;
    }

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
            if (proc.ExitCode != 0) return null;

            using var doc   = JsonDocument.Parse(stdout);
            var root        = doc.RootElement;
            var accessKey   = root.GetProperty("AccessKeyId").GetString()!;
            var secretKey   = root.GetProperty("SecretAccessKey").GetString()!;
            var token       = root.TryGetProperty("SessionToken", out var t) ? t.GetString() : null;

            return string.IsNullOrWhiteSpace(token)
                ? new BasicAWSCredentials(accessKey, secretKey)
                : new SessionAWSCredentials(accessKey, secretKey, token);
        }
        catch { return null; }
    }
}

public sealed record RawLogEvent(string Timestamp, string Message, string Stream);

public sealed record RawLogsRequest(
    string Service,
    string Environment,
    string? StartTime    = null,
    string? EndTime      = null,
    string? FilterText   = null);

