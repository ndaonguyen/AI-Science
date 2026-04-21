# Harness Arena

A learning-oriented agent evaluation harness in .NET 10. Run agent configurations against task suites, capture full execution traces, and grade the results.

## Why this exists

Modern AI agents live or die by their harness — the scaffolding around the LLM that runs the loop, manages context, and executes tools. This project is a from-scratch harness so you can *see* exactly what agents do step by step, compare configurations, and understand why small prompt changes make big accuracy differences.

## Current state — v0

- Three agent backends: OpenAI (live), mock (scripted, free), Claude (stub)
- Two tools: `calculator` and `finish`
- Math word problem task suite loaded from YAML (10 tasks)
- Three hand-tuned configs: `baseline`, `strict`, `no-calculator`
- Exact-match grader
- CLI that runs the suite, prints a leaderboard, writes JSON traces

## Quick start

**Try it without any API key first (mock mode):**

```powershell
dotnet build
dotnet run --project src/HarnessArena.Cli -- run --tasks tasks/math --agent mock
```

You'll see all 10 tasks run in under a second, with a leaderboard showing mock results.

**Then run against real OpenAI:**

```powershell
$env:OPENAI_API_KEY = "sk-proj-..."
dotnet run --project src/HarnessArena.Cli -- run --tasks tasks/math --agent openai --config baseline
```

## Architecture

```
HarnessArena.Core       - Models, interfaces (no deps)
HarnessArena.Tools      - Tool implementations
HarnessArena.Agents     - OpenAIAgent, FakeAgent, ClaudeAgent (stub)
HarnessArena.Grading    - Exact-match grader (LLM judge later)
HarnessArena.Runner     - Task × Config orchestration
HarnessArena.Cli        - Console entrypoint
```

Dependencies only point inward. `Core` depends on nothing. See `docs/PLAN.md` for the v0 → v1 → v2 plan.

## CLI reference

```
harness run --tasks <folder> [--agent <kind>] [--config <id> ...] [--output <folder>]

Agents:
  openai   call OpenAI API (requires OPENAI_API_KEY)   [default]
  mock     scripted fake responses, no API calls
  claude   call Anthropic API (not yet wired)

Configs:
  baseline      - tool use allowed, normal prompt
  strict        - must use calculator for every step
  no-calculator - no calculator tool, forced to compute in-head
```

## Cost expectation (OpenAI, gpt-4o-mini)

One full suite run (10 tasks, baseline config): roughly $0.005–$0.02. You can run all three configs against all tasks dozens of times on $1.
