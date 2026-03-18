using Application.Common.Interfaces;
using Application.Common.Models;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Databot.Endpoints;

public sealed class QuestionSamples : Infrastructure.EndpointGroupBase
{
    public override string GroupName => "question-samples";

    public override void Map(RouteGroupBuilder builder)
    {
        builder.MapGet(HandleAsync)
            .WithName("GetQuestionSamples")
            .WithSummary("Get sample questions for an environment")
            .WithDescription("Returns grouped sample questions from [SQLGig].[DataBOT].[QuestionSamples]. Cached for 10 minutes per environment.")
            .Produces<QuestionSamplesResponse>()
            .ProducesValidationProblem();
    }

    private static async Task<Results<Ok<QuestionSamplesResponse>, ValidationProblem>> HandleAsync(
        string? environment,
        bool? refresh,
        IQuestionSamplesService service,
        ILogger<QuestionSamples> logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(environment) || !IsKnownEnvironment(environment))
        {
            var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["environment"] = ["environment query parameter is required and must be General, SqlServer_Live, or Windows_Live."]
            };
            return TypedResults.ValidationProblem(errors);
        }

        var doRefresh = refresh ?? false;

        logger.LogInformation(
            "GET /api/question-samples?environment={Environment}&refresh={Refresh}",
            environment,
            doRefresh);

        var response = await service.GetAsync(environment, doRefresh, cancellationToken);
        return TypedResults.Ok(response);
    }

    private static bool IsKnownEnvironment(string environment)
    {
        return string.Equals(environment, "General", StringComparison.OrdinalIgnoreCase)
               || environment.StartsWith("SqlServer_", StringComparison.OrdinalIgnoreCase)
               || environment.StartsWith("Windows_", StringComparison.OrdinalIgnoreCase);
    }
}
