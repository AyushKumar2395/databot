using Application.Common.Models;
using Infrastructure.Services;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Databot.Endpoints;

public sealed class Metrics : Infrastructure.EndpointGroupBase
{
    public override string GroupName => "metrics";

    public override void Map(RouteGroupBuilder builder)
    {
        builder.MapGet(HandleAsync)
            .WithName("GetMetrics")
            .WithSummary("Get available metrics for a History environment")
            .WithDescription("Returns grouped metric definitions from the metric whitelist for SqlServer_History or Windows_History.")
            .Produces<MetricsResponse>()
            .ProducesValidationProblem();
    }

    private static Task<Results<Ok<MetricsResponse>, ValidationProblem>> HandleAsync(
        string? environment,
        ILogger<Metrics> logger)
    {
        // GetMetricMapForEnvironment returns non-null only for History environments
        var map = AskPipelineService.GetMetricMapForEnvironment(environment ?? string.Empty);
        if (map is null)
        {
            var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["environment"] = ["environment must be SqlServer_History or Windows_History."]
            };
            return Task.FromResult<Results<Ok<MetricsResponse>, ValidationProblem>>(
                TypedResults.ValidationProblem(errors));
        }

        logger.LogInformation("GET /api/metrics?environment={Environment}", environment);
        if (map.Count == 0)
        {
            return Task.FromResult<Results<Ok<MetricsResponse>, ValidationProblem>>(
                TypedResults.Ok(new MetricsResponse { Environment = environment! }));
        }

        var groups = map.Values
            .GroupBy(m => m.MetricGroup, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new MetricGroup
            {
                Group = g.Key,
                Metrics = g.OrderBy(m => m.MetricLabel, StringComparer.OrdinalIgnoreCase)
                    .Select(m => new MetricItem
                    {
                        Key = m.MetricKey,
                        Label = m.MetricLabel,
                        IsText = m.MetricSelectSql == "NULL AS MetricValue"
                    })
                    .ToList()
            })
            .ToList();

        var response = new MetricsResponse
        {
            Environment = environment!,
            TotalCount = map.Count,
            Groups = groups
        };

        return Task.FromResult<Results<Ok<MetricsResponse>, ValidationProblem>>(
            TypedResults.Ok(response));
    }
}
