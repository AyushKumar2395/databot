namespace Application.Common.Models;

/// <summary>
/// Result of template lookup against ToolRegistry.
/// </summary>
public sealed class ToolResolutionResult
{
    public bool Found { get; init; }
    public string? ToolCode { get; init; }
    public string? ScriptLanguage { get; init; }
    public string? Script { get; init; }
    public double? Score { get; init; }
}
