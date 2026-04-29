using BugMemory.Api.Contracts;
using BugMemory.Application.UseCases;

namespace BugMemory.Api.Endpoints;

public static class BugMemoryEndpoints
{
    public static void MapBugMemoryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/bugs").WithTags("Bug Memory");

        group.MapGet("", async (ListBugMemoriesUseCase useCase, CancellationToken ct) =>
            Results.Ok(await useCase.ExecuteAsync(ct)));

        group.MapGet("{id:guid}", async (Guid id, GetBugMemoryUseCase useCase, CancellationToken ct) =>
        {
            var entry = await useCase.ExecuteAsync(id, ct);
            return entry is null ? Results.NotFound() : Results.Ok(entry);
        });

        group.MapPost("", async (CreateBugMemoryRequest request, CreateBugMemoryUseCase useCase, CancellationToken ct) =>
        {
            try
            {
                var dto = await useCase.ExecuteAsync(
                    new CreateBugMemoryCommand(request.Title, request.Tags ?? new(), request.Context, request.RootCause, request.Solution),
                    ct);
                return Results.Created($"/api/bugs/{dto.Id}", dto);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPut("{id:guid}", async (Guid id, UpdateBugMemoryRequest request, UpdateBugMemoryUseCase useCase, CancellationToken ct) =>
        {
            try
            {
                var dto = await useCase.ExecuteAsync(
                    new UpdateBugMemoryCommand(id, request.Title, request.Tags ?? new(), request.Context, request.RootCause, request.Solution),
                    ct);
                return dto is null ? Results.NotFound() : Results.Ok(dto);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapDelete("{id:guid}", async (Guid id, DeleteBugMemoryUseCase useCase, CancellationToken ct) =>
        {
            var deleted = await useCase.ExecuteAsync(id, ct);
            return deleted ? Results.NoContent() : Results.NotFound();
        });
    }

    public static void MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").WithTags("Search & RAG");

        group.MapPost("search", async (SearchRequest request, SearchBugMemoriesUseCase useCase, CancellationToken ct) =>
        {
            var results = await useCase.ExecuteAsync(new SearchBugMemoriesQuery(request.Query, request.TopK ?? 5), ct);
            return Results.Ok(results);
        });

        group.MapPost("ask", async (AskRequest request, AskBugMemoryUseCase useCase, CancellationToken ct) =>
        {
            var response = await useCase.ExecuteAsync(new AskBugMemoryQuery(request.Question, request.TopK ?? 5), ct);
            return Results.Ok(response);
        });

        group.MapPost("extract", async (ExtractRequest request, ExtractBugFieldsUseCase useCase, CancellationToken ct) =>
        {
            try
            {
                var result = await useCase.ExecuteAsync(new ExtractBugFieldsCommand(request.SourceText), ct);
                return Results.Ok(result);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}
