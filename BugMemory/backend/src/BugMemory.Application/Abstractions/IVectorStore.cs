namespace BugMemory.Application.Abstractions;

public sealed record VectorSearchHit(Guid EntryId, float Score);

public interface IVectorStore
{
    Task EnsureCollectionAsync(CancellationToken ct);
    Task UpsertAsync(Guid id, float[] embedding, IReadOnlyDictionary<string, object> payload, CancellationToken ct);
    Task<IReadOnlyList<VectorSearchHit>> SearchAsync(float[] queryEmbedding, int topK, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
}
