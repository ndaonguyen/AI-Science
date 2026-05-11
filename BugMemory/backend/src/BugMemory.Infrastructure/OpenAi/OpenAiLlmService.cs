using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BugMemory.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BugMemory.Infrastructure.OpenAi;

public sealed class OpenAiLlmService : ILlmService
{
    private readonly HttpClient _http;
    private readonly OpenAiOptions _options;
    private readonly ILogger<OpenAiLlmService> _logger;

    public OpenAiLlmService(HttpClient http, IOptions<OpenAiOptions> options, ILogger<OpenAiLlmService> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
        _http.BaseAddress ??= new Uri("https://api.openai.com/");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
    }

    public async Task<ExtractedBugFields> ExtractBugFieldsAsync(string sourceText, CancellationToken ct)
    {
        const string systemPrompt = """
            You extract structured bug-memory fields from raw source text (Slack threads, chat logs, error reports, debugging conversations).
            
            Rules:
            - title: short, specific summary (max 80 chars). No ticket prefixes unless they appear.
            - tags: 2-6 lowercase, hyphenated tags. Prefer service names, technologies, error types.
            - context: what was happening — service, environment, trigger, symptoms. 1-3 sentences, plain prose.
            - rootCause: the cause as discussed or inferred. If thread is inconclusive, prefix with "Suspected: ". 1-3 sentences.
            - solution: the fix or workaround. If unresolved, write "Unresolved — " followed by proposed direction. 1-3 sentences.
            - Strip Slack metadata (timestamps, usernames, emoji reactions) from prose fields.
            - Do not invent details not present or reasonably inferable.
            - If a field has no signal, return an empty string.
            
            Respond with JSON only, no markdown, matching: { "title": "", "tags": [], "context": "", "rootCause": "", "solution": "" }
            """;

        var request = new ChatRequest(
            _options.ChatModel,
            new[]
            {
                new ChatMessage("system", systemPrompt),
                new ChatMessage("user", sourceText),
            },
            new ResponseFormat("json_object"),
            Temperature: 0.2);

        var response = await _http.PostAsJsonAsync("v1/chat/completions", request, ct);
        await response.EnsureSuccessOrThrowWithBodyAsync("OpenAI", ct);
        var payload = await response.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken: ct)
                      ?? throw new InvalidOperationException("Empty chat response");

        var content = payload.Choices.FirstOrDefault()?.Message.Content
                      ?? throw new InvalidOperationException("No content in chat response");

        var extracted = JsonSerializer.Deserialize<ExtractionPayload>(content, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                        ?? throw new InvalidOperationException("Could not parse extraction JSON");

        return new ExtractedBugFields(
            extracted.Title ?? string.Empty,
            extracted.Tags ?? new List<string>(),
            extracted.Context ?? string.Empty,
            extracted.RootCause ?? string.Empty,
            extracted.Solution ?? string.Empty);
    }

    public async Task<ContextReview> ReviewContextAsync(
        string context,
        IReadOnlyList<string> tags,
        IReadOnlyList<string> affectedServices,
        string repoSnapshot,
        CancellationToken ct)
    {
        var tagList = tags.Count == 0 ? "(none)" : string.Join(", ", tags);
        var serviceList = affectedServices.Count == 0 ? "(none)" : string.Join(", ", affectedServices);
        var repoBlock = string.IsNullOrWhiteSpace(repoSnapshot)
            ? "(no repo content available — review prose only)"
            : repoSnapshot;

        var systemPrompt = $$"""
            You review a draft "Context" field that a developer wrote for a bug or feature memory entry.
            Your goal is to make the Context clearer, more specific, and technically accurate against the actual code provided.

            Inputs you receive:
            - Tags: {{tagList}}
            - Affected services: {{serviceList}}
            - Repo snapshot for those services (file tree + keyword-matched snippets), or "(no repo content available)" if none.
            - Draft Context written by the user.

            Rules:
            - Cross-check technical specifics in the Context (service names, file paths, class names, env vars, endpoints) against the repo snapshot. Flag anything that looks wrong or missing.
            - Fix grammar, articles, verb agreement, and awkward phrasing.
            - Tighten vague phrases ("something broke", "weird issue") into specific language IF the repo snapshot supports the specificity. Do not invent details that the snapshot does not show.
            - If the snapshot is missing or unhelpful, do a prose-only review (grammar + clarity) and say so in the summary.
            - Keep the original meaning. If unsure what the user meant, mention it in suggestions rather than guessing.

            Respond with JSON only, no markdown, matching:
            {
              "summary": "one short sentence describing the overall state of the Context",
              "suggestions": ["bullet 1", "bullet 2", ...],
              "rewrittenContext": "the full Context rewritten with your improvements applied"
            }

            Repo snapshot:
            {{repoBlock}}
            """;

        var request = new ChatRequest(
            _options.ChatModel,
            new[]
            {
                new ChatMessage("system", systemPrompt),
                new ChatMessage("user", string.IsNullOrWhiteSpace(context) ? "(empty)" : context),
            },
            new ResponseFormat("json_object"),
            Temperature: 0.2);

        var response = await _http.PostAsJsonAsync("v1/chat/completions", request, ct);
        await response.EnsureSuccessOrThrowWithBodyAsync("OpenAI", ct);
        var payload = await response.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken: ct)
                      ?? throw new InvalidOperationException("Empty chat response");

        var content = payload.Choices.FirstOrDefault()?.Message.Content
                      ?? throw new InvalidOperationException("No content in chat response");

        var parsed = JsonSerializer.Deserialize<ReviewPayload>(content, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                     ?? throw new InvalidOperationException("Could not parse review JSON");

        return new ContextReview(
            parsed.Summary ?? string.Empty,
            parsed.Suggestions ?? new List<string>(),
            parsed.RewrittenContext ?? string.Empty);
    }

    public async Task<ClarificationAnswer> AnswerClarificationAsync(
        string question,
        string draftContext,
        IReadOnlyList<string> tags,
        IReadOnlyList<string> affectedServices,
        string repoSnapshot,
        CancellationToken ct)
    {
        var tagList = tags.Count == 0 ? "(none)" : string.Join(", ", tags);
        var serviceList = affectedServices.Count == 0 ? "(none)" : string.Join(", ", affectedServices);
        var repoBlock = string.IsNullOrWhiteSpace(repoSnapshot)
            ? "(no repo content available)"
            : repoSnapshot;
        var draftBlock = string.IsNullOrWhiteSpace(draftContext)
            ? "(empty)"
            : draftContext;

        var systemPrompt = $$"""
            You answer a clarification question that an AI reviewer raised about a developer's bug/feature memory entry. Your answer must be grounded in the provided repo snapshot.

            Inputs:
            - Tags: {{tagList}}
            - Affected services: {{serviceList}}
            - User's draft Context (for background only)
            - Repo snapshot (file tree + keyword-matched snippets)

            Rules:
            - Ground every claim in the snapshot. Cite file paths (and line numbers when shown) inside the snapshot as evidence.
            - If the snapshot doesn't contain enough to answer, say so plainly. Do NOT invent file names, class names, or behavior.
            - Be concise. 1-4 sentences for the answer.
            - "evidence" is a short list of file paths (with line numbers when available) that support the answer. Empty list if no evidence.

            Respond with JSON only, no markdown:
            { "answer": "...", "evidence": ["path/to/file.cs:42", ...] }

            User's draft Context:
            {{draftBlock}}

            Repo snapshot:
            {{repoBlock}}
            """;

        var request = new ChatRequest(
            _options.ChatModel,
            new[]
            {
                new ChatMessage("system", systemPrompt),
                new ChatMessage("user", question),
            },
            new ResponseFormat("json_object"),
            Temperature: 0.2);

        var response = await _http.PostAsJsonAsync("v1/chat/completions", request, ct);
        await response.EnsureSuccessOrThrowWithBodyAsync("OpenAI", ct);
        var payload = await response.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken: ct)
                      ?? throw new InvalidOperationException("Empty chat response");

        var content = payload.Choices.FirstOrDefault()?.Message.Content
                      ?? throw new InvalidOperationException("No content in chat response");

        var parsed = JsonSerializer.Deserialize<ClarificationPayload>(content, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                     ?? throw new InvalidOperationException("Could not parse clarification JSON");

        return new ClarificationAnswer(
            parsed.Answer ?? string.Empty,
            parsed.Evidence ?? new List<string>());
    }

    public async Task<string> RewriteContextWithAnswersAsync(
        string originalContext,
        IReadOnlyList<string> tags,
        IReadOnlyList<string> affectedServices,
        IReadOnlyList<ConfirmedClarification> clarifications,
        string repoSnapshot,
        CancellationToken ct)
    {
        var tagList = tags.Count == 0 ? "(none)" : string.Join(", ", tags);
        var serviceList = affectedServices.Count == 0 ? "(none)" : string.Join(", ", affectedServices);
        var repoBlock = string.IsNullOrWhiteSpace(repoSnapshot)
            ? "(no repo content available)"
            : repoSnapshot;
        var qaBlock = clarifications.Count == 0
            ? "(none)"
            : string.Join("\n\n", clarifications.Select((c, i) =>
                $"{i + 1}. Q: {c.Question}\n   A: {c.Answer}"));

        var systemPrompt = $$"""
            You produce a polished final "Context" field for a bug/feature memory entry by combining the user's original Context with their confirmed answers to clarifying questions.

            Inputs:
            - Tags: {{tagList}}
            - Affected services: {{serviceList}}
            - Repo snapshot (for additional grounding, not the source of truth)
            - User's original Context
            - Confirmed clarifications: questions and the answers the user accepted or edited

            Rules:
            - The CONFIRMED ANSWERS are authoritative — incorporate them as facts.
            - Preserve the user's voice and intent. Don't make it longer than it needs to be.
            - Don't repeat the answers verbatim — weave them into a coherent Context paragraph.
            - Fix grammar and tighten phrasing while you're at it.
            - Do NOT introduce new facts beyond what the original Context, the confirmed answers, or the repo snapshot support.
            - Keep it 1-4 short paragraphs.

            Respond with JSON only, no markdown:
            { "context": "the final rewritten Context" }

            Confirmed clarifications:
            {{qaBlock}}

            Repo snapshot:
            {{repoBlock}}
            """;

        var request = new ChatRequest(
            _options.ChatModel,
            new[]
            {
                new ChatMessage("system", systemPrompt),
                new ChatMessage("user", string.IsNullOrWhiteSpace(originalContext) ? "(empty)" : originalContext),
            },
            new ResponseFormat("json_object"),
            Temperature: 0.2);

        var response = await _http.PostAsJsonAsync("v1/chat/completions", request, ct);
        await response.EnsureSuccessOrThrowWithBodyAsync("OpenAI", ct);
        var payload = await response.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken: ct)
                      ?? throw new InvalidOperationException("Empty chat response");

        var content = payload.Choices.FirstOrDefault()?.Message.Content
                      ?? throw new InvalidOperationException("No content in chat response");

        var parsed = JsonSerializer.Deserialize<RewritePayload>(content, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                     ?? throw new InvalidOperationException("Could not parse rewrite JSON");

        return parsed.Context ?? string.Empty;
    }

    public async Task<RagAnswer> AnswerWithContextAsync(
        string question,
        IReadOnlyList<RetrievedContext> context,
        CancellationToken ct)
    {
        var contextBlock = string.Join("\n\n---\n\n",
            context.Select((c, i) => $"[Source {i + 1}] (id: {c.EntryId}, score: {c.Score:F2})\n{c.Content}"));

        var systemPrompt = $$"""
            You answer the user's question using ONLY the bug memory entries provided as sources below. Each source has an id.
            
            Rules:
            - Ground every claim in the sources. If the sources don't answer the question, say so plainly — don't speculate.
            - Reference sources naturally in prose (e.g. "from the Kafka retry bug...") rather than dumping raw ids.
            - Be concise and practical. Lead with the answer or fix, then context.
            - At the end, return a "citedIds" array containing ONLY the ids you actually used.
            
            Sources:
            {{contextBlock}}
            
            Respond with JSON only, no markdown, matching: { "answer": "", "citedIds": [] }
            """;

        var request = new ChatRequest(
            _options.ChatModel,
            new[]
            {
                new ChatMessage("system", systemPrompt),
                new ChatMessage("user", question),
            },
            new ResponseFormat("json_object"),
            Temperature: 0.3);

        var response = await _http.PostAsJsonAsync("v1/chat/completions", request, ct);
        await response.EnsureSuccessOrThrowWithBodyAsync("OpenAI", ct);
        var payload = await response.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken: ct)
                      ?? throw new InvalidOperationException("Empty chat response");

        var content = payload.Choices.FirstOrDefault()?.Message.Content
                      ?? throw new InvalidOperationException("No content in chat response");

        var parsed = JsonSerializer.Deserialize<RagPayload>(content, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                     ?? throw new InvalidOperationException("Could not parse RAG JSON");

        var citedIds = (parsed.CitedIds ?? new List<string>())
            .Select(id => Guid.TryParse(id, out var g) ? g : (Guid?)null)
            .Where(g => g.HasValue)
            .Select(g => g!.Value)
            .ToList();

        return new RagAnswer(parsed.Answer ?? string.Empty, citedIds);
    }

    private sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages,
        [property: JsonPropertyName("response_format")] ResponseFormat ResponseFormat,
        [property: JsonPropertyName("temperature")] double Temperature);

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record ResponseFormat(
        [property: JsonPropertyName("type")] string Type);

    private sealed record ChatResponse(
        [property: JsonPropertyName("choices")] List<ChatChoice> Choices);

    private sealed record ChatChoice(
        [property: JsonPropertyName("message")] ChatMessage Message);

    private sealed class ExtractionPayload
    {
        public string? Title { get; set; }
        public List<string>? Tags { get; set; }
        public string? Context { get; set; }
        public string? RootCause { get; set; }
        public string? Solution { get; set; }
    }

    private sealed class RagPayload
    {
        public string? Answer { get; set; }
        public List<string>? CitedIds { get; set; }
    }

    private sealed class ReviewPayload
    {
        public string? Summary { get; set; }
        public List<string>? Suggestions { get; set; }
        public string? RewrittenContext { get; set; }
    }

    private sealed class ClarificationPayload
    {
        public string? Answer { get; set; }
        public List<string>? Evidence { get; set; }
    }

    private sealed class RewritePayload
    {
        public string? Context { get; set; }
    }
}
