using Application.Common.Models;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Repository;
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

        builder.Services.AddDbContext<ApplicationDbContext>((_, options) =>
        {
            options.UseNpgsql(connectionString);
            options.ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
        });

        var mcpConfig = builder.Configuration.GetSection("LLM") ??
                        throw new InvalidOperationException("Missing LLM configuration");

        builder.Services.Configure<LlmOptions>(mcpConfig);

        builder.Services.AddIdentityApiEndpoints<User>()
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>();

        builder.Services.AddScoped<SeedingService>();

        builder.Services.AddSingleton<KernelFactory>();
        builder.Services.AddSingleton<SkPromptRunner>();
    }
}