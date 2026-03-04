namespace Application.Common.Models;

/// <summary>
/// Result of template lookup against ToolRegistry.
/// </summary>
public sealed class ToolResolutionResult
{
    public bool Found { get; set; }
    public int? ToolId { get; set; }
    public string? QueryCode { get; set; }
    public string? ToolName { get; set; }
    public string? Environment { get; set; }
    public string? Description { get; set; }
    public string? Keywords { get; set; }
    public string? ToolTags { get; set; }
    public int? Priority { get; set; }
    public bool? IsReadOnly { get; set; }
    public string? ScriptLanguage { get; set; }
    public string? ScriptTemplate { get; set; }
    public string? ParameterSchema { get; set; }
    public string? OutputSchema { get; set; }
    public double? Score { get; set; }
    public double? Confidence { get; set; }
    public string? ScoreBreakdown { get; set; }
    public bool IsLowConfidence { get; set; }
    public string SelectionMethod { get; set; } = "NONE";
    public List<QueryCodeCandidate> Candidates { get; set; } = [];
}
