using System.Globalization;
using HarnessArena.Core.Grading;
using HarnessArena.Core.Models;

namespace HarnessArena.Grading;

/// <summary>
/// Compares the agent's final answer to the expected answer.
///
/// Strategy (in order):
///   1. If both parse as doubles, compare numerically with a small tolerance.
///      This catches "29" vs "29.0" vs " 29 " without fuss.
///   2. Otherwise fall back to a case-insensitive trimmed string comparison.
///
/// For v0 math tasks this is all you need. Non-numeric free-form answers come
/// in v1 with an LLM-as-judge grader.
/// </summary>
public sealed class ExactMatchGrader : IGrader
{
    public string Name => "exact_match";

    public Task<GradeResult> GradeAsync(TaskDefinition task, Run run, CancellationToken ct)
    {
        if (run.Status != RunStatus.Completed)
        {
            return Task.FromResult(new GradeResult(
                Passed: false,
                Score: 0.0,
                Grader: Name,
                Rationale: $"Run did not complete (status: {run.Status})."));
        }

        var actual = (run.FinalAnswer ?? "").Trim();
        var expected = task.ExpectedAnswer.Trim();

        if (actual.Length == 0)
        {
            return Task.FromResult(new GradeResult(
                false, 0.0, Name, "Agent produced no final answer."));
        }

        if (double.TryParse(actual, NumberStyles.Any, CultureInfo.InvariantCulture, out var a) &&
            double.TryParse(expected, NumberStyles.Any, CultureInfo.InvariantCulture, out var e))
        {
            var passed = Math.Abs(a - e) < 1e-6;
            return Task.FromResult(new GradeResult(
                Passed: passed,
                Score: passed ? 1.0 : 0.0,
                Grader: Name,
                Rationale: passed
                    ? $"Numeric match: {a} == {e}."
                    : $"Numeric mismatch: got {a}, expected {e}."));
        }

        var stringMatch = string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        return Task.FromResult(new GradeResult(
            Passed: stringMatch,
            Score: stringMatch ? 1.0 : 0.0,
            Grader: Name,
            Rationale: stringMatch
                ? "String match (case-insensitive)."
                : $"String mismatch: got \"{actual}\", expected \"{expected}\"."));
    }
}
