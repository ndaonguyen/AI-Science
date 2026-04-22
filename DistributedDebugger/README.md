# DistributedDebugger

AI-powered bug investigator for distributed microservice systems.

You describe a bug — or paste in a Jira ticket — and an agent does the grunt work of pulling logs, tracing Kafka events, and producing a structured root-cause report. Built to practice **harness engineering** and **RAG** on a problem I actually hit at work: investigating bugs across a stack of microservices where the cause can be anywhere.

## Status: Phase 1 (skeleton)

What works today:

- ReAct loop with OpenAI (`gpt-4o-mini` by default)
- Freeform bug description via `--desc` or `--desc-file`, optional `--ticket` id for reporting
- Four tools wired into the agent:
  - `search_logs` — mock log search (realistic CoCo-style fixtures)
  - `fetch_kafka_events` — mock Kafka event lookup
  - `record_hypothesis` — agent declares a working theory, added to the trace
  - `finish_investigation` — typed root-cause report
- Markdown report rendered to console and written to `investigations/`
- Streaming step-by-step output so you can watch the agent reason

What's next (not yet built):

| Phase | Feature |
|---|---|
| 2 | Real Datadog + CloudWatch tools replacing the mocks |
| 2 | Keyword pre-filter + time-window narrowing before RAG |
| 3 | MongoDB document-state and OpenSearch index-state tools |
| 4 | LLM-as-judge grader, regression suite, cost/quality dashboard |

## Quick start

```bash
# From AI-Science/DistributedDebugger
export OPENAI_API_KEY=sk-...

dotnet run --project src/DistributedDebugger.Cli -- investigate \
  --ticket COCO-1234 \
  --desc "Activity act-789 published at 14:27 UTC but not appearing in content search. User reports refreshing doesn't help."
```

Output goes to stdout (streaming) and a dated markdown file in `investigations/`.

## Architecture

Layered the same way as `HarnessArena`:

```
DistributedDebugger.Core     domain models + tool interface
DistributedDebugger.Tools    the tools the agent can call
DistributedDebugger.Agent    the ReAct loop (OpenAI)
DistributedDebugger.Cli      CLI entry point + markdown report renderer
```

The agent in `InvestigatorAgent.cs` is the core piece. The loop is intentionally
very similar to `HarnessArena/OpenAIAgent.cs` — same pattern, different system
prompt, different tools. That's the whole point of the harness pattern: the loop
stays provider-agnostic while the tools change per domain.

## Why mocks in Phase 1

Real Datadog/CloudWatch queries cost API quota and network round trips, and they
make the agent hard to test deterministically. The mock tools return fixtures
modelled on a real-looking CoCo indexing bug (Kafka event published, consumer
times out on OpenSearch, DLQ not configured, event silently dropped). That gives
the agent a concrete story to discover, which is exactly what you want when
iterating on the agent's prompt and tool descriptions.

Phase 2 swaps the mocks for real tools with the same `IDebugTool` interface —
nothing in the agent changes.

## Cost estimate

One investigation with mock tools and `gpt-4o-mini`:
- ~3-5 tool calls, ~6-10 model iterations
- ~3k input tokens, ~800 output tokens
- **~$0.001 per investigation** (roughly one-tenth of a cent)
