using System.Security.Cryptography;
using System.Text;
using BugMemory.Application.Abstractions;
using Microsoft.Extensions.Caching.Memory;

namespace BugMemory.Infrastructure.CodeScan;

public sealed class CachingRepoCodeScanner : IRepoCodeScanner
{
    private static readonly TimeSpan SlidingTtl = TimeSpan.FromMinutes(5);

    private readonly LocalRepoCodeScanner _inner;
    private readonly IMemoryCache _cache;

    public CachingRepoCodeScanner(LocalRepoCodeScanner inner, IMemoryCache cache)
    {
        _inner = inner;
        _cache = cache;
    }

    public RepoScanResult Scan(
        IReadOnlyList<string> serviceNames,
        IReadOnlyList<string> keywords,
        CancellationToken ct)
    {
        var key = BuildKey(serviceNames, keywords);
        if (_cache.TryGetValue(key, out RepoScanResult? cached) && cached is not null)
        {
            return cached;
        }

        var result = _inner.Scan(serviceNames, keywords, ct);
        _cache.Set(key, result, new MemoryCacheEntryOptions
        {
            SlidingExpiration = SlidingTtl,
            Size = 1,
        });
        return result;
    }

    private static string BuildKey(IReadOnlyList<string> services, IReadOnlyList<string> keywords)
    {
        var s = services
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal);
        var k = keywords
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal);

        var raw = "repo-scan:" + string.Join(",", s) + "|" + string.Join(",", k);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes);
    }
}
