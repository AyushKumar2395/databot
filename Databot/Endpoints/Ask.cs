using Application.Common.Interfaces;
using Application.Common.Models;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Databot.Endpoints;

public sealed class Ask : EndpointGroupBase
{
    public override string GroupName => "ask";

    public override void Map(RouteGroupBuilder builder)
    {
        builder.MapPost(HandleAsync)
            .WithSummary("Ask Databot")
            .WithDescription("Runs tune -> template lookup -> generate pipeline.")
            .Produces<AskApiResponse>()
            .ProducesValidationProblem();
    }

    private static async Task<Results<Ok<AskApiResponse>, ValidationProblem>> HandleAsync(
        AskApiRequest request,
        IAskPipelineService pipeline,
        ILogger<Ask> logger,
        CancellationToken cancellationToken)
    {
        var validationErrors = Validate(request);
        if (validationErrors.Count > 0)
            return TypedResults.ValidationProblem(validationErrors);

        logger.LogInformation(
            "Received /api/ask request. ConversationId={ConversationId}, UserId={UserId}, Environment={Environment}",
            request.ConversationId,
            request.UserId,
            request.Environment);

        var response = await pipeline.ExecuteAsync(request, cancellationToken);
        return TypedResults.Ok(response);
    }

    private static Dictionary<string, string[]> Validate(AskApiRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(request.Environment))
            errors["environment"] = ["Environment is required."];

        if (!IsKnownEnvironment(request.Environment))
            errors["environment"] = ["Environment must be General, SqlServer_*, or Windows_*."];

        request.SelectedServers ??= [];
        if (RequiresSelectedServers(request.Environment) && request.SelectedServers.Length == 0)
        {
            errors["selectedServers"] =
            [
                "selectedServers must contain at least one value when environment starts with SqlServer_ or Windows_."
            ];
        }

        return errors;
    }

    private static bool RequiresSelectedServers(string environment)
    {
        return environment.StartsWith("SqlServer_", StringComparison.OrdinalIgnoreCase)
               || environment.StartsWith("Windows_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownEnvironment(string environment)
    {
        return string.Equals(environment, "General", StringComparison.OrdinalIgnoreCase)
               || environment.StartsWith("SqlServer_", StringComparison.OrdinalIgnoreCase)
               || environment.StartsWith("Windows_", StringComparison.OrdinalIgnoreCase);
    }
}
