using BugMemory.Application.Abstractions;
using BugMemory.Infrastructure.OpenAi;
using BugMemory.Infrastructure.Persistence;
using BugMemory.Infrastructure.Qdrant;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BugMemory.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<OpenAiOptions>(configuration.GetSection("OpenAi"));
        services.Configure<QdrantOptions>(configuration.GetSection("Qdrant"));
        services.Configure<JsonFileBugMemoryRepositoryOptions>(configuration.GetSection("Storage"));

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IBugMemoryRepository, JsonFileBugMemoryRepository>();

        services.AddHttpClient<IEmbeddingService, OpenAiEmbeddingService>();
        services.AddHttpClient<ILlmService, OpenAiLlmService>();
        services.AddHttpClient<IVectorStore, QdrantVectorStore>();

        return services;
    }
}
