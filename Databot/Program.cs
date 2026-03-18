using Databot.Hubs;
using Infrastructure.Data;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.AddApplicationServices();
builder.AddInfrastructureServices();
builder.AddWebServices();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

var enableDatabaseInitialization =
    builder.Configuration.GetValue("Database:EnableInitialization", false);

if (enableDatabaseInitialization)
{
    try
    {
        await app.InitializeSeedAsync();
    }
    catch (Exception ex)
    {
        // Keep API runnable even if external DB is unavailable in local dev.
        app.Logger.LogWarning(ex, "Database initialization failed. Continuing in API-only mode.");
    }
}
else
{
    app.Logger.LogInformation("Database initialization is disabled (Database:EnableInitialization=false).");
}

app.UseCors(); // CORS must be FIRST — before HTTPS redirect, before exception handler

// Only redirect to HTTPS in production — in dev, UI connects via HTTP
if (!app.Environment.IsDevelopment())
    app.UseHttpsRedirection();

app.UseExceptionHandler(appBuilder =>
{
    appBuilder.Run(async context =>
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        var exception = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
        logger.LogError(exception, "Unhandled exception on {Method} {Path}", context.Request.Method, context.Request.Path);
        context.Response.StatusCode = 500;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new
        {
            error = "Internal server error",
            message = exception?.Message ?? "An unexpected error occurred."
        }));
    });
});

app.MapHub<AskHub>("/hubs/ask");
app.MapHub<HeartbeatHub>("/hubs/heartbeat");
app.MapEndpoints();

app.Run();
