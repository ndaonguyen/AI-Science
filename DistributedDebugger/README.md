# DistributedDebugger

AI-powered bug investigator for distributed microservice systems.

You describe a bug — or paste in a Jira ticket — and an agent does the grunt work of pulling logs, tracing events, and producing a structured root-cause report. Built to practice **harness engineering** and **RAG** on a real problem: investigating bugs across a stack of microservices where the cause can be anywhere.

## Status: Phase 2 (real CloudWatch + RAG)

What works today:

- ReAct loop with OpenAI (`gpt-4o-mini` by default)
- Freeform bug description via `--desc` or `--desc-file`, optional `--ticket` id
- Tools:
  - `search_logs` — **real CloudWatch Logs** via AWS SDK (SSO-aware), with a 4-stage RAG pipeline
  - `fetch_kafka_events` — still mock (Phase 3 target)
  - `record_hypothesis` — agent declares a working theory, added to the trace
  - `finish_investigation` — typed root-cause report
- Three swappable retrievers: `keyword`, `semantic`, `hybrid` (default)
- `--mock` flag to bypass CloudWatch entirely for fast, free iteration
- Streaming step-by-step output + markdown report written to `investigations/`

## The RAG pipeline (Phase 2's learning goldmine)

Every `search_logs` call flows through four stages. The first two are free and deterministic; the last two cost a fraction of a cent.

```
  Stage 1: Time-window narrowing            ← free, biggest reduction
  Stage 2: CloudWatch filter pattern        ← free, server-side
  Stage 3: Chunking + embedding             ← tiny cost
  Stage 4: Top-K by cosine similarity       ← tiny cost
```

That ordering is the core RAG lesson — spend deterministic filters first, AI filters last. See `src/DistributedDebugger.Tools/CloudWatch/CloudWatchLogSearchTool.cs` for the pipeline and `SemanticLogRetriever.cs` for the embedding step.

## What's next

| Phase | Feature |
|---|---|
| 3 | MongoDB document-state and OpenSearch index-state tools |
| 3 | Real Kafka event lookup (replace `MockKafkaEventTool`) |
| 4 | LLM-as-judge grader, regression suite, cost/quality dashboard |

## Quick start

```bash
# One-time: log in via SSO so the AWS SDK has credentials
aws sso login

export OPENAI_API_KEY=sk-...

cd DistributedDebugger

# Free, deterministic run (no AWS, no embeddings) — use while iterating
dotnet run --project src/DistributedDebugger.Cli -- investigate --mock \
  --desc "Activity act-789 published at 14:27 UTC but not in content search"

# Real run against your CoCo CloudWatch logs
dotnet run --project src/DistributedDebugger.Cli -- investigate \
  --ticket COCO-1234 \
  --desc "Activity act-789 published at 14:27 UTC but not in content search" \
  --retriever hybrid
```

## Architecture

Layered the same way as `HarnessArena`:

```
DistributedDebugger.Core     domain models + tool interface
DistributedDebugger.Tools    the tools the agent can call
  └─ CloudWatch/             log search tool + RAG retrievers
DistributedDebugger.Agent    the ReAct loop (OpenAI)
DistributedDebugger.Cli      CLI entry point + markdown report renderer
```

The agent in `InvestigatorAgent.cs` is the core piece. The loop is deliberately
very similar to `HarnessArena/OpenAIAgent.cs` — same pattern, different system
prompt, different tools. That's the whole point of the harness pattern: the loop
stays provider-agnostic while the tools change per domain.

## Auth and cost

**AWS**: Uses the default credential chain. Works out of the box if your SSO
session is active. Run `aws sso login` when it expires.

**OpenAI cost per real investigation** (`gpt-4o-mini` + `text-embedding-3-small`):
- ~200 log events fetched, embedded, top-K retrieved → ~$0.0001
- ~8 model iterations generating the analysis → ~$0.001
- **Total: ~$0.001–0.002** (roughly one-tenth of a cent)

Run with `--mock` while iterating on prompts. Switch to real data when you're
investigating an actual bug.
