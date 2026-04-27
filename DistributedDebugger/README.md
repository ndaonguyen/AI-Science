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

## V3: deterministic flow with RAG, schemas, and memory

V3 is a separate UI/endpoint variant served at `/v3.html` and `/api/v3/*`.
V2 (`/v2.html`) keeps working unchanged so you can A/B them on the same
bug. V3 adds three things on top of V2's filter/extend/analyze flow:

1. **RAG retrieval before analyze.** When the gathered log set crosses a
   threshold (default 100), V3 narrows it to the top-K most relevant lines
   using a hybrid keyword+semantic retriever. Below threshold it's a
   pass-through. Configurable via `V3_RAG_THRESHOLD` and `V3_RAG_TOPK`.

2. **Schemas bundled into every analyze prompt.** `schemas/*.md` are
   prepended as `## Reference: schemas` so the model knows CoCo's collection
   shapes without you explaining them every time.

3. **Investigation memory.** Past V3 analyses are persisted in a SQLite +
   sqlite-vec database at `~/.dd/memory.db` and retrieved by similarity
   when you start a new analysis ("you saw something like this two weeks
   ago — root cause was X"). Toggle off via the checkbox in the bug context
   card or the `DD_MEMORY_DISABLED=1` env var.

### One-time setup for V3 memory

Memory uses the [sqlite-vec](https://github.com/asg017/sqlite-vec) SQLite
extension (`vec0`). The extension is a small native library that doesn't
ship via NuGet yet, so a bootstrap script downloads the right binary for
your OS into `~/.dd/`. Skipping this step is fine — V3 will run without
memory and the status line will say `memory: unavailable (...)`. The
analyzer, RAG, and schemas all still work.

You only need to do this **once per machine**.

#### Step 1 — Run the bootstrap script

**Linux / macOS:**
```bash
cd DistributedDebugger
./scripts/bootstrap-vec.sh
```

**Windows (PowerShell):**
```powershell
cd DistributedDebugger
powershell -ExecutionPolicy Bypass -File scripts/bootstrap-vec.ps1
```

You should see output like:
```
-> fetching https://github.com/asg017/sqlite-vec/releases/...
-> extracting
[OK] installed C:\Users\you\.dd\vec0.dll
```

(Path will be `~/.dd/vec0.so` on Linux, `~/.dd/vec0.dylib` on macOS.)

#### Step 2 — Verify the binary is in place

**Linux / macOS:**
```bash
ls -la ~/.dd/vec0.*
```

**Windows (PowerShell):**
```powershell
Test-Path "$env:USERPROFILE\.dd\vec0.dll"   # should print: True
(Get-Item "$env:USERPROFILE\.dd\vec0.dll").Length   # should be ~1-2 MB
```

If the file is missing or zero bytes, re-run the bootstrap script and
read its output for download errors (most common: corporate proxy
blocking GitHub releases).

#### Step 3 — Restart and test

Restart the web app so .NET attempts to load the extension fresh, then
run any V3 analyze. Look at the status line under the analysis output:

| Line says... | Means... |
|---|---|
| `memory: 0 related (of 0)` | Working. Empty corpus on first run; will grow as you analyze. |
| `memory: 2 related (of 47)` | Working. Found 2 similar past investigations out of 47 stored. |
| `memory: off` | Toggle is unchecked or `DD_MEMORY_DISABLED=1` is set. |
| `memory: unavailable (init failed: ...)` | vec0 isn't loading. See troubleshooting below. |

#### Troubleshooting

**`SQLite Error 1: 'The specified module could not be found.'` (Windows)**

Two possible causes:

1. The DLL isn't where Microsoft.Data.Sqlite is looking. Verify with the
   Step 2 check. If `Test-Path` returns `False`, re-run the bootstrap.
2. The DLL is on disk but a **Visual C++ runtime dependency is missing**.
   Install [Microsoft Visual C++ Redistributable (x64)](https://aka.ms/vs/17/release/vc_redist.x64.exe)
   and try again. Most dev machines already have it from Visual Studio
   or Rider, but a fresh box might not.

**`cannot open shared object file` (Linux) / `image not found` (macOS)**

The native loader can't find vec0. Either:
1. The bootstrap put the file somewhere unexpected — check Step 2.
2. The file is there but for the wrong architecture (e.g. you're on
   ARM64 but downloaded x86_64). Re-run the bootstrap; it auto-detects
   `uname -m`.

**`PowerShell parse error in bootstrap-vec.ps1`**

You're on a version before commit `e817b48` (the encoding fix). Pull
the latest `main` and re-run.

**Custom location**

If `~/.dd/` isn't writable for some reason, set `DD_VEC0_PATH` to point
at the binary explicitly:
```bash
export DD_VEC0_PATH=/some/other/path/vec0.so
```

### V3 eval harness

```bash
# Run all configs (baseline / no-rag / with-memory) over the case suite.
# Eval uses /tmp/dd-eval-memory.db — synthetic cases never touch your real
# memory at ~/.dd/memory.db.
dotnet run --project src/DistributedDebugger.Cli -- eval-v3
```

Useful comparisons:
- `--config baseline --config no-rag` — does RAG help on big log sets?
- `--config baseline --config with-memory` — does memory help when cases
  share patterns?

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
