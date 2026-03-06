using Application.Common.Models;

namespace Application.Common.Interfaces;

/// <summary>
/// Orchestrates the full ask pipeline:
/// validation-ready request -> tune -> template lookup -> generate -> response mapping.
/// </summary>
public interface IAskPipelineService
{
    Task<AskApiResponse> ExecuteAsync(AskApiRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Executes the full pipeline while emitting SSE progress events via <paramref name="progress"/>.
    /// Emits phase.start/phase.done for TUNING, INTENT, MODEL_SELECT, GENERATE, EXECUTE, ANSWER,
    /// exec.target.start/done/error per selected target, and a terminal "final" event.
    /// </summary>
    Task<AskApiResponse> ExecuteWithProgressAsync(
        AskApiRequest request,
        IProgressStream progress,
        CancellationToken cancellationToken);
}
