using Application.Common.Models;

namespace Application.Common.Interfaces;

public interface IHeartbeatService
{
    /// <summary>
    /// Executes lightweight vital-sign scripts on all selected targets in parallel
    /// and returns per-server health cards with threshold-aware classification.
    /// For Windows servers, also probes SQL instances found via the hierarchy view.
    /// </summary>
    Task<HeartbeatResponse> GetVitalsAsync(HeartbeatRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Streams heartbeat phases incrementally: vitals first (fast), then alerts, then SQL instances.
    /// Each yield is a partial update — the UI merges it into the existing state.
    /// </summary>
    IAsyncEnumerable<HeartbeatStreamUpdate> GetVitalsStreamAsync(
        HeartbeatRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Records the latest vitals response into the per-server ring buffer (last 10 snapshots).
    /// Called automatically after each SignalR pulse or REST heartbeat call.
    /// </summary>
    void RecordSnapshots(HeartbeatResponse response);

    /// <summary>
    /// Retrieves recent snapshots for a specific server from the ring buffer.
    /// </summary>
    List<HeartbeatSnapshot> GetSnapshots(string server);

    /// <summary>
    /// Analyzes the last N heartbeat snapshots for a specific server using LLM.
    /// Returns a structured analysis with trends, verdict, and recommendations.
    /// </summary>
    Task<HeartbeatAnalysisResponse> AnalyzeServerAsync(
        HeartbeatAnalysisRequest request, CancellationToken cancellationToken);
}
