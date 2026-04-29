using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using BugMemory.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BugMemory.Infrastructure.Qdrant;

public sealed class QdrantOptions
{
    public string BaseUrl { get; set; } = "http://localhost:6333";
    public string CollectionName { get; set; } = "bug_memories";
    public int VectorSize { get; set; } = 1536;
    public string Distance { get; set; } = "Cosine";
}

public sealed class QdrantVectorStore : IVectorStore
{
    private readonly HttpClient _http;
    private readonly QdrantOptions _options;
    private readonly ILogger<QdrantVectorStore> _logger;

    public QdrantVectorStore(HttpClient http, IOptions<QdrantOptions> options, ILogger<QdrantVectorStore> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
        _http.BaseAddress ??= new Uri(_options.BaseUrl);
    }

    public async Task EnsureCollectionAsync(CancellationToken ct)
    {
        var existsResponse = await _http.GetAsync($"collections/{_options.CollectionName}", ct);
        if (existsResponse.IsSuccessStatusCode)
        {
            _logger.LogInformation("Qdrant collection {Name} already exists", _options.CollectionName);
            return;
        }
        if (existsResponse.StatusCode != HttpStatusCode.NotFound)
        {
            existsResponse.EnsureSuccessStatusCode();
        }

        var createPayload = new
        {
            vectors = new { size = _options.VectorSize, distance = _options.Distance },
        };
        var create = await _http.PutAsJsonAsync($"collections/{_options.CollectionName}", createPayload, ct);
        create.EnsureSuccessStatusCode();
        _logger.LogInformation("Created Qdrant collection {Name}", _options.CollectionName);
    }

    public async Task UpsertAsync(Guid id, float[] embedding, IReadOnlyDictionary<string, object> payload, CancellationToken ct)
    {
        var body = new
        {
            points = new[]
            {
                new
                {
                    id = id.ToString(),
                    vector = embedding,
                    payload,
                },
            },
        };
        var response = await _http.PutAsJsonAsync($"collections/{_options.CollectionName}/points?wait=true", body, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<VectorSearchHit>> SearchAsync(float[] queryEmbedding, int topK, CancellationToken ct)
    {
        var body = new
        {
            vector = queryEmbedding,
            limit = topK,
            with_payload = true,
        };
        var response = await _http.PostAsJsonAsync($"collections/{_options.CollectionName}/points/search", body, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<SearchResponse>(cancellationToken: ct);
        if (result?.Result is null) return Array.Empty<VectorSearchHit>();

        var hits = new List<VectorSearchHit>(result.Result.Count);
        foreach (var item in result.Result)
        {
            if (Guid.TryParse(item.Id, out var guid))
            {
                hits.Add(new VectorSearchHit(guid, item.Score));
            }
        }
        return hits;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var body = new { points = new[] { id.ToString() } };
        var response = await _http.PostAsJsonAsync($"collections/{_options.CollectionName}/points/delete?wait=true", body, ct);
        response.EnsureSuccessStatusCode();
    }

    private sealed record SearchResponse(
        [property: JsonPropertyName("result")] List<SearchPoint> Result);

    private sealed record SearchPoint(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("score")] float Score);
}
