namespace BugMemory.Eval;

/// <summary>
/// One bug entry in the seed corpus. Loaded from seed-bugs.yaml and
/// indexed into a dedicated harness Qdrant collection at run start.
///
/// The Id field is a stable string ("kafka-retry-dup-key") and is what
/// EvalCase.ExpectedBugIds references. Internally the entity gets a fresh
/// Guid each run (via BugMemoryEntry.Create), and the harness maintains
/// a stable-id → Guid map so the retrieval grader can look up which
/// run-time Guids correspond to the case's expected stable ids.
/// </summary>
public sealed class SeedBug
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public string Context { get; set; } = "";
    public string RootCause { get; set; } = "";
    public string Solution { get; set; } = "";
}
