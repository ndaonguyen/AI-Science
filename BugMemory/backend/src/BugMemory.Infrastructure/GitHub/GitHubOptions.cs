namespace BugMemory.Infrastructure.GitHub;

/// <summary>
/// Configuration for the GitHub search provider.
///
/// Goes in appsettings.Development.json under "GitHub:". All fields
/// except RepoAllowlist are required. If any required field is missing,
/// IsConfigured returns false and the Ask use case skips this provider.
///
/// Example appsettings.Development.json (gitignored):
///   "GitHub": {
///     "PersonalAccessToken": "github_pat_...",
///     "RepoAllowlist": [
///       "your-org/content-media-service",
///       "your-org/authoring-service"
///     ]
///   }
///
/// The PAT should be a fine-grained token scoped to ONLY the repos you
/// want this tool to read. GitHub's fine-grained tokens at
/// https://github.com/settings/tokens?type=beta have per-repo selection
/// and per-permission scopes — pick "Contents: Read-only" and "Metadata:
/// Read-only" as the minimum.
/// </summary>
public sealed class GitHubOptions
{
    /// <summary>
    /// Fine-grained personal access token (starts with 'github_pat_')
    /// or classic PAT (starts with 'ghp_'). Fine-grained is preferred —
    /// per-repo scoping makes accidental over-permissioning impossible.
    /// </summary>
    public string PersonalAccessToken { get; set; } = "";

    /// <summary>
    /// Repos this provider is allowed to search, in 'owner/repo' form.
    /// Required — without an allowlist, the provider would search ALL
    /// repos the PAT can see, which is rarely what you want. Empty
    /// list = no searches (provider reports IsConfigured = false).
    /// </summary>
    public List<string> RepoAllowlist { get; set; } = new();
}
