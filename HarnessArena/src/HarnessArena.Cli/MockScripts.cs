using HarnessArena.Agents;

namespace HarnessArena.Cli;

/// <summary>
/// Scripted responses for every task in tasks/math. Designed so the leaderboard
/// shows a realistic mix of outcomes:
///
///   - Most tasks: the agent solves them correctly (calculator -> finish).
///   - A few tasks: the agent gets the wrong answer (exercises the grader).
///   - One task: the agent never calls finish (exercises MaxIterationsHit).
///
/// Tweak these to explore edge cases without spending a cent on the API.
/// </summary>
public static class MockScripts
{
    public static readonly IReadOnlyDictionary<string, ScriptedRun> All =
        new Dictionary<string, ScriptedRun>(StringComparer.Ordinal)
        {
            // Happy path: 3 boxes of 12 apples, give away 7 -> 29.
            ["math-easy-01"] = new(new[]
            {
                Turn("I'll compute 3 * 12 - 7.",
                     Tool("calculator", """{"expression":"3 * 12 - 7"}""")),
                Turn("The answer is 29.",
                     Tool("finish", """{"answer":"29"}""")),
            }),

            // Happy path: 24 students, 1/3 absent -> 16 present.
            ["math-easy-02"] = new(new[]
            {
                Turn("Let me calculate 24 - (24 / 3).",
                     Tool("calculator", """{"expression":"24 - (24 / 3)"}""")),
                Turn("Done.", Tool("finish", """{"answer":"16"}""")),
            }),

            // Happy path: 2 books at $15 + 5 pens at $3 -> 45.
            ["math-easy-03"] = new(new[]
            {
                Turn("Computing 2*15 + 5*3.",
                     Tool("calculator", """{"expression":"2*15 + 5*3"}""")),
                Turn(null, Tool("finish", """{"answer":"45"}""")),
            }),

            // Happy path: 180 km at 60 km/h = 3h from 9:00 -> 12:00.
            ["math-medium-01"] = new(new[]
            {
                Turn("Travel time is distance / speed.",
                     Tool("calculator", """{"expression":"180 / 60"}""")),
                Turn("3 hours after 09:00 is 12:00.",
                     Tool("finish", """{"answer":"12:00"}""")),
            }),

            // WRONG ANSWER: garden path area. Agent forgets the corner squares.
            // Real answer: 44. Agent says: 40.
            ["math-medium-02"] = new(new[]
            {
                Turn("Outer rectangle area minus garden area.",
                     Tool("calculator", """{"expression":"(12+2) * (8+2) - 12*8"}""")),
                Turn("Hmm, let me reconsider without the corners.",
                     Tool("calculator", """{"expression":"2*12 + 2*8"}""")),
                Turn("The path area is 40.",
                     Tool("finish", """{"answer":"40"}""")),
            }),

            // Happy path: $80 with 25% then 10% off -> 54.
            ["math-medium-03"] = new(new[]
            {
                Turn("First discount.",
                     Tool("calculator", """{"expression":"80 * 0.75"}""")),
                Turn("Second discount on the discounted price.",
                     Tool("calculator", """{"expression":"60 * 0.9"}""")),
                Turn(null, Tool("finish", """{"answer":"54"}""")),
            }),

            // Happy path: Alice has 2/3 of 48 -> 32.
            ["math-medium-04"] = new(new[]
            {
                Turn("Alice has 2x, Bob has x, total 3x = 48.",
                     Tool("calculator", """{"expression":"2 * (48 / 3)"}""")),
                Turn("Alice has 32 marbles.",
                     Tool("finish", """{"answer":"32"}""")),
            }),

            // Happy path: work rate 1/4 + 1/6 = 5/12, so 12/5 = 2.4.
            ["math-hard-01"] = new(new[]
            {
                Turn("Combined rate is 1/4 + 1/6.",
                     Tool("calculator", """{"expression":"1/(1.0/4 + 1.0/6)"}""")),
                Turn("That gives 2.4 hours.",
                     Tool("finish", """{"answer":"2.40"}""")),
            }),

            // LOOP FAILURE: agent keeps recomputing, never calls finish.
            // This will hit MaxIterationsHit and show in the leaderboard as a fail.
            ["math-hard-02"] = new(new[]
            {
                Turn("Let me compute cost price.",
                     Tool("calculator", """{"expression":"120 / 1.2"}""")),
                Turn("New selling price.",
                     Tool("calculator", """{"expression":"120 - 18"}""")),
                Turn("Double-checking.",
                     Tool("calculator", """{"expression":"102 - 100"}""")),
                Turn("Let me verify once more.",
                     Tool("calculator", """{"expression":"102 - 100"}""")),
                Turn("Still verifying.",
                     Tool("calculator", """{"expression":"102 - 100"}""")),
                // No finish — loop will hit MaxIterationsHit.
            }),

            // Happy path: harmonic mean. 2 * 20 * 30 / 50 = 24.
            ["math-hard-03"] = new(new[]
            {
                Turn("Using the harmonic mean formula for average speed.",
                     Tool("calculator", """{"expression":"(2 * 20 * 30) / (20 + 30)"}""")),
                Turn(null, Tool("finish", """{"answer":"24.00"}""")),
            }),
        };

    private static ScriptedTurn Turn(string? thinking, ScriptedToolCall? call) =>
        new(thinking, call);

    private static ScriptedToolCall Tool(string name, string json) =>
        new(name, json);
}
