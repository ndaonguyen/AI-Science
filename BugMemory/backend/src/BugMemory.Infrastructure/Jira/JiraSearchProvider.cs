using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BugMemory.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BugMemory.Infrastructure.Jira;

/// <summary>
/// External search provider for Jira Cloud. Translates the user's
/// question into a JQL text search, fetches matching issues plus their
/// recent comments, returns them as <see cref="ExternalHit"/>s the Ask
/// use case can feed to the LLM.
///
/// Auth: HTTP Basic with email + API token (Atlassian's PAT equivalent).
/// Token goes in JiraOptions; HttpClient is registered as typed in
/// InfrastructureServiceCollectionExtensions.
///
/// API: Jira Cloud REST API v3
///   - POST /rest/api/3/search/jql       (text-search returning issues)
///   - GET  /rest/api/3/issue/{key}/comment (fetch comments)
///
/// The /search/jql endpoint takes JQL we construct as:
///   text ~ "<question>" AND (<DefaultJqlFilter>)
///   ORDER BY updated DESC
///
/// "text ~ ..." searches summary + description + comments combined.
/// ORDER BY updated DESC biases toward recent tickets — usually more
/// useful for debugging since current state is what we're investigating.
/// </summary>
public sealed class JiraSearchProvider : IExternalSearchProvider
{
    private readonly HttpClient _http;
    private readonly JiraOptions _options;
    private readonly ILogger<JiraSearchProvider> _logger;

    public string Name => "jira";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.BaseUrl) &&
        !string.IsNullOrWhiteSpace(_options.Email) &&
        !string.IsNullOrWhiteSpace(_options.ApiToken);

    public JiraSearchProvider(
        HttpClient http,
        IOptions<JiraOptions> options,
        ILogger<JiraSearchProvider> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;

        // Configure the typed HttpClient with auth + base URL. Doing it
        // here (not in DI registration) keeps the auth concern co-located
        // with the provider that uses it. Idempotent: calling twice on
        // the same client is harmless.
        if (IsConfigured)
        {
            // BaseAddress wants a trailing slash for relative paths to
            // resolve right. We accept it without — append if missing.
            var baseUrl = _options.BaseUrl.TrimEnd('/') + "/";
            _http.BaseAddress ??= new Uri(baseUrl);

            // HTTP Basic auth header: base64("email:apitoken")
            var creds = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{_options.Email}:{_options.ApiToken}"));
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", creds);

            _http.DefaultRequestHeaders.Accept.Clear();
            _http.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
        }
    }

    public async Task<IReadOnlyList<ExternalHit>> SearchAsync(
        string query,
        int topK,
        CancellationToken ct)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("Jira provider called while not configured — returning empty");
            return Array.Empty<ExternalHit>();
        }
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<ExternalHit>();
        }

        var jql = BuildJql(query);
        _logger.LogDebug("Jira search: jql={Jql}", jql);

        var searchRequest = new
        {
            jql = jql,
            // Fields to return on each issue. We pull description here
            // (saves a per-issue fetch) plus the metadata we need for
            // the ExternalHit. 'description' on v3 comes back as ADF JSON,
            // which we flatten in AdfTextExtractor.
            fields = new[] { "summary", "description", "status", "updated", "issuetype" },
            // Hard cap on Jira's side. We pass topK directly; Jira will
            // round-trip whatever we ask up to its server-side max (5000).
            maxResults = topK,
            // Order by recency — debugging usually cares about current state
            // more than ancient history. JQL ORDER BY is embedded in the
            // jql string, not a separate param, but we set it via BuildJql.
        };

        IssueSearchResponse? searchResponse;
        try
        {
            using var resp = await _http.PostAsJsonAsync(
                "rest/api/3/search/jql", searchRequest, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException(
                    $"Jira search returned {(int)resp.StatusCode}: {Truncate(body, 1024)}");
            }
            searchResponse = await resp.Content.ReadFromJsonAsync<IssueSearchResponse>(
                JsonOpts, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Jira search failed");
            throw;
        }

        if (searchResponse?.Issues is null || searchResponse.Issues.Count == 0)
        {
            return Array.Empty<ExternalHit>();
        }

        // Convert each issue to an ExternalHit. Comments are fetched in
        // parallel — they're the slowest part (N round trips) but
        // independent, so paralleling helps a lot for topK=5.
        var commentFetches = searchResponse.Issues
            .Select(issue => FetchCommentsAsync(issue.Key, ct))
            .ToList();
        var commentResults = await Task.WhenAll(commentFetches);

        var hits = new List<ExternalHit>(searchResponse.Issues.Count);
        for (int i = 0; i < searchResponse.Issues.Count; i++)
        {
            var issue = searchResponse.Issues[i];
            var comments = commentResults[i];
            hits.Add(BuildHit(issue, comments));
        }
        return hits;
    }

    private string BuildJql(string question)
    {
        // Escape internal quotes in the user's question — Jira's JQL
        // text-match takes a quoted string, so embedded quotes break
        // parsing. Backslash-escape per Atlassian docs.
        var escaped = question.Replace("\\", "\\\\").Replace("\"", "\\\"");

        var sb = new StringBuilder();
        sb.Append("text ~ \"").Append(escaped).Append('"');
        if (!string.IsNullOrWhiteSpace(_options.DefaultJqlFilter))
        {
            // Wrap the user filter in parens so operator precedence is
            // explicit. If the filter has its own ORs, this prevents
            // them from binding loosely with the text match.
            sb.Append(" AND (").Append(_options.DefaultJqlFilter).Append(')');
        }
        sb.Append(" ORDER BY updated DESC");
        return sb.ToString();
    }

    private async Task<List<CommentBody>> FetchCommentsAsync(string issueKey, CancellationToken ct)
    {
        if (_options.CommentsPerIssue <= 0)
            return new List<CommentBody>();

        try
        {
            // We ask for the most-recent comments — '?orderBy=-created'
            // gives reverse-chronological. maxResults caps the fetch.
            using var resp = await _http.GetAsync(
                $"rest/api/3/issue/{Uri.EscapeDataString(issueKey)}/comment" +
                $"?orderBy=-created&maxResults={_options.CommentsPerIssue}",
                ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Comments fetch for {Key} returned {Status} — skipping comments for this issue",
                    issueKey, (int)resp.StatusCode);
                return new List<CommentBody>();
            }
            var payload = await resp.Content.ReadFromJsonAsync<CommentSearchResponse>(
                JsonOpts, ct);
            return payload?.Comments ?? new List<CommentBody>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Don't fail the whole search if one ticket's comments
            // can't be fetched — return the issue with no comments.
            _logger.LogWarning(ex, "Comments fetch for {Key} threw — skipping", issueKey);
            return new List<CommentBody>();
        }
    }

    private ExternalHit BuildHit(Issue issue, List<CommentBody> comments)
    {
        // Build the snippet: title + status + description + most recent
        // comments. Cap each section so one verbose ticket doesn't
        // dominate the prompt.
        var sb = new StringBuilder();
        sb.Append(issue.Key).Append(' ').Append(issue.Fields?.Summary ?? "(no summary)")
          .AppendLine();

        if (!string.IsNullOrEmpty(issue.Fields?.Status?.Name))
        {
            sb.Append("Status: ").Append(issue.Fields.Status.Name).AppendLine();
        }

        var description = AdfTextExtractor.Flatten(issue.Fields?.Description);
        if (!string.IsNullOrWhiteSpace(description))
        {
            sb.AppendLine().Append("Description:").AppendLine();
            sb.AppendLine(Truncate(description, 1500));
        }

        if (comments.Count > 0)
        {
            sb.AppendLine().Append("Recent comments:").AppendLine();
            foreach (var c in comments)
            {
                var body = AdfTextExtractor.Flatten(c.Body);
                if (!string.IsNullOrWhiteSpace(body))
                {
                    var author = c.Author?.DisplayName ?? "(unknown)";
                    sb.Append("- ").Append(author).Append(": ");
                    sb.AppendLine(Truncate(body, 600));
                }
            }
        }

        var url = $"{_options.BaseUrl.TrimEnd('/')}/browse/{issue.Key}";

        // Score: Jira doesn't return one. Use 1.0 — every hit is a
        // text match, the ordering itself is the rank signal. The Ask
        // use case can rerank if it wants; we don't second-guess Jira's
        // ordering here.
        return new ExternalHit(
            Provider: "jira",
            ExternalId: issue.Key,
            Url: url,
            Title: issue.Fields?.Summary ?? issue.Key,
            Snippet: sb.ToString(),
            When: issue.Fields?.Updated,
            Score: 1.0);
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
        return s[..max] + "...";
    }

    // ----- JSON shapes for the Jira REST API responses we consume -----

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class IssueSearchResponse
    {
        [JsonPropertyName("issues")]
        public List<Issue> Issues { get; set; } = new();
    }

    private sealed class Issue
    {
        [JsonPropertyName("key")] public string Key { get; set; } = "";
        [JsonPropertyName("fields")] public IssueFields? Fields { get; set; }
    }

    private sealed class IssueFields
    {
        [JsonPropertyName("summary")] public string? Summary { get; set; }
        [JsonPropertyName("description")] public JsonElement? Description { get; set; }
        [JsonPropertyName("status")] public IssueStatus? Status { get; set; }
        [JsonPropertyName("updated")] public DateTimeOffset? Updated { get; set; }
    }

    private sealed class IssueStatus
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
    }

    private sealed class CommentSearchResponse
    {
        [JsonPropertyName("comments")] public List<CommentBody> Comments { get; set; } = new();
    }

    private sealed class CommentBody
    {
        [JsonPropertyName("body")] public JsonElement? Body { get; set; }
        [JsonPropertyName("author")] public CommentAuthor? Author { get; set; }
    }

    private sealed class CommentAuthor
    {
        [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    }
}
