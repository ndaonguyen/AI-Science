using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BugMemory.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BugMemory.Infrastructure.GitHub;

/// <summary>
/// External search provider for GitHub. Translates the user's question
/// into a GitHub commits-search query restricted to the configured
/// repo allowlist; returns matching commits as <see cref="ExternalHit"/>s
/// the Ask use case can feed to the LLM.
///
/// Auth: PAT (fine-grained recommended) in the Authorization header.
///
/// API: GitHub REST API v3, GET /search/commits
///   - Supports natural-language commit-message search
///   - Returns commit metadata + message; we don't pull diff bodies
///     here (cost: extra round-trip per commit) — message + linked PR
///     title is usually enough grounding for the LLM
///
/// Honest limitations spelled out:
///
///   1. NOT a git pickaxe ("find the commit that first added <string>")
///      search. That's `git log -S` against a local clone, which the
///      existing LocalRepoCodeScanner already does for the write-side
///      review flow. The GitHub API doesn't expose pickaxe; for true
///      "first commit to add blockId," reuse the LocalRepoCodeScanner
///      via that path or extend it.
///
///   2. Commits search has a custom Accept header requirement and a
///      stricter rate limit than other endpoints (30 req/min vs 5000/h
///      for authenticated users overall). We set the right header and
///      log a warning if we get a rate-limit response.
///
///   3. We search across all repos in the allowlist with ORed 'repo:'
///      qualifiers. GitHub caps the search query length to ~256 chars
///      including qualifiers — with many repos in the allowlist this
///      can hit the limit. Implementation falls back to multiple
///      requests if the query is too long (one per repo).
/// </summary>
public sealed class GitHubSearchProvider : IExternalSearchProvider
{
    private readonly HttpClient _http;
    private readonly GitHubOptions _options;
    private readonly ILogger<GitHubSearchProvider> _logger;

    public string Name => "github";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.PersonalAccessToken) &&
        _options.RepoAllowlist.Count > 0;

    public GitHubSearchProvider(
        HttpClient http,
        IOptions<GitHubOptions> options,
        ILogger<GitHubSearchProvider> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;

        if (IsConfigured)
        {
            _http.BaseAddress ??= new Uri("https://api.github.com/");
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _options.PersonalAccessToken);

            // GitHub requires a User-Agent. They reject requests without
            // one with a 403. The product name is shown in GitHub's
            // API logs if your account ever gets a notice — use
            // something identifiable.
            _http.DefaultRequestHeaders.UserAgent.Clear();
            _http.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("BugMemory", "0.1"));

            // The 'cloak-preview' accept header was historically needed
            // for commits search. As of 2024 it's no longer required but
            // doesn't hurt. We use the standard v3 accept header.
            _http.DefaultRequestHeaders.Accept.Clear();
            _http.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        }
    }

    public async Task<IReadOnlyList<ExternalHit>> SearchAsync(
        string query,
        int topK,
        CancellationToken ct)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("GitHub provider called while not configured — returning empty");
            return Array.Empty<ExternalHit>();
        }
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<ExternalHit>();
        }

        // GitHub search query syntax: '<text> repo:owner/repo'. ORing
        // many repos uses 'repo:a/b repo:c/d' (space-separated = OR
        // for the same qualifier). Query length limit ~256 chars; if
        // the allowlist would push us over, search per-repo and merge.
        var combinedQuery = BuildCombinedQuery(query, _options.RepoAllowlist);
        if (combinedQuery is not null)
        {
            return await SearchOnceAsync(combinedQuery, topK, ct);
        }
        else
        {
            // Too long; split per-repo.
            _logger.LogDebug("Allowlist too long for single query — searching per-repo");
            var perRepoTopK = Math.Max(1, topK / _options.RepoAllowlist.Count);
            var tasks = _options.RepoAllowlist
                .Select(repo => SearchOnceAsync($"{query} repo:{repo}", perRepoTopK, ct))
                .ToList();
            var results = await Task.WhenAll(tasks);
            // Merge and trim back to topK by descending score.
            return results
                .SelectMany(r => r)
                .OrderByDescending(h => h.Score)
                .Take(topK)
                .ToList();
        }
    }

    private static string? BuildCombinedQuery(string text, IReadOnlyList<string> repos)
    {
        var sb = new StringBuilder();
        sb.Append(text);
        foreach (var repo in repos)
        {
            sb.Append(" repo:").Append(repo);
        }
        var combined = sb.ToString();
        // Conservative cap — GitHub's documented limit is 256, but
        // queries that get close sometimes fail with 422. Leave headroom.
        return combined.Length > 220 ? null : combined;
    }

    private async Task<IReadOnlyList<ExternalHit>> SearchOnceAsync(
        string query, int perPage, CancellationToken ct)
    {
        var encoded = Uri.EscapeDataString(query);
        // sort=committer-date orders by commit date descending — recent
        // commits first, which is usually what you want when debugging.
        var url = $"search/commits?q={encoded}&per_page={perPage}&sort=committer-date&order=desc";

        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                // GitHub returns 403 for rate-limit hits with X-RateLimit-*
                // headers. Surface a clear message instead of a generic
                // HTTP error.
                var remaining = resp.Headers.TryGetValues("X-RateLimit-Remaining", out var v)
                    ? string.Join(",", v) : "?";
                throw new HttpRequestException(
                    $"GitHub search returned 403 (likely rate-limited, X-RateLimit-Remaining: {remaining})");
            }
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException(
                    $"GitHub search returned {(int)resp.StatusCode}: {Truncate(body, 1024)}");
            }
            var payload = await resp.Content.ReadFromJsonAsync<SearchCommitsResponse>(
                JsonOpts, ct);
            if (payload?.Items is null) return Array.Empty<ExternalHit>();

            return payload.Items.Select(BuildHit).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "GitHub search failed for query: {Query}", query);
            throw;
        }
    }

    private static ExternalHit BuildHit(CommitItem item)
    {
        var sha = item.Sha ?? "";
        var repoFullName = item.Repository?.FullName ?? "";
        var shortSha = sha.Length >= 7 ? sha[..7] : sha;

        var sb = new StringBuilder();
        sb.Append(repoFullName).Append('@').Append(shortSha).AppendLine();

        var message = item.Commit?.Message ?? "";
        if (!string.IsNullOrWhiteSpace(message))
        {
            sb.AppendLine().Append("Commit message:").AppendLine();
            sb.AppendLine(Truncate(message, 1500));
        }

        var author = item.Commit?.Author?.Name ?? "(unknown)";
        var when = item.Commit?.Author?.Date;
        if (when.HasValue)
        {
            sb.Append("Authored: ").Append(when.Value.ToString("u"))
              .Append(" by ").Append(author).AppendLine();
        }

        var url = item.HtmlUrl ?? $"https://github.com/{repoFullName}/commit/{sha}";

        // Score: GitHub returns a score in the search response. Normalize
        // to roughly 0..1 by dividing by 10 (GitHub scores are typically
        // small positives, often around 1-5). If you ever see scores way
        // outside that band the normalization can be revised.
        var score = Math.Clamp(item.Score / 10.0, 0.0, 1.0);
        if (score == 0) score = 1.0; // no score reported → treat as plain hit

        return new ExternalHit(
            Provider: "github",
            ExternalId: $"{repoFullName}@{shortSha}",
            Url: url,
            Title: FirstLine(message) ?? shortSha,
            Snippet: sb.ToString(),
            When: when,
            Score: score);
    }

    private static string? FirstLine(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var idx = s.IndexOf('\n');
        return idx < 0 ? s : s[..idx];
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
        return s[..max] + "...";
    }

    // ----- JSON shapes -----

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class SearchCommitsResponse
    {
        [JsonPropertyName("items")] public List<CommitItem> Items { get; set; } = new();
    }

    private sealed class CommitItem
    {
        [JsonPropertyName("sha")] public string? Sha { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("score")] public double Score { get; set; }
        [JsonPropertyName("commit")] public CommitBody? Commit { get; set; }
        [JsonPropertyName("repository")] public CommitRepo? Repository { get; set; }
    }

    private sealed class CommitBody
    {
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("author")] public CommitAuthor? Author { get; set; }
    }

    private sealed class CommitAuthor
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("date")] public DateTimeOffset? Date { get; set; }
    }

    private sealed class CommitRepo
    {
        [JsonPropertyName("full_name")] public string? FullName { get; set; }
    }
}
