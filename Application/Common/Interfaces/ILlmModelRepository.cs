using Application.Common.Models;

namespace Application.Common.Interfaces;

/// <summary>
/// Loads LLM model definitions from persistent storage.
/// </summary>
public interface ILlmModelRepository
{
    Task<IReadOnlyList<LlmModelDefinition>> GetAllEnabledAsync(CancellationToken cancellationToken = default);
}
