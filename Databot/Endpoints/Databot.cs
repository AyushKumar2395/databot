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
        // UI-intention mapping: backend works with explicit environment tags
        var environmentTag = request.Environment.Equals("SqlServer", StringComparison.OrdinalIgnoreCase)
            ? "<SqlServer_Live>"
            : "<Windows_Live>";

        // QueryCode router will be plugged in later (template-first). For now pass empty as UI does when absent.
        var routedQueryCode = string.Empty;

        var tunedLine = await runner.TuningQuestionAsync(
            request.Question,
            environmentTag,
            routedQueryCode,
            request.AgentModel,
            cancellationToken);

        // Preserve UI behavior: if irrelevant, return the mismatch line directly
        if (tunedLine.StartsWith("MISMATCH:", StringComparison.OrdinalIgnoreCase))
            return TypedResults.Ok(tunedLine);

        // tunedLine is: <TUNED_QUESTION>||<QUERYCODE>
        var parts = tunedLine.Split("||", 2, StringSplitOptions.None);
        var tunedTextOnly = parts.Length > 0 ? parts[0].Trim() : tunedLine.Trim();

        var script = await runner.GenerateScriptAsync(
            environmentTag,
            tunedTextOnly,
            request.AgentModel,
            cancellationToken);

        return TypedResults.Ok(script);
    }
}