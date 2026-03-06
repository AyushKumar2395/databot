using System.Runtime.CompilerServices;
using Application.Common.Interfaces;
using Application.Common.Models;
using Databot.Endpoints;
using Microsoft.AspNetCore.SignalR;

namespace Databot.Hubs;

public sealed class AskHub(IAskPipelineService pipeline, ILogger<AskHub> logger) : Hub
{
    private const int ChunkSize = 256;

    public async IAsyncEnumerable<AskStreamUpdate> StreamAsk(
        AskApiRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var validationErrors = AskRequestValidator.ValidateAsk(request);
        if (validationErrors.Count > 0)
        {
            yield return AskStreamUpdate.ValidationFailed(validationErrors);
            yield break;
        }

        logger.LogInformation(
            "Received SignalR ask stream. ConnectionId={ConnectionId}, ConversationId={ConversationId}, BearerToken={BearerToken}, Environment={Environment}",
            Context.ConnectionId,
            request.ConversationId,
            request.BearerToken,
            request.Environment);

        yield return AskStreamUpdate.Status("started", "Ask pipeline started.");

        AskApiResponse? response = null;
        AskStreamUpdate? failureUpdate = null;
        try
        {
            response = await pipeline.ExecuteAsync(request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            yield break;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "SignalR ask stream failed. ConnectionId={ConnectionId}, ConversationId={ConversationId}",
                Context.ConnectionId,
                request.ConversationId);

            failureUpdate = AskStreamUpdate.Error("PIPELINE_FAILED", "Failed to process ask request.");
        }

        if (failureUpdate is not null)
        {
            yield return failureUpdate;
            yield break;
        }

        yield return AskStreamUpdate.Status("streaming", "Streaming response content.");
        foreach (var chunk in ChunkText(GetStreamContent(response!), ChunkSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return AskStreamUpdate.Chunk(chunk);
            await Task.Yield();
        }

        yield return AskStreamUpdate.Completed(response!);
    }

    private static IEnumerable<string> ChunkText(string text, int chunkSize)
    {
        if (string.IsNullOrWhiteSpace(text) || chunkSize <= 0)
            yield break;

        for (var offset = 0; offset < text.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, text.Length - offset);
            yield return text.Substring(offset, length);
        }
    }

    private static string GetStreamContent(AskApiResponse response)
    {
        if (!string.IsNullOrWhiteSpace(response.Result.AnswerText))
            return response.Result.AnswerText;

        if (!string.IsNullOrWhiteSpace(response.Script?.Final))
            return response.Script!.Final;

        return response.Tuning.StopReason ?? string.Empty;
    }
}
