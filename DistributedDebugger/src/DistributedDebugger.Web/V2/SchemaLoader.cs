namespace DistributedDebugger.Web.V2;

/// <summary>
/// Loads and caches reference schemas (authoring-service, content-search-service)
/// so the LogAnalyzer can prepend them to every analyze prompt. Files travel
/// with the build output via the &lt;Content&gt; entry in the .csproj — they're
/// expected at <c>{AppContext.BaseDirectory}/schemas/*.md</c>.
///
/// Read-once-and-cache because the files don't change at runtime and we don't
/// want to hit the disk on every analyze call. Thread-safety is handled via a
/// lazy that builds the list on first access.
///
/// Graceful when the folder doesn't exist (returns an empty list) so the
/// analyzer still works on a deployment where schemas were deleted or never
/// shipped — the model just won't get the reference material.
/// </summary>
public sealed class SchemaLoader
{
    private readonly Lazy<IReadOnlyList<SchemaDoc>> _docs;

    public SchemaLoader()
    {
        // Resolve the schemas folder relative to the running binary's
        // location so it works regardless of cwd. Rider's run config and
        // `dotnet run` agree on AppContext.BaseDirectory; cwd does not.
        var dir = Path.Combine(AppContext.BaseDirectory, "schemas");
        _docs = new Lazy<IReadOnlyList<SchemaDoc>>(() => LoadFrom(dir));
    }

    public IReadOnlyList<SchemaDoc> All => _docs.Value;

    private static IReadOnlyList<SchemaDoc> LoadFrom(string dir)
    {
        if (!Directory.Exists(dir))
        {
            Console.Error.WriteLine(
                $"[v2/schemas] folder not found at {dir} — analyses will run without schema context. " +
                "Schemas are optional but recommended; rebuild to copy them via the .csproj <Content> entry.");
            return Array.Empty<SchemaDoc>();
        }

        var files = Directory.GetFiles(dir, "*.md").OrderBy(f => f).ToArray();
        var docs = new List<SchemaDoc>(files.Length);
        foreach (var file in files)
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var content = File.ReadAllText(file);
                docs.Add(new SchemaDoc(name, content));
                Console.Error.WriteLine($"[v2/schemas] loaded {name} ({content.Length:N0} chars)");
            }
            catch (Exception ex)
            {
                // One bad file shouldn't take down the rest. Log and skip.
                Console.Error.WriteLine($"[v2/schemas] failed to load {file}: {ex.Message}");
            }
        }
        return docs;
    }
}

/// <summary>
/// One reference schema doc. <c>Name</c> is the filename without extension
/// (used in API responses to confirm what the analyzer saw); <c>Content</c>
/// is the raw markdown that gets injected into the prompt.
/// </summary>
public sealed record SchemaDoc(string Name, string Content);
