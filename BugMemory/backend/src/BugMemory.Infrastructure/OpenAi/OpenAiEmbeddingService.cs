using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using BugMemory.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace BugMemory.Infrastructure.OpenAi;

public sealed class OpenAiOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string EmbeddingModel { get; set; } = "text-embedding-3-small";
    public string ChatModel { get; set; } = "gpt-4o";
    public int EmbeddingDimensions { get; set; } = 1536;
}

public sealed class OpenAiEmbeddingService : IEmbeddingService
{
    private readonly HttpClient _http;
    private readonly OpenAiOptions _options;

    public OpenAiEmbeddingService(HttpClient http, IOptions<OpenAiOptions> options)
    {
        _http = http;
        _options = options.Value;
        _http.BaseAddress ??= new Uri("https://api.openai.com/");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        var batch = await EmbedBatchAsync(new[] { text }, ct);
        return batch[0];
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        var request = new EmbeddingRequest(_options.EmbeddingModel, texts);
        var response = await _http.PostAsJsonAsync("v1/embeddings", request, ct);
        await response.EnsureSuccessOrThrowWithBodyAsync("OpenAI", ct);
        var payload = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(cancellationToken: ct)
                      ?? throw new InvalidOperationException("Empty embedding response");

        return payload.Data
            .OrderBy(d => d.Index)
            .Select(d => d.Embedding)
            .ToList();
    }

    private sealed record EmbeddingRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] IReadOnlyList<string> Input);

    private sealed record EmbeddingResponse(
        [property: JsonPropertyName("data")] List<EmbeddingDatum> Data);

    private sealed record EmbeddingDatum(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("embedding")] float[] Embedding);
}
