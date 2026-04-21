using HarnessArena.Core.Models;

namespace HarnessArena.Core.Grading;

public sealed record GradeResult(
    bool Passed,
    double Score,        // 0.0 - 1.0
    string Grader,       // which grader produced this
    string? Rationale
);

public interface IGrader
{
    string Name { get; }
    Task<GradeResult> GradeAsync(TaskDefinition task, Run run, CancellationToken ct);
}
