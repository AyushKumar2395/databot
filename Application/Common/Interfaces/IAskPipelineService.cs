using Application.Common.Models;

namespace Application.Common.Interfaces;

/// <summary>
/// Orchestrates the full ask pipeline:
/// validation-ready request -> tune -> template lookup -> generate -> response mapping.
/// </summary>
public interface IAskPipelineService
{
    Task<AskApiResponse> ExecuteAsync(AskApiRequest request, CancellationToken cancellationToken);
}
