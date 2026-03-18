namespace Application.Common.Models;

/// <summary>
/// Deterministic routing decision produced at the start of the pipeline.
/// </summary>
public sealed class ExecutionRoute
{
    /// <summary>"SAMPLE_ONLY" | "TEMPLATE_OR_LLM" | "LLM_ONLY" | "ANSWER_ONLY"</summary>
    public string RouteKind { get; init; } = string.Empty;

    /// <summary>"SAMPLE" | "TEMPLATE" | "LLM" | null</summary>
    public string? ScriptSource { get; init; }

    /// <summary>Non-null only for SAMPLE_ONLY route.</summary>
    public int? SampleId { get; init; }

    /// <summary>Non-null only for SAMPLE_ONLY route.</summary>
    public string? GroupKey { get; init; }

    /// <summary>Mirrors RouteKind for the response plan.generatorMode field.</summary>
    public string GeneratorMode { get; init; } = string.Empty;
}
