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
│   └── BugMemory.Api/             ← Minimal API endpoints, DI wiring
└── tests/
    └── BugMemory.UnitTests/       ← xUnit + Moq + AwesomeAssertions

frontend/   ← React + TypeScript + Vite

docker-compose.yml  ← Qdrant
```

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

- .NET 8 SDK
- Node.js 20+
- Docker (for Qdrant)
- An OpenAI API key

### 1. Start Qdrant

```bash
docker compose up -d
```

Qdrant runs on `localhost:6333`. Dashboard at `http://localhost:6333/dashboard`.

### 2. Configure secrets

Create `backend/src/BugMemory.Api/appsettings.Development.json`:

```json
{
  "OpenAi": {
    "ApiKey": "sk-..."
  }
}
```

Or use user secrets:

```bash
cd backend/src/BugMemory.Api
dotnet user-secrets init
dotnet user-secrets set "OpenAi:ApiKey" "sk-..."
```

### 3. Run the backend

```bash
cd backend
dotnet run --project src/BugMemory.Api
```

API listens on `http://localhost:5080`. Swagger at `/swagger`.

### 4. Run the frontend

```bash
cd frontend
npm install
npm run dev
```

Frontend on `http://localhost:5173`. Vite proxies `/api` to the backend.

### 5. Run tests

```bash
cd backend
dotnet test
```

## API endpoints

| Method | Path | Purpose |
|--------|------|---------|
| `GET`    | `/api/bugs`         | List all bugs |
| `GET`    | `/api/bugs/{id}`    | Get one bug |
| `POST`   | `/api/bugs`         | Create a bug (also indexes embedding) |
| `PUT`    | `/api/bugs/{id}`    | Update a bug (re-indexes) |
| `DELETE` | `/api/bugs/{id}`    | Delete from store and vector index |
| `POST`   | `/api/search`       | Vector similarity search → ranked list |
| `POST`   | `/api/ask`          | RAG: retrieve + generate grounded answer |
| `POST`   | `/api/extract`      | Pull structured fields from raw text |

## Swapping out providers

Want to use Anthropic instead of OpenAI? Create `AnthropicLlmService : ILlmService`, register it in `InfrastructureServiceCollectionExtensions`. Same for embeddings (Voyage AI), vector store (pgvector), or persistence (Postgres). The Application and Domain layers don't change.
