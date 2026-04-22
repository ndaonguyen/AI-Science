# DistributedDebugger

AI-powered bug investigator for distributed microservice systems.

You describe a bug — or paste in a Jira ticket — and an agent does the grunt work of pulling logs, checking MongoDB/OpenSearch/Kafka state, and producing a structured root-cause report. Built to practice **harness engineering** and **RAG** on a real problem: investigating bugs across a stack of microservices where the cause can be anywhere.

## Status: Phase 3 (human-in-the-loop data tools)

What works today:

- ReAct loop with OpenAI (`gpt-4o-mini` by default)
- Freeform bug description via `--desc` or `--desc-file`, optional `--ticket` id
- Tools:
  - `search_logs` — **real CloudWatch Logs** via AWS SDK (SSO-aware), with a 4-stage RAG pipeline
  - `request_mongo_query` — agent formulates a MongoDB find; you run it and paste the result
  - `request_opensearch_query` — same pattern for OpenSearch Query DSL
  - `request_kafka_events` — same pattern for Kafka (any env, any UI you have)
  - `record_hypothesis` — agent declares a working theory, added to the trace
  - `finish_investigation` — typed root-cause report
- Three swappable retrievers for logs: `keyword`, `semantic`, `hybrid` (default)
- `--mock` flag to bypass CloudWatch for free iteration on prompts

## Why human-in-the-loop for Mongo/OpenSearch/Kafka?

Direct programmatic access to those systems is usually (and rightly) locked down in mature engineering orgs. The agent **describes** what data it wants to see, the CLI renders the exact query in a clearly-boxed prompt, and you paste the result back. The agent continues with the new evidence.

This is better than direct access for three reasons:

1. **Works across all environments** — test, staging, live — with zero credentials
2. **You stay in control** — every query is reviewed before anything runs
3. **Better prompt engineering pressure** — the agent learns to ask *narrow, specific* questions, because you won't run a "dump everything" query

## The UX flow

```
[iter 4] 🔧 request_mongo_query (awaiting your input below)

┌─ Manual data request ──────────────────────────────────
│ Source: MongoDB  (suggested env: staging)
│ Reason: Need to confirm activity was actually published
├─ Query ────────────────────────────────────────────────
│ db.activities.find(
│     {
│       "_id": "act-789"
│     },
│     {
│       "status": 1,
│       "isEpContent": 1,
│       "publishedAt": 1
│     }
│   ).limit(1)
└────────────────────────────────────────────────────────

Paste the result below. Type 'END' on its own line when done,
or 'empty' if no match, or 'skip' to decline. Ctrl+C to abort.
> [{"_id": "act-789", "status": "published", ... }]
> END

  [iter 4] ✓ MongoDB result: ...
```

## Architecture

```
DistributedDebugger.Core
  ├─ Models/                   domain records
  └─ Tools/
     ├─ IDebugTool.cs          tool contract
     └─ IHumanDataProvider.cs  NEW: bridge for agent ↔ human

DistributedDebugger.Tools
  ├─ CloudWatch/               real Datadog-free log search + RAG retrievers
  └─ HumanLoop/                NEW: request_{mongo,opensearch,kafka} tools

DistributedDebugger.Agent      ReAct loop (OpenAI)

DistributedDebugger.Cli
  ├─ Program.cs                entry point
  ├─ ReportWriter.cs           markdown renderer
  └─ ConsoleHumanDataProvider  NEW: stdin implementation of IHumanDataProvider
```

## What's next

| Phase | Feature |
|---|---|
| 4 | LLM-as-judge grader to evaluate investigation quality |
| 4 | Regression suite — run past bugs through the agent and check root-cause accuracy |
| 4 | Cost/quality dashboard comparing retrievers, models, prompts |

## Quick start

```bash
aws sso login           # one-time per SSO session
export OPENAI_API_KEY=sk-...

cd DistributedDebugger

# Free, no AWS, no paste-in — for iterating on prompts
dotnet run --project src/DistributedDebugger.Cli -- investigate --mock \
  --desc "Activity act-789 published at 14:27 UTC but not in content search"

# Real CloudWatch + human-loop for Mongo/OpenSearch/Kafka
dotnet run --project src/DistributedDebugger.Cli -- investigate \
  --ticket COCO-1234 \
  --desc "Activity act-789 published at 14:27 UTC but not in content search"
```

When the agent calls `request_*`, the CLI pauses and shows you the query. Paste the result, type `END`, and the investigation continues.

## Cost

Per real investigation with `gpt-4o-mini` + `text-embedding-3-small`:
- ~8 model iterations, 2-3 manual query requests → ~$0.001–0.002 in API cost
- Your time: 1-2 minutes to run the requested queries

Compare with an unassisted investigation of the same bug: typically 30 min to a few hours.
