using Application.Common.Interfaces;
using Application.Common.Models;
using Infrastructure.Services;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Databot.Endpoints;

public sealed class Ask : EndpointGroupBase
{
    public override string GroupName => "ask";

    public override void Map(RouteGroupBuilder builder)
    {
        builder.MapPost(HandleAsync)
            .WithSummary("Ask Databot")
            .WithDescription("Runs tune -> generate -> execute (with autofix + consolidation).")
            .Produces<AskApiResponse>()
            .ProducesValidationProblem();

        builder.MapPost(HandleStreamAsync, "stream")
            .WithSummary("Ask Databot (SSE stream)")
            .WithDescription("Streams pipeline phase events as Server-Sent Events. Terminal 'final' event contains the full AskApiResponse.")
            .Produces(200, contentType: "text/event-stream")
            .ProducesValidationProblem();
    }

    private static async Task<Results<Ok<AskApiResponse>, ValidationProblem>> HandleAsync(
        AskApiRequest request,
        IAskPipelineService pipeline,
        ILogger<Ask> logger,
        CancellationToken cancellationToken)
    {
        var validationErrors = AskRequestValidator.ValidateAsk(request);
        if (validationErrors.Count > 0)
            return TypedResults.ValidationProblem(validationErrors);

        logger.LogInformation(
            "Received /api/ask request. ConversationId={ConversationId}, BearerToken={BearerToken}, Environment={Environment}",
            request.ConversationId,
            request.BearerToken,
            request.Environment);

        var response = await pipeline.ExecuteAsync(request, cancellationToken);
        return TypedResults.Ok(response);
    }

    private static async Task HandleStreamAsync(
        AskApiRequest request,
        IAskPipelineService pipeline,
        HttpResponse httpResponse,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger<Ask>();
        var validationErrors = AskRequestValidator.ValidateAsk(request);
        if (validationErrors.Count > 0)
        {
            httpResponse.StatusCode = 400;
            await httpResponse.WriteAsJsonAsync(validationErrors, cancellationToken);
            return;
        }

        logger.LogInformation(
            "Received /api/ask/stream request. ConversationId={ConversationId}, BearerToken={BearerToken}, Environment={Environment}",
            request.ConversationId,
            request.BearerToken,
            request.Environment);

        // SSE headers — must be set before first write.
        httpResponse.Headers.ContentType = "text/event-stream";
        httpResponse.Headers.CacheControl = "no-cache";
        httpResponse.Headers.Append("X-Accel-Buffering", "no");
        httpResponse.Headers.Append("Connection", "keep-alive");

        // Disable response buffering so Kestrel flushes each event immediately.
        var bodyFeature = httpResponse.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>();
        bodyFeature?.DisableBuffering();

        var sseLogger = loggerFactory.CreateLogger<SseProgressStream>();
        var progress = new SseProgressStream(
            httpResponse,
            request.ConversationId ?? string.Empty,
            request.BearerToken ?? string.Empty,
            sseLogger);

        try
        {
            await pipeline.ExecuteWithProgressAsync(request, progress, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — silent exit.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SSE pipeline failed. ConversationId={ConversationId}", request.ConversationId);
            try
            {
                await progress.EmitAsync(ProgressEvent.Error($"Pipeline failed: {ex.Message}"), cancellationToken);
            }
            catch
            {
                // Best effort — client may already be gone.
            }
        }
    }
}
