# Harness Arena

A learning-oriented agent evaluation harness in .NET 10. Run agent configurations against task suites, capture full execution traces, and grade the results.

## Why this exists

Modern AI agents live or die by their harness — the scaffolding around the LLM that runs the loop, manages context, and executes tools. This project is a from-scratch harness so you can *see* exactly what agents do step by step, compare configurations, and understand why small prompt changes make big accuracy differences.

## Current state — v0

- Single agent loop talking to Claude via the official Anthropic C# SDK.
- Two tools: `calculator` and `finish`.
- Math word problem task suite loaded from YAML.
- Exact-match grader.
- CLI that runs a task suite against one config and writes JSON traces.

## Quick start

```bash
# 1. Set your API key
export ANTHROPIC_API_KEY=sk-ant-...

# 2. Restore + build
dotnet build

# 3. Run the math suite
dotnet run --project src/HarnessArena.Cli -- \
    --tasks tasks/math \
    --config baseline

# 4. Inspect traces
ls runs/
```

## Architecture

```
HarnessArena.Core       — Models, interfaces (no deps)
HarnessArena.Tools      — Tool implementations
HarnessArena.Agents     — Claude agent loop
HarnessArena.Grading    — Exact-match grader (LLM judge later)
HarnessArena.Runner     — Task × Config orchestration
HarnessArena.Cli        — Console entrypoint
```

See `docs/PLAN.md` for the full v0 → v1 → v2 build plan.

## Heads-up

The Anthropic C# SDK is in beta. Some type names in `ClaudeAgent.cs` may need adjustment when you first compile — the code documents the *shape* of the integration. Build incrementally: get a no-tool "hello" call working before wiring tools.
