using Application.Common.Interfaces;
using Application.Common.Models;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Databot.Endpoints;

/// <summary>
/// REST endpoint for a single heartbeat snapshot (non-streaming).
/// For live streaming, use the SignalR hub at /hubs/heartbeat.
/// </summary>
public sealed class Heartbeat : Infrastructure.EndpointGroupBase
{
    public override string GroupName => "heartbeat";

    public override void Map(RouteGroupBuilder builder)
    {
        builder.MapPost(HandleAsync)
            .WithName("GetHeartbeat")
            .WithSummary("Get a single heartbeat snapshot")
            .WithDescription(
                "Executes lightweight vital-sign scripts on all selected targets and returns " +
                "per-server health cards with threshold-aware classification. " +
                "For continuous monitoring, use the SignalR hub at /hubs/heartbeat.")
            .Produces<HeartbeatResponse>()
            .ProducesValidationProblem();

        builder.MapPost("/analyze", AnalyzeAsync)
            .WithName("AnalyzeHeartbeat")
            .WithSummary("AI-powered analysis of recent heartbeat snapshots")
            .WithDescription(
                "Gathers the last 10 heartbeat snapshots for a specific server from the in-memory ring buffer, " +
                "sends them to an LLM for trend analysis, and returns a structured health report with " +
                "verdict, sections, metric trends, and actionable recommendations.")
            .Produces<HeartbeatAnalysisResponse>()
            .ProducesValidationProblem();
    }

    private static async Task<Results<Ok<HeartbeatResponse>, ValidationProblem>> HandleAsync(
        HeartbeatRequest request,
        IHeartbeatService heartbeatService,
        ILogger<Heartbeat> logger,
        CancellationToken cancellationToken)
    {
        var errors = HeartbeatRequestValidator.Validate(request);
        if (errors.Count > 0)
            return TypedResults.ValidationProblem(errors);

        logger.LogInformation(
            "POST /api/heartbeat — Environment={Environment}, Targets={TargetCount}, Interval={Interval}s",
            request.Environment, request.SelectedTargets.Length, request.IntervalSeconds);

        var response = await heartbeatService.GetVitalsAsync(request, cancellationToken);
        return TypedResults.Ok(response);
    }

    private static async Task<Results<Ok<HeartbeatAnalysisResponse>, ValidationProblem>> AnalyzeAsync(
        HeartbeatAnalysisRequest request,
        IHeartbeatService heartbeatService,
        ILogger<Heartbeat> logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Server))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["server"] = ["Server name is required."]
            });
        }

        logger.LogInformation(
            "POST /api/heartbeat/analyze — Server={Server}, Environment={Environment}",
            request.Server, request.Environment);

        var response = await heartbeatService.AnalyzeServerAsync(request, cancellationToken);
        return TypedResults.Ok(response);
    }
}
