namespace BugMemory.Application.Abstractions;

/// <summary>
/// One hit from an external source (Jira ticket, GitHub commit, etc).
///
/// Designed to be uniform across providers so the Ask use case can merge
/// hits from multiple providers and hand them to the LLM with consistent
/// provenance. The LLM cites by the <see cref="Url"/> field (clickable in
/// the UI) rather than by an internal id, since these hits aren't
/// persisted in our system.
/// </summary>
/// <param name="Provider">
/// Stable provider identifier — "jira" or "github". The UI uses this to
/// choose a citation icon / label and to wire up the "save as bug
/// memory" button to the right source-specific pre-fill flow.
/// </param>
/// <param name="ExternalId">
/// Provider-native id ("COCO-1234" for Jira, "owner/repo@abcdef0" for
/// GitHub). Stable per hit, used by the "save as bug" flow to re-fetch
/// the same source for pre-fill rather than passing the full body
/// through the URL query string.
/// </param>
/// <param name="Url">Canonical URL the user can open in a browser.</param>
/// <param name="Title">
/// Short, human-readable line: ticket summary, commit message subject,
/// PR title. What the UI shows as the citation header.
/// </param>
/// <param name="Snippet">
/// The body content the LLM reads to ground its answer. For Jira:
/// description + recent comments + resolution. For GitHub: commit
/// message + diff hunks (or PR description). Length-capped per provider
/// (Jira ~3KB, GitHub commit ~5KB) to keep total prompt size sane when
/// merging many hits.
/// </param>
/// <param name="When">
/// Timestamp on the original artifact (ticket created/updated, commit
/// authored). Used to bias recency in merge/rerank. Null if the
/// provider didn't return a usable date.
/// </param>
/// <param name="Score">
/// Provider-native relevance score in [0..1] if available, otherwise
/// 1.0 (treat as "this matched, take it"). Jira's text-search and
/// GitHub's pickaxe both produce rankings we can normalize.
/// </param>
public sealed record ExternalHit(
    string Provider,
    string ExternalId,
    string Url,
    string Title,
    string Snippet,
    DateTimeOffset? When,
    double Score);

/// <summary>
/// One source of external knowledge that the Ask use case can include
/// in retrieval. Implementations live in Infrastructure (JiraSearchProvider,
/// GitHubSearchProvider) and are wired by name into the per-query source
/// list the user toggles in the UI.
///
/// Failure mode: if a provider is misconfigured or unreachable, the
/// implementation should THROW with a clear message — the use case
/// catches per-provider exceptions so a Jira outage doesn't take down
/// the whole Ask. The Application layer doesn't try to make providers
/// self-healing; that's an Infrastructure-level decision (e.g. add
/// caching, retry policies) per provider.
/// </summary>
public interface IExternalSearchProvider
{
    /// <summary>
    /// Stable identifier — matches the <see cref="ExternalHit.Provider"/>
    /// values. The Ask use case resolves user-selected source names
    /// against this property.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Indicates whether the provider has the configuration it needs
    /// to run (PAT set, base URL set, etc). The Ask use case skips
    /// providers that report false rather than calling them and
    /// receiving a clear-but-noisy auth failure for every query.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Run a search. The query is the user's raw question text — providers
    /// are responsible for translating it into provider-native search
    /// syntax (JQL, git pickaxe, GitHub search qualifiers). topK is a
    /// hint, not a hard limit; small overshoots are fine, but providers
    /// should make a reasonable effort to cap at topK or close.
    /// </summary>
    Task<IReadOnlyList<ExternalHit>> SearchAsync(
        string query,
        int topK,
        CancellationToken ct);
}
