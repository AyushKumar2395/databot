using Application.Common.Interfaces;
using Application.Common.Models;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Repository;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Infrastructure;

public static class DependencyInjection
{
    public static void AddInfrastructureServices(this IHostApplicationBuilder builder)
    {
        var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Keeps app bootable in local dev when no external SQL Server is configured.
            connectionString =
                "Server=(localdb)\\MSSQLLocalDB;Database=Databot;Trusted_Connection=True;TrustServerCertificate=True;";
        }

        builder.Services.AddDbContext<ApplicationDbContext>((_, options) =>
        {
            options.UseSqlServer(connectionString);
            options.ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
        });

        var mcpConfig = builder.Configuration.GetSection("LLM");

        builder.Services.Configure<LlmOptions>(mcpConfig);

        builder.Services.AddIdentityApiEndpoints<User>()
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>();

        builder.Services.AddScoped<SeedingService>();

        builder.Services.AddScoped<IAskPipelineService, AskPipelineService>();
        builder.Services.AddSingleton<IModelSelector, HardcodedModelSelector>();
        builder.Services.AddSingleton<IToolRegistryResolver, StubToolRegistryResolver>();
        builder.Services.AddSingleton<ILLMClient, GeminiClient>();
        builder.Services.AddSingleton<ILLMClient, OpenAiClient>();

        builder.Services.AddSingleton<KernelFactory>();
        builder.Services.AddSingleton<SkPromptRunner>();
        builder.Services.AddSingleton<IQueryCodeRouterService, QueryCodeRouterService>();
    }
}
