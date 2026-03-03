using Application.Common.Models;

namespace Application.Common.Interfaces;

/// <summary>
/// Resolves prebuilt scripts/templates from ToolRegistry before fallback LLM generation.
/// </summary>
public interface IToolRegistryResolver
{
    Task<ToolResolutionResult> ResolveBestToolAsync(
        string environment,
        string tunedQuestion,
        CancellationToken cancellationToken);
}
