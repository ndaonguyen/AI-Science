namespace BugMemory.Application.Abstractions;

public sealed record RepoScanResult(
    IReadOnlyList<ResolvedRepo> Resolved,
    IReadOnlyList<string> Unresolved,
    string Snapshot);

public sealed record ResolvedRepo(string ServiceName, string Path);

public interface IRepoCodeScanner
{
    RepoScanResult Scan(
        IReadOnlyList<string> serviceNames,
        IReadOnlyList<string> keywords,
        CancellationToken ct);
}
