using System.Reflection;
using Domain.Constants;
using Domain.Entities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Data;

public static class ApplicationDbContextInitializerExtensions
{
    public static async Task InitializeSeedAsync(this WebApplication app)
    {
        var service = app.Services.CreateScope();
        var initializer = service.ServiceProvider.GetRequiredService<SeedingService>();

        await initializer.InitializeMigrationAsync(app.Environment.IsDevelopment());
        await initializer.SeedAsync();
    }
}

internal sealed class SeedingService(
    ApplicationDbContext context,
    UserManager<User> userManager,
    RoleManager<ApplicationRole> roleManager,
    ILogger<SeedingService> logger)
{
    private readonly ILogger<SeedingService> _logger = logger;
    private readonly ApplicationDbContext _context = context;
    private readonly RoleManager<ApplicationRole> _roleManager = roleManager;
    private readonly UserManager<User> _userManager = userManager;

    public async Task InitializeMigrationAsync(bool isDevelopment)
    {
        try
        {
            switch (isDevelopment)
            {
                case true when _context.Database.IsNpgsql():
                    await _context.Database.EnsureDeletedAsync();
                    await _context.Database.EnsureCreatedAsync();
                    break;
                case false when _context.Database.IsNpgsql():
                    await _context.Database.MigrateAsync();
                    break;
            }

            await SeedAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, ex.Message);
            throw;
        }
    }

    public async Task SeedAsync()
    {
        try
        {
            await SeedRolesAsync();
            await SeedUserAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, ex.Message);
            throw;
        }
    }

    async Task SeedRolesAsync()
    {
        await SeedingRoleAsync(_roleManager, Roles.Administrator);
        await SeedingRoleAsync(_roleManager, Roles.User);
    }

    async Task SeedUserAsync()
    {
        try
        {
            var user = new User("Admin user", "administrator@localhost");

            if (_userManager.Users.All(u => u.UserName != user.UserName))
            {
                await _userManager.CreateAsync(user, "Administrator1!");
                await _userManager.AddToRoleAsync(user, Roles.Administrator);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, ex.Message);
            throw;
        }
    }

    async Task SeedingRoleAsync(RoleManager<ApplicationRole> roleManager, string roleName)
    {
        try
        {
            if (!await _roleManager.RoleExistsAsync(roleName))
            {
                await _roleManager.CreateAsync(new ApplicationRole(roleName));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, ex.Message);
            throw;
        }
    }
}