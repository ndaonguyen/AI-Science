# DistributedDebugger

AI-powered bug investigator for distributed microservice systems, with a full evaluation harness.

You describe a bug, the agent investigates it by pulling logs and asking you targeted questions about MongoDB/OpenSearch/Kafka state, and produces a structured root-cause report. The eval harness replays recorded bugs through the same agent to measure whether prompt/model/tool changes make things better or worse over time.

## Status: Phase 4 (harness complete)

The system is now end-to-end: it can investigate live bugs AND measure its own accuracy via a regression suite.

### Live investigation (from Phase 1-3)

- ReAct loop with OpenAI (`gpt-4o-mini` default)
- Freeform bug description or Jira ticket id
- Tools: CloudWatch log search (RAG-filtered), plus human-in-the-loop `request_mongo_query`, `request_opensearch_query`, `request_kafka_events`
- Three retrievers: `keyword`, `semantic`, `hybrid`

### Evaluation harness (new in Phase 4)

- **Eval cases** — YAML files in `eval-cases/` describing a past bug, its ground truth, and pre-recorded tool responses
- **Scripted tools** — `ScriptedLogTool` + `ScriptedHumanDataProvider` stand in for CloudWatch / human prompts during eval runs, so regressions are deterministic
- **LLM-as-judge grader** — `gpt-4o` compares the agent's investigation against ground truth on four axes: cause correctness, service coverage, keyword coverage, confidence appropriateness
- **Regression runner** — runs cases × configs, prints a leaderboard, writes CSV results for diffing across runs

## Quick start

```bash
aws sso login                           # for live investigation only
export OPENAI_API_KEY=sk-...

cd DistributedDebugger

# Live investigation (costs ~$0.001–0.002 + your paste-in time)
dotnet run --project src/DistributedDebugger.Cli -- investigate \
  --ticket COCO-1234 \
  --desc "Activity act-789 published at 14:27 UTC but not in content search"

# Regression suite — compare configs across all eval cases
dotnet run --project src/DistributedDebugger.Cli -- eval \
  --config baseline --config big-model
```

Example eval output:

```
Loading eval cases from eval-cases...
Loaded 2 case(s). Running against 2 config(s). Judge model: gpt-4o.

  ✓ [baseline   ] opensearch-indexing-dlq-missing          cause=yes svc=1.00 iter=8  ...
  ✓ [baseline   ] mongo-flag-mismatch-content-hidden       cause=yes svc=0.50 iter=6  ...
  ✓ [big-model  ] opensearch-indexing-dlq-missing          cause=yes svc=1.00 iter=5  ...
  ✓ [big-model  ] mongo-flag-mismatch-content-hidden       cause=yes svc=1.00 iter=5  ...

Leaderboard (by config):
  Config         Pass         SvcCov  AvgIter  Tokens (in/out/judge)
  --------------------------------------------------------------------------
  big-model      2/2 (100%)   1.00    5.0      12400/800/3200
  baseline       2/2 (100%)   0.75    7.0      11200/700/3100
```

## Adding a new eval case

Every bug you investigate becomes a test case:

1. Run `investigate` as normal on the real bug.
2. Once you're happy with the answer, capture the key evidence the agent needed: which logs, which Mongo/OpenSearch/Kafka responses.
3. Create a new YAML file in `eval-cases/` with the case description, ground truth, and the scripted responses.
4. Run `debugger eval` — new case is automatically picked up.

Over time your suite becomes a regression safety net: any change to prompts, tools, or models can be measured against past bugs before shipping.

See `eval-cases/opensearch-indexing-dlq-missing.yaml` for the full schema.

## Architecture

```
DistributedDebugger.Core
  ├─ Models/                   domain records (BugReport, Investigation, RootCauseReport)
  └─ Tools/                    IDebugTool, IHumanDataProvider

DistributedDebugger.Tools
  ├─ CloudWatch/               real CloudWatch log search + RAG retrievers
  ├─ HumanLoop/                request_{mongo,opensearch,kafka} tools
  └─ FinishInvestigationTool, RecordHypothesisTool, MockLogSearchTool

DistributedDebugger.Agent      ReAct loop (OpenAI gpt-4o-mini)

DistributedDebugger.Eval       NEW in Phase 4
  ├─ EvalCase.cs               case + ground truth models
  ├─ YamlCaseLoader.cs         parses eval-cases/*.yaml
  ├─ Tools/                    scripted replacements for eval runs
  ├─ LlmAsJudgeGrader.cs       gpt-4o judge with deterministic pre-checks
  └─ RegressionRunner.cs       cases × configs → leaderboard

DistributedDebugger.Cli
  ├─ Program.cs                subcommand dispatch (investigate / eval)
  ├─ EvalCommand.cs            eval subcommand handler
  ├─ ReportWriter.cs           markdown renderer
  └─ ConsoleHumanDataProvider  stdin impl of IHumanDataProvider
```

## Cost

**One investigation**: ~$0.001–0.002 (gpt-4o-mini + embeddings if hybrid)

**One eval run over 2 cases × 2 configs**: ~$0.02–0.05 (gpt-4o judge dominates, so running with many cases scales roughly linearly with the judge-token column)

Run `debugger eval --config baseline` as a cheap smoke test any time you tweak the system prompt or a tool description. The judge is the expensive part — swap to `gpt-4o-mini` as judge for faster / cheaper iteration, and use `gpt-4o` before merging.
