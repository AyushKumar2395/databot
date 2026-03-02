using Application.Common.Models;
using Infrastructure.Repository;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Databot.Endpoints;

public class Databot : EndpointGroupBase
{
    public override void Map(RouteGroupBuilder builder)
    {
        builder.MapPost(TuningQuestion)
            .WithSummary("Tuning Question")
            .WithDescription("Tuning Question from user's question")
            .Produces<string>()
            .ProducesProblem(StatusCodes.Status400BadRequest);
    }

    async Task<Results<Ok<string>, BadRequest>> TuningQuestion(SkPromptRunner runner, AskQuestionRequest request,
        CancellationToken cancellationToken)
    {
        var servers = string.Join(',', request.Servers);
        var tunedQuestion = await runner.TuningQuestionAsync(request.Question, request.Environment,
            servers, request.AgentModel, cancellationToken);

        var script = await runner.GenerateScriptAsync(tunedQuestion, servers, request.AgentModel, cancellationToken);
        return TypedResults.Ok(script);
    }
}