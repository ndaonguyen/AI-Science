using System.Text;
using BugMemory.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BugMemory.Infrastructure.CodeScan;

public sealed class LocalRepoCodeScanner : IRepoCodeScanner
{
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "node_modules", "bin", "obj", "dist", "build",
        "out", "coverage", ".next", ".nuxt", "__pycache__", ".pytest_cache",
        "target", "vendor", "packages", ".gradle"
    };

    private static readonly HashSet<string> ScannableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".java", ".kt", ".go",
        ".rb", ".php", ".rs", ".swift", ".scala", ".sql", ".yml", ".yaml",
        ".json", ".md", ".csproj", ".sln", ".toml"
    };

    private const int MaxTreeEntriesPerRepo = 60;
    private const int MaxSnippetsPerRepo = 12;
    private const int MaxSnippetCharsPerRepo = 4000;
    private const int SnippetContextLines = 1;

    private readonly ServiceReposOptions _options;
    private readonly ILogger<LocalRepoCodeScanner> _logger;

    public LocalRepoCodeScanner(IOptions<ServiceReposOptions> options, ILogger<LocalRepoCodeScanner> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public RepoScanResult Scan(
        IReadOnlyList<string> serviceNames,
        IReadOnlyList<string> keywords,
        CancellationToken ct)
    {
        var resolved = new List<ResolvedRepo>();
        var unresolved = new List<string>();
        var snapshot = new StringBuilder();

        var distinctKeywords = keywords
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .Where(k => k.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        foreach (var name in serviceNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!_options.Paths.TryGetValue(name, out var path) || string.IsNullOrWhiteSpace(path))
            {
                unresolved.Add(name);
                continue;
            }

            if (!Directory.Exists(path))
            {
                _logger.LogWarning("Configured repo path for {Service} does not exist: {Path}", name, path);
                unresolved.Add(name);
                continue;
            }

            resolved.Add(new ResolvedRepo(name, path));
            AppendRepoSnapshot(snapshot, name, path, distinctKeywords, ct);
        }

        return new RepoScanResult(resolved, unresolved, snapshot.ToString());
    }

    private static void AppendRepoSnapshot(
        StringBuilder sb,
        string serviceName,
        string rootPath,
        IReadOnlyList<string> keywords,
        CancellationToken ct)
    {
        sb.Append("=== Repo: ").Append(serviceName).Append(" (").Append(rootPath).AppendLine(") ===");

        sb.AppendLine("File tree (truncated):");
        var tree = BuildShallowTree(rootPath, MaxTreeEntriesPerRepo, ct);
        foreach (var line in tree)
        {
            sb.Append("  ").AppendLine(line);
        }
        sb.AppendLine();

        if (keywords.Count > 0)
        {
            sb.Append("Keyword matches for: ").AppendLine(string.Join(", ", keywords));
            var snippets = FindSnippets(rootPath, keywords, ct);
            if (snippets.Count == 0)
            {
                sb.AppendLine("  (no matches found)");
            }
            else
            {
                foreach (var snippet in snippets)
                {
                    sb.AppendLine(snippet);
                }
            }
            sb.AppendLine();
        }
    }

    private static List<string> BuildShallowTree(string root, int max, CancellationToken ct)
    {
        var entries = new List<string>();
        try
        {
            WalkDirectory(root, root, 0, maxDepth: 2, entries, max, ct);
        }
        catch (UnauthorizedAccessException)
        {
            // skip
        }
        catch (IOException)
        {
            // skip
        }
        return entries;
    }

    private static void WalkDirectory(
        string root,
        string current,
        int depth,
        int maxDepth,
        List<string> entries,
        int max,
        CancellationToken ct)
    {
        if (depth > maxDepth || entries.Count >= max) return;
        ct.ThrowIfCancellationRequested();

        IEnumerable<string> subdirs;
        IEnumerable<string> files;
        try
        {
            subdirs = Directory.EnumerateDirectories(current);
            files = Directory.EnumerateFiles(current);
        }
        catch (UnauthorizedAccessException) { return; }
        catch (DirectoryNotFoundException) { return; }
        catch (IOException) { return; }

        foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (entries.Count >= max) return;
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            entries.Add(rel);
        }

        foreach (var dir in subdirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            if (entries.Count >= max) return;
            var name = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(name) || IgnoredDirectories.Contains(name) || name.StartsWith("."))
            {
                continue;
            }
            var rel = Path.GetRelativePath(root, dir).Replace('\\', '/') + "/";
            entries.Add(rel);
            WalkDirectory(root, dir, depth + 1, maxDepth, entries, max, ct);
        }
    }

    private static List<string> FindSnippets(
        string root,
        IReadOnlyList<string> keywords,
        CancellationToken ct)
    {
        var snippets = new List<string>();
        var totalChars = 0;

        IEnumerable<string> files;
        try
        {
            files = EnumerateScannableFiles(root, ct);
        }
        catch (UnauthorizedAccessException)
        {
            return snippets;
        }

        foreach (var file in files)
        {
            if (snippets.Count >= MaxSnippetsPerRepo || totalChars >= MaxSnippetCharsPerRepo) break;
            ct.ThrowIfCancellationRequested();

            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            for (var i = 0; i < lines.Length; i++)
            {
                if (snippets.Count >= MaxSnippetsPerRepo || totalChars >= MaxSnippetCharsPerRepo) break;

                var line = lines[i];
                var matched = keywords.FirstOrDefault(k =>
                    line.Contains(k, StringComparison.OrdinalIgnoreCase));
                if (matched is null) continue;

                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                var start = Math.Max(0, i - SnippetContextLines);
                var end = Math.Min(lines.Length - 1, i + SnippetContextLines);
                var snippet = new StringBuilder();
                snippet.Append("  ").Append(rel).Append(':').Append(i + 1)
                    .Append(" [match: ").Append(matched).AppendLine("]");
                for (var j = start; j <= end; j++)
                {
                    var marker = j == i ? "> " : "  ";
                    var text = lines[j];
                    if (text.Length > 200) text = text.Substring(0, 200) + "...";
                    snippet.Append("    ").Append(marker).AppendLine(text);
                }
                var text2 = snippet.ToString();
                totalChars += text2.Length;
                snippets.Add(text2);
            }
        }

        return snippets;
    }

    private static IEnumerable<string> EnumerateScannableFiles(string root, CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch (UnauthorizedAccessException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            catch (IOException) { continue; }

            foreach (var file in files)
            {
                var ext = Path.GetExtension(file);
                if (ScannableExtensions.Contains(ext))
                {
                    yield return file;
                }
            }

            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch (UnauthorizedAccessException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            catch (IOException) { continue; }

            foreach (var sub in subdirs)
            {
                var name = Path.GetFileName(sub);
                if (string.IsNullOrEmpty(name) || IgnoredDirectories.Contains(name) || name.StartsWith("."))
                {
                    continue;
                }
                stack.Push(sub);
            }
        }
    }
}
