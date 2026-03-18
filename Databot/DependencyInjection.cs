namespace Databot;

public static class DependencyInjection
{
    public static void AddWebServices(this IHostApplicationBuilder builder)
    {
        builder.Services.AddOpenApi(o => o.AddDocumentTransformer<ApiDocumentationTransformer>());

        builder.Services.AddSignalR()
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            });

        // CORS: allow UI origins for both REST and SignalR WebSocket connections
        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                if (builder.Environment.IsDevelopment())
                {
                    // Dev: allow any origin (localhost:3000, :4200, etc.)
                    policy.SetIsOriginAllowed(_ => true)
                          .AllowAnyHeader()
                          .AllowAnyMethod()
                          .AllowCredentials();
                }
                else
                {
                    // Production: restrict to known UI origins
                    policy.WithOrigins(
                            builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                            ?? ["https://databot.ctsglobalconnect.com"])
                          .AllowAnyHeader()
                          .AllowAnyMethod()
                          .AllowCredentials();
                }
            });
        });

        builder.Services.AddProblemDetails();
    }
}
