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

app.UseHttpsRedirection();

app.UseExceptionHandler(_ => { });

app.MapHub<AskHub>("/hubs/ask");
app.MapEndpoints();

app.Run();
