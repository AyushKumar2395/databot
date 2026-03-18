using Application.Common.Interfaces;
using Application.Common.Models;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Databot.Endpoints;

public sealed class Servers : Infrastructure.EndpointGroupBase
{
    public override string GroupName => "servers";

    public override void Map(RouteGroupBuilder builder)
    {
        builder.MapGet(GetUserServersAsync)
            .WithName("GetUserServers")
            .WithSummary("Get user servers")
            .WithDescription("Returns SQL Server or Windows servers assigned to the given user via Get_UserSQLServer / Get_UserWINServer stored procedures.")
            .Produces<UserServersResponse>()
            .ProducesValidationProblem();
    }

    private static async Task<Results<Ok<UserServersResponse>, ValidationProblem>> GetUserServersAsync(
        [AsParameters] ServerQuery query,
        IUserServerRepository repository,
        ILogger<Servers> logger,
        CancellationToken cancellationToken)
    {
        var errors = Validate(query);
        if (errors.Count > 0)
            return TypedResults.ValidationProblem(errors);

        var env = query.Environment!.Trim();
        var userId = query.UserId!.Trim();

        logger.LogInformation(
            "GET /api/servers — UserId={UserId}, Environment={Environment}",
            userId, env);

        List<UserServerEntry> servers;

        if (env.StartsWith("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            servers = await repository.GetSqlServersAsync(userId, cancellationToken);
        }
        else if (env.StartsWith("Windows", StringComparison.OrdinalIgnoreCase))
        {
            servers = await repository.GetWinServersAsync(userId, cancellationToken);
        }
        else
        {
            servers = [];
        }

        return TypedResults.Ok(new UserServersResponse
        {
            UserId = userId,
            Environment = env,
            Servers = servers
        });
    }

    private static Dictionary<string, string[]> Validate(ServerQuery query)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(query.UserId))
            errors["userId"] = ["userId is required."];

        if (string.IsNullOrWhiteSpace(query.Environment))
            errors["environment"] = ["environment is required."];
        else if (!query.Environment.StartsWith("SqlServer", StringComparison.OrdinalIgnoreCase)
                 && !query.Environment.StartsWith("Windows", StringComparison.OrdinalIgnoreCase))
            errors["environment"] = ["environment must start with SqlServer or Windows."];

        return errors;
    }
}

/// <summary>
/// Query parameters for GET /api/servers?userId=xxx&amp;environment=SqlServer_Live
/// </summary>
public sealed class ServerQuery
{
    public string? UserId { get; set; }
    public string? Environment { get; set; }
}
