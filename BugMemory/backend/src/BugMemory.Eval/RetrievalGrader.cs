namespace BugMemory.Eval;

/// <summary>
/// Deterministic grader that scores how well retrieval surfaced the
/// expected bugs. Computes precision and recall at the top-K cutoff that
/// was actually used.
///
/// Why both metrics:
///   - Recall (did we find all the relevant bugs?) tells you whether
///     the embeddings + retrieval are good enough to hit what matters.
///   - Precision (of the K we returned, how many were relevant?) tells
///     you whether the retrieval is wasting context-window budget on
///     irrelevant sources.
///
/// Both are useful — a config that gets recall@5 = 1.0 and precision@5
/// = 0.2 is finding everything but dragging in noise; one that gets
/// precision@5 = 1.0 and recall@5 = 0.4 is targeted but missing things.
///
/// Pass criterion: by default a case 'passes retrieval' if recall is
/// 1.0 (every expected id was in the returned top-K). That's strict
/// — you can soften it to 'recall ≥ 0.5' if you want partial credit.
/// </summary>
public static class RetrievalGrader
{
    public static RetrievalScore Grade(
        IReadOnlyList<Guid> retrievedIds,
        IReadOnlyList<Guid> expectedIds)
    {
        // Defensive: empty expected = nothing to evaluate. Score as
        // perfect retrieval (vacuously) so it doesn't drag down the
        // leaderboard. This mostly comes up if a case author wrote a
        // question with no expected matches — probably a mistake but
        // don't penalise the run.
        if (expectedIds.Count == 0)
        {
            return new RetrievalScore(
                Precision: 1.0,
                Recall: 1.0,
                MatchedCount: 0,
                ExpectedCount: 0,
                RetrievedCount: retrievedIds.Count);
        }

        var expectedSet = new HashSet<Guid>(expectedIds);
        var retrievedSet = new HashSet<Guid>(retrievedIds);

        // Number of expected items that ARE in the retrieved set.
        var matched = expectedIds.Count(e => retrievedSet.Contains(e));

        var precision = retrievedIds.Count == 0
            ? 0.0
            : (double)matched / retrievedIds.Count;
        var recall = (double)matched / expectedIds.Count;

        return new RetrievalScore(
            Precision: precision,
            Recall: recall,
            MatchedCount: matched,
            ExpectedCount: expectedIds.Count,
            RetrievedCount: retrievedIds.Count);
    }
}

public sealed record RetrievalScore(
    double Precision,
    double Recall,
    int MatchedCount,
    int ExpectedCount,
    int RetrievedCount)
{
    /// <summary>
    /// Default pass criterion: every expected id was in top-K.
    /// Strict by design — the harness's job is to flag regressions, not
    /// to grade on a curve.
    /// </summary>
    public bool Passed => Recall >= 1.0;
}
