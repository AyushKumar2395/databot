using Application.Common.Interfaces;
using Application.Common.Models;
using Infrastructure.Repository;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Databot.Endpoints;

public class Databot : EndpointGroupBase
{
    public override void Map(RouteGroupBuilder builder)
    {
        builder.MapPost(AskSqlGig)
            .WithSummary("Tuning Question")
            .WithDescription("Tuning Question from user's question")
            .Produces<string>()
            .ProducesProblem(StatusCodes.Status400BadRequest);
    }
    private async Task<Results<Ok<ChatExecuteResponse>, BadRequest>> AskSqlGig(
        SkPromptRunner runner,
        IQueryCodeRouterService queryCodeRouter,
        AskQuestionRequest request,
        CancellationToken cancellationToken)
    {
        // UI-intention mapping: backend works with explicit environment tags
        var environmentTag = request.Environment.Equals("SqlServer", StringComparison.OrdinalIgnoreCase)
            ? "<SqlServer_Live>"
            : "<Windows_Live>";

        // Step-2: route QueryCode on server-side (rule-based stub; can be replaced with DB/LLM later).
        var routedQueryCode = queryCodeRouter.Route(request.Question, environmentTag) ?? string.Empty;

        var tunedLine = await runner.TuningQuestionAsync(
            request.Question,
            environmentTag,
            routedQueryCode,
            request.AgentModel,
            cancellationToken);

        // tunedLine is either:
        // - MISMATCH: IRRELEVANT_QUESTION||<QUERYCODE>
        // - <TUNED_QUESTION>||<QUERYCODE>
        var parts = tunedLine.Split("||", 2, StringSplitOptions.None);
        var tunedQuestion = parts.Length > 0 ? parts[0].Trim() : tunedLine.Trim();
        var queryCode = parts.Length == 2 ? (parts[1] ?? string.Empty).Trim() : string.Empty;

        var response = new ChatExecuteResponse
        {
            EnvironmentTag = environmentTag,
            RawQuestion = request.Question,
            TunedLine = tunedLine,
            TunedQuestion = tunedQuestion,
            QueryCode = queryCode,
            ScriptSource = "None",
        };

        // Preserve UI behavior: irrelevant question
        if (tunedLine.StartsWith("MISMATCH:", StringComparison.OrdinalIgnoreCase))
        {
            response.Status = "MISMATCH";
            return TypedResults.Ok(response);
        }

        // Step-1 response enhancement: return structured JSON (script included)
        var script = await runner.GenerateScriptAsync(
            environmentTag,
            tunedQuestion,
            request.AgentModel,
            cancellationToken);

        response.Status = "OK";
        response.ScriptSource = "LLM";
        response.Script = script;

        return TypedResults.Ok(response);
    }
}