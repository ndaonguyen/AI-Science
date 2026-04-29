using BugMemory.Api.Endpoints;
using BugMemory.Application;
using BugMemory.Application.Abstractions;
using BugMemory.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? new[] { "http://localhost:5173" })
     .AllowAnyHeader()
     .AllowAnyMethod()));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseExceptionHandler(errorApp => errorApp.Run(async ctx =>
{
    var feature = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
    var logger = ctx.RequestServices.GetRequiredService<ILogger<Program>>();
    logger.LogError(feature?.Error, "Unhandled exception");
    ctx.Response.StatusCode = 500;
    await ctx.Response.WriteAsJsonAsync(new { error = "Internal server error", detail = feature?.Error.Message });
}));

// Bootstrap Qdrant collection on startup. Fail-soft: if Qdrant isn't
// running yet, log a warning and let the app start anyway. Note we do NOT
// retry on first use — the next /api/bugs POST or /api/ask call will try
// to talk to Qdrant directly and fail with a clear "Qdrant returned ..."
// error from the HTTP helper. The startup attempt is best-effort init,
// not a circuit breaker. If you see this warning, the most common cause
// is forgetting to run `docker compose up -d` before starting the API.
using (var scope = app.Services.CreateScope())
{
    var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStore>();
    try
    {
        await vectorStore.EnsureCollectionAsync(CancellationToken.None);
    }
    catch (Exception ex)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogWarning(ex,
            "Could not initialize Qdrant collection on startup. " +
            "The API will start anyway, but bug-create and ask requests will " +
            "fail until Qdrant is reachable. Did you run `docker compose up -d`?");
    }
}

app.MapBugMemoryEndpoints();
app.MapSearchEndpoints();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

public partial class Program { }
