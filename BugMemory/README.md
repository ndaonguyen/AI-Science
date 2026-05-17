# Bug Memory

A personal RAG-powered bug knowledge base. Capture bug context, root cause, and solution as you encounter them — then ask natural-language questions later and get answers grounded in your own past experience.

## Architecture

Clean architecture with strict dependency direction: `Domain ← Application ← Infrastructure ← API`.

```
backend/
├── src/
│   ├── BugMemory.Domain/          ← Entities, no dependencies
│   ├── BugMemory.Application/     ← Use cases, abstractions, DTOs
│   ├── BugMemory.Infrastructure/  ← OpenAI, Qdrant, JSON file repo
│   ├── BugMemory.Api/             ← Minimal API + static frontend (wwwroot/)
│   └── BugMemory.Eval/            ← Eval harness (console app)
└── tests/
    └── BugMemory.UnitTests/       ← xUnit + Moq + AwesomeAssertions

eval/                              ← Seed corpus + cases for the harness
docker-compose.yml                 ← Qdrant
```

The frontend is plain HTML/CSS/JS in `BugMemory.Api/wwwroot/`, served by ASP.NET as static files alongside the API. One process, one port — the same pattern as DistributedDebugger.

### SOLID at a glance

- **Single responsibility** — each use case is one class with one `ExecuteAsync`. `CreateBugMemoryUseCase`, `AskBugMemoryUseCase`, `ExtractBugFieldsUseCase`, etc.
- **Open/closed** — swap providers by implementing the interface. Replace OpenAI with Ollama? Implement `IEmbeddingService` and `ILlmService`. Replace Qdrant with pgvector? Implement `IVectorStore`. Replace JSON file with EF Core? Implement `IBugMemoryRepository`. The Application layer never changes.
- **Liskov** — `BugMemoryEntry.Hydrate` and `BugMemoryEntry.Create` both produce the same kind of entity; consumers don't need to care.
- **Interface segregation** — `IEmbeddingService`, `IVectorStore`, `ILlmService`, `IBugMemoryRepository`, `IClock` are tiny and focused. Nothing implements a fat "do everything" interface.
- **Dependency inversion** — use cases depend on abstractions in `Application`. Infrastructure plugs in via `AddInfrastructure`.

### How RAG works here

1. **Index on write** — when you save a bug, the entity emits a structured embedding text (`Title / Tags / Context / Root cause / Solution`), `IEmbeddingService` turns it into a 1536-dim vector via OpenAI's `text-embedding-3-small`, and `IVectorStore` upserts it into Qdrant under the bug's id.
2. **Retrieve on ask** — your question is embedded the same way, Qdrant returns the top-K nearest neighbors by cosine similarity, and the use case fetches the full entries from the repository.
3. **Generate** — the retrieved entries are passed as context to GPT-4o with a strict prompt: answer only from these sources, return the ids you actually used. The response is parsed and the citation list is filtered to those ids.

The two extras beyond a vanilla RAG: the **extract** endpoint runs the same LLM in a different prompt that turns raw chat/Slack threads into structured fields, and the **ask** response always includes citations so you can trust-but-verify.

## Running locally

### Prerequisites

- .NET 9 SDK (or later — roll-forward to current LTS works fine)
- Docker (for Qdrant)
- An OpenAI API key

### 1. Start Qdrant

```bash
docker compose up -d
```

Qdrant runs on `localhost:6333`. Dashboard at `http://localhost:6333/dashboard`.

### 2. Configure secrets

The repo has `appsettings.Development.json.example` as a template. Copy it
to a real file (which is gitignored, so it never gets committed):

```bash
cd backend/src/BugMemory.Api
cp appsettings.Development.json.example appsettings.Development.json
# then edit appsettings.Development.json and replace the placeholder with your real key
```

Or use user secrets (keeps the secret out of the working tree entirely):

```bash
cd backend/src/BugMemory.Api
dotnet user-secrets init
dotnet user-secrets set "OpenAi:ApiKey" "sk-..."
```

### 3. Run the app

```bash
cd backend
dotnet run --project src/BugMemory.Api
```

Open `http://localhost:5080` in your browser. The frontend (Ask / Add /
All tabs) and the API (`/api/*`) both live on this single port. Swagger
is at `/swagger`.

### 4. Run tests

```bash
cd backend
dotnet test
```

### 5. Run the eval harness (optional)

The harness measures whether prompt or model changes are improving or
regressing answer quality. Two graders run per case:

- **Retrieval grader** (deterministic): does the right past bug appear
  in top-K? Computes precision and recall.
- **Answer grader** (LLM-as-judge using gpt-4o): does the generated
  answer meet the case's stated criteria?

Run all configs (baseline / cheap-model / narrow-retrieval) over the
case suite:

```bash
cd backend
dotnet run --project src/BugMemory.Eval
```

Or pick specific configs:

```bash
dotnet run --project src/BugMemory.Eval -- \
  --config baseline --config cheap-model
```

The harness uses a SEPARATE Qdrant collection (`bug_memories_eval`)
and a separate JSON file under your OS temp dir, so synthetic eval
cases never pollute your real bug-memories. The collection is wiped
and re-seeded at the start of each run for reproducibility.

Cases live in `eval/cases/*.yaml` — one file per case. Add new cases by
copying an existing file and editing. The seed corpus is `eval/seed-bugs.yaml`.

## API endpoints

| Method | Path | Purpose |
|--------|------|---------|
| `GET`    | `/api/bugs`         | List all bugs |
| `GET`    | `/api/bugs/{id}`    | Get one bug |
| `POST`   | `/api/bugs`         | Create a bug (also indexes embedding) |
| `PUT`    | `/api/bugs/{id}`    | Update a bug (re-indexes) |
| `DELETE` | `/api/bugs/{id}`    | Delete from store and vector index |
| `POST`   | `/api/search`       | Vector similarity search → ranked list |
| `POST`   | `/api/ask`          | RAG over saved bugs only — original endpoint |
| `POST`   | `/api/ask-mixed`    | RAG over saved bugs + external sources (Jira, GitHub) |
| `POST`   | `/api/extract`      | Pull structured fields from raw text |

## External sources (Jira and GitHub)

`/api/ask-mixed` supports retrieving from external knowledge sources alongside saved bug memories. The request:

```json
POST /api/ask-mixed
{
  "question": "why did we add blockId to content-media-service?",
  "topK": 5,
  "sources": ["bugs", "jira", "github"]
}
```

Source names:
- `"bugs"` — the vector-indexed saved bug memories (always available)
- `"jira"` — Jira Cloud tickets (requires `Jira:*` config in `appsettings.Development.json`)
- `"github"` — GitHub commits in your allowlisted repos (requires `GitHub:*` config)

Omit `sources` entirely (or pass empty list) and it defaults to `["bugs"]`, matching `/api/ask` behavior.

The response includes citations from each source kind plus diagnostic info on which sources were queried and whether any errored:

```json
{
  "answer": "...",
  "bugCitations": [...],
  "externalCitations": [
    { "provider": "jira", "externalId": "COCO-1234", "url": "...", "title": "...", "score": 1.0 }
  ],
  "sourcesQueried": ["bugs", "jira"],
  "sourceErrors": []
}
```

Failure mode: if one source errors (Jira unreachable, GitHub rate-limited), it's recorded in `sourceErrors` and the answer is produced from whatever did work. Total failure (all sources empty + errored) returns a clear "no usable context" message.

### Setting up Jira

1. Generate an Atlassian API token at https://id.atlassian.com/manage-profile/security/api-tokens
2. Add the `Jira` block to your gitignored `appsettings.Development.json` (see `appsettings.Development.json.example` for template)
3. `BaseUrl` is your Atlassian Cloud URL (`https://your-org.atlassian.net`); `Email` is the account that owns the token
4. `DefaultJqlFilter` is optional — useful for scoping to a specific project (`project = COCO`) or status (`statusCategory = Done`)

### Setting up GitHub

1. Generate a fine-grained PAT at https://github.com/settings/tokens?type=beta
   - Scope to the specific repos you want this tool to read
   - Permissions: `Contents: Read-only`, `Metadata: Read-only`
2. Add the `GitHub` block to `appsettings.Development.json` with the token and a `RepoAllowlist` of `"owner/repo"` strings

`RepoAllowlist` is required — empty list means the GitHub provider reports `IsConfigured = false` and gets skipped. This is deliberate to prevent accidental global-search.

### Honest limitations

- **GitHub search is commit-message + diff-content search, not `git log -S` pickaxe.** If you want to find the commit that first added a specific string, the existing `LocalRepoCodeScanner` (used by the write-side `/api/review` flow) does that against local clones. The GitHub provider here uses GitHub's commit search API, which is good for "find commits mentioning X" but not "find when X was introduced."
- **Sending repo/ticket content to OpenAI.** External hits get fed to the LLM as context — that means commits and tickets are sent to the OpenAI Chat Completions API as prompt content. If your org has policy concerns about third-party LLM access to source code or ticket data, configure the providers accordingly (smaller allowlist, JQL filter to non-sensitive projects, etc.) or don't enable them.
- **Rate limits.** Jira: typical org-tier rate limits apply. GitHub: 30 req/min for authenticated commits search (stricter than other endpoints). The Ask use case does ~2 GitHub API calls per query (search + paging if allowlist is large). Plenty of headroom for personal use; if you ask 30+ questions per minute the rate limits will fire.

## Swapping out providers

Want to use Anthropic instead of OpenAI? Create `AnthropicLlmService : ILlmService`, register it in `InfrastructureServiceCollectionExtensions`. Same for embeddings (Voyage AI), vector store (pgvector), or persistence (Postgres). The Application and Domain layers don't change.
