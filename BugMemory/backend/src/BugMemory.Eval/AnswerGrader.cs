using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BugMemory.Eval;

/// <summary>
/// LLM-as-judge grader for the answer text. Given the case's question,
/// the case's plain-prose criteria, and the actual answer the system
/// produced, asks a separate LLM call to decide pass/fail with a short
/// rationale.
///
/// We deliberately use a STRONGER model for grading than for answering
/// (gpt-4o regardless of which model the answerer used). The grader's
/// job is to be a reliable judge — its cost is proportional to the size
/// of the case set, not to the production traffic, so the price is
/// affordable even with the bigger model.
///
/// We don't reuse the existing OpenAiLlmService.AnswerWithContextAsync
/// here because the grader needs a different prompt shape and different
/// JSON output. Sharing the HttpClient setup pattern from there.
/// </summary>
public sealed class AnswerGrader
{
    private readonly HttpClient _http;
    private const string GraderModel = "gpt-4o";

    public AnswerGrader(string apiKey)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri("https://api.openai.com/"),
        };
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public async Task<AnswerGrade> GradeAsync(
        string question,
        string criteria,
        string actualAnswer,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(criteria))
        {
            // No criteria = nothing to grade against. Vacuously pass.
            // Same reasoning as the retrieval grader: don't penalise
            // case authors who haven't written criteria yet.
            return new AnswerGrade(
                Passed: true,
                Rationale: "(no criteria specified — pass by default)");
        }

        var systemPrompt = """
            You are grading the output of a personal RAG-powered bug
            knowledge base. The user asked a question, the system
            retrieved past bug entries and produced an answer. Your job:
            decide whether the answer meets the case's stated criteria.

            Be strict but fair. If the criteria say 'names X as the fix
            and acknowledges Y as the root cause', the answer must do
            both — partial credit is a fail. Do not invent criteria
            that aren't stated. If the answer hallucinates content not
            grounded in the question OR adds material the criteria
            don't ask for in a way that distracts from the actual
            answer, fail it.

            Respond with JSON only: { "passed": bool, "rationale": "<one sentence>" }.
            """;

        var userPrompt = $$"""
            Question:
            {{question}}

            Criteria for a good answer:
            {{criteria}}

            Actual answer:
            {{actualAnswer}}
            """;

        var request = new
        {
            model = GraderModel,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt },
            },
            response_format = new { type = "json_object" },
            temperature = 0.0,
        };

        var response = await _http.PostAsJsonAsync("v1/chat/completions", request, ct);
        if (!response.IsSuccessStatusCode)
        {
            // Read the body so we know WHY the grader call failed (rate
            // limit? bad key?) instead of just the status code. Same
            // pattern as Infrastructure/HttpResponseExtensions but
            // inlined here so the harness has no dependency on a PR
            // that may not be merged yet.
            string body;
            try { body = await response.Content.ReadAsStringAsync(ct); }
            catch { body = "(could not read body)"; }
            throw new HttpRequestException(
                $"OpenAI (grader) returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}",
                inner: null,
                statusCode: response.StatusCode);
        }

        var payload = await response.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken: ct)
                      ?? throw new InvalidOperationException("Empty grader response");

        var content = payload.Choices.FirstOrDefault()?.Message.Content
                      ?? throw new InvalidOperationException("No content in grader response");

        try
        {
            var parsed = JsonSerializer.Deserialize<GradePayload>(
                content, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (parsed is null)
                return new AnswerGrade(false, "(grader returned unparseable JSON)");
            return new AnswerGrade(parsed.Passed, parsed.Rationale ?? "");
        }
        catch (JsonException ex)
        {
            return new AnswerGrade(false, $"(grader JSON parse error: {ex.Message})");
        }
    }

    private sealed record ChatResponse(
        [property: JsonPropertyName("choices")] List<ChatChoice> Choices);
    private sealed record ChatChoice(
        [property: JsonPropertyName("message")] ChatMessage Message);
    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed class GradePayload
    {
        public bool Passed { get; set; }
        public string? Rationale { get; set; }
    }
}

public sealed record AnswerGrade(bool Passed, string Rationale);
