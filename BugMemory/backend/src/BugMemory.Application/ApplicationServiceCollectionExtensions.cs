using BugMemory.Application.UseCases;
using Microsoft.Extensions.DependencyInjection;

namespace BugMemory.Application;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<CreateBugMemoryUseCase>();
        services.AddScoped<UpdateBugMemoryUseCase>();
        services.AddScoped<DeleteBugMemoryUseCase>();
        services.AddScoped<ListBugMemoriesUseCase>();
        services.AddScoped<GetBugMemoryUseCase>();
        services.AddScoped<SearchBugMemoriesUseCase>();
        services.AddScoped<AskBugMemoryUseCase>();
        services.AddScoped<ExtractBugFieldsUseCase>();
        return services;
    }
}
