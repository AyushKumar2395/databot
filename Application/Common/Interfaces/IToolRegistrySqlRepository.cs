using Application.Common.Models;

namespace Application.Common.Interfaces;

/// <summary>
/// Loads active ToolRegistry candidates from SQLGig database for a given environment.
/// </summary>
public interface IToolRegistrySqlRepository
{
    Task<List<QueryCodeCandidate>> GetActiveToolsByEnvironmentAsync(
        string environment,
        CancellationToken cancellationToken);
}
