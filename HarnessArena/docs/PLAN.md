# Build plan

## v0 — weekend build (current)

Minimal end-to-end harness running math word problems.

- [x] Solution scaffolding
- [x] Core domain models (TaskDefinition, AgentConfig, Run, TraceEvent)
- [x] ITool / IToolRegistry abstractions
- [x] CalculatorTool, FinishTool
- [x] ClaudeAgent loop skeleton
- [x] YAML task loader
- [x] Exact-match grader
- [x] JSON trace persistence
- [x] CLI entrypoint
- [ ] Verify end-to-end against your API key
- [ ] 10 math tasks across difficulty tiers

## v1 — next weekend

- Parallel execution (`Parallel.ForEachAsync`)
- Multiple agent configs, run cross-product
- LLM-as-judge grader for non-exact-match tasks
- ASP.NET Core dashboard
  - Leaderboard view (config x task grid)
  - Trace viewer (step-by-step replay of a single run)
- File/text manipulation task domain

## v2 — the interesting bits

- Context management strategies (full / sliding / summarised), compared
- Tool-count ablations (3 tools vs 10 tools — measure the drop)
- Failure taxonomy: auto-classify failures (tool misuse, infinite loop, hallucinated args, etc.)
- Sub-agent orchestrator
- Cost/latency dashboard alongside accuracy

## Stretch

- SWE-bench-style coding task domain (requires sandbox)
- Tool call caching for deterministic replays
- Statistical significance testing between configs
