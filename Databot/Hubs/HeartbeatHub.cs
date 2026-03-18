using System.Runtime.CompilerServices;
using Application.Common.Interfaces;
using Application.Common.Models;
using Databot.Endpoints;
using Microsoft.AspNetCore.SignalR;

namespace Databot.Hubs;

public sealed class HeartbeatHub(IHeartbeatService heartbeatService, ILogger<HeartbeatHub> logger) : Hub
{
    /// <summary>
    /// Streams live heartbeat vitals at a configurable interval (5-60 seconds).
    /// The client sends the request once; the server pushes HeartbeatStreamUpdate
    /// messages until the client disconnects or cancels.
    /// </summary>
    public async IAsyncEnumerable<HeartbeatStreamUpdate> StreamVitals(
        HeartbeatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Validate
        var errors = HeartbeatRequestValidator.Validate(request);
        if (errors.Count > 0)
        {
            yield return HeartbeatStreamUpdate.Error(
                string.Join("; ", errors.SelectMany(e => e.Value)));
            yield break;
        }

        // Clamp interval to 5-60 seconds.
        var intervalMs = Math.Clamp(request.IntervalSeconds, 5, 60) * 1000;

        logger.LogInformation(
            "Heartbeat stream started. ConnectionId={ConnectionId}, Targets={TargetCount}, Interval={IntervalSec}s",
            Context.ConnectionId, request.SelectedTargets.Length, request.IntervalSeconds);

        while (!cancellationToken.IsCancellationRequested)
        {
            // Stream phases incrementally: vitals → alerts → sql_instances → final
            // Collect updates first (can't yield inside try-catch in C#)
            var updates = new List<HeartbeatStreamUpdate>();
            HeartbeatStreamUpdate? errorUpdate = null;
            var shouldBreak = false;

            try
            {
                await foreach (var update in heartbeatService.GetVitalsStreamAsync(request, cancellationToken))
                    updates.Add(update);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                shouldBreak = true;
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning("Heartbeat cycle had a timeout — some targets may be missing.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Heartbeat stream cycle failed.");
                errorUpdate = HeartbeatStreamUpdate.Error($"Heartbeat cycle failed: {ex.Message}");
                shouldBreak = true;
            }

            // Yield collected updates outside try-catch
            foreach (var u in updates)
                yield return u;

            if (errorUpdate is not null)
                yield return errorUpdate;

            if (shouldBreak) break;

            // Wait for the next interval (or until cancelled).
            try
            {
                await Task.Delay(intervalMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Heartbeat stream stopped. ConnectionId={ConnectionId}", Context.ConnectionId);
        yield return HeartbeatStreamUpdate.Stopped();
    }
}
