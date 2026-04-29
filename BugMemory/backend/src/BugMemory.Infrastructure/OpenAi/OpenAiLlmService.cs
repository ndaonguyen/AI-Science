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
}
