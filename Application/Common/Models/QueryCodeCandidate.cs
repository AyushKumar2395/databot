namespace Application.Common.Models;

/// <summary>
/// Candidate tool metadata used for deterministic ranked selection.
/// </summary>
public sealed class QueryCodeCandidate
{
    public int ToolId { get; init; }
    public string QueryCode { get; init; } = string.Empty;
    public string ToolName { get; init; } = string.Empty;
    public string Environment { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Keywords { get; init; } = string.Empty;
    public string ToolTags { get; init; } = string.Empty;
    public int Priority { get; init; }
    public bool IsReadOnly { get; init; }
    public string ScriptLanguage { get; init; } = string.Empty;
    public string ScriptTemplate { get; init; } = string.Empty;
    public string? ParameterSchema { get; init; }
    public string? OutputSchema { get; init; }
    public double Score { get; init; }
    public int KeywordHitCount { get; init; }
    public int NameHitCount { get; init; }
    public int DescriptionHitCount { get; init; }
    public int TagOverlapCount { get; init; }
    public int CompatibilityPenalty { get; init; }
}
