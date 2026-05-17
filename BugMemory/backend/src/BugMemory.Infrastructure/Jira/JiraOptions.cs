namespace BugMemory.Infrastructure.Jira;

/// <summary>
/// Configuration for the Jira search provider.
///
/// Goes in appsettings.Development.json under "Jira:". All fields except
/// DefaultJqlFilter are required; if any required field is missing, the
/// provider reports IsConfigured = false and the Ask use case skips it
/// rather than failing the whole query.
///
/// Example appsettings.Development.json (gitignored):
///   "Jira": {
///     "BaseUrl": "https://your-org.atlassian.net",
///     "Email": "you@your-org.com",
///     "ApiToken": "ATATT3xFfGF0...",
///     "DefaultJqlFilter": "project = COCO AND statusCategory = Done"
///   }
///
/// The DefaultJqlFilter is ANDed into every search — useful for scoping
/// to a specific project or to closed tickets only. Leave empty to
/// search the user's whole accessible Jira.
/// </summary>
public sealed class JiraOptions
{
    /// <summary>e.g. "https://your-org.atlassian.net" — no trailing slash.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>Email of the Atlassian account whose API token will authenticate.</summary>
    public string Email { get; set; } = "";

    /// <summary>
    /// Atlassian API token (generated at
    /// https://id.atlassian.com/manage-profile/security/api-tokens).
    /// </summary>
    public string ApiToken { get; set; } = "";

    /// <summary>
    /// Optional JQL fragment ANDed into every search to scope the
    /// retriever. e.g. 'project = COCO AND statusCategory = Done'.
    /// Empty string = no filter (search all accessible issues).
    /// </summary>
    public string DefaultJqlFilter { get; set; } = "";

    /// <summary>
    /// How many recent comments to pull per matched ticket. Each ticket
    /// search costs 1 + N API calls (1 for the search, 1 per ticket for
    /// comments). 3 is a balance: most useful context-density per call.
    /// 0 = skip comments entirely.
    /// </summary>
    public int CommentsPerIssue { get; set; } = 3;
}
