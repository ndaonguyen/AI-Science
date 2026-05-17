using BugMemory.Application.Abstractions;
using BugMemory.Infrastructure.CodeScan;
using BugMemory.Infrastructure.GitHub;
using BugMemory.Infrastructure.Jira;
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
        services.Configure<ServiceReposOptions>(configuration.GetSection("ServiceRepos"));
        services.Configure<JiraOptions>(configuration.GetSection("Jira"));
        services.Configure<GitHubOptions>(configuration.GetSection("GitHub"));

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IBugMemoryRepository, JsonFileBugMemoryRepository>();

        services.AddHttpClient<IEmbeddingService, OpenAiEmbeddingService>();
        services.AddHttpClient<ILlmService, OpenAiLlmService>();
        services.AddHttpClient<IVectorStore, QdrantVectorStore>();

        // External search providers. Each gets its own typed HttpClient
        // so the per-provider auth headers and BaseAddress stay isolated.
        //
        // Registration shape: we register the CONCRETE types via
        // AddHttpClient<TImpl> (gives each its own HttpClient lifecycle),
        // then forward both to IExternalSearchProvider with explicit
        // AddTransient lambdas. Why not AddHttpClient<TInterface, TImpl>?
        // That overload would have the second call REPLACE the first
        // because DI registrations against the same service type are
        // last-wins by default. We want BOTH providers resolvable via
        // GetServices<IExternalSearchProvider>() so the Ask use case can
        // iterate them.
        //
        // If a provider's config is missing/incomplete, IsConfigured
        // returns false and the use case skips it — registration is
        // unconditional regardless of whether the user has set the PAT.
        services.AddHttpClient<JiraSearchProvider>();
        services.AddHttpClient<GitHubSearchProvider>();
        services.AddTransient<IExternalSearchProvider>(sp =>
            sp.GetRequiredService<JiraSearchProvider>());
        services.AddTransient<IExternalSearchProvider>(sp =>
            sp.GetRequiredService<GitHubSearchProvider>());

        services.AddMemoryCache();
        services.AddSingleton<LocalRepoCodeScanner>();
        services.AddSingleton<IRepoCodeScanner, CachingRepoCodeScanner>();

        return services;
    }
}
