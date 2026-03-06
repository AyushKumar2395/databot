namespace Application.Common.Models;

/// <summary>
/// In-memory model metadata mirroring [SQLGig].[DataBOT].[LLMModels].
/// </summary>
public sealed class LlmModelDefinition
{
    public int ModelId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string ModelKey { get; init; } = string.Empty;
    public string Provider { get; init; } = string.Empty;
    public int SortOrder { get; init; }
    public bool IsDefault { get; init; }
    public bool IsEnabled { get; init; }
    public string? ApiKeyEncrypted { get; init; }
    public string? ApiKeyHint { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public string? UpdatedBy { get; init; }
    public bool UseForTune { get; init; }
    public bool UseForTemplateFind { get; init; }
    public bool UseForValidate { get; init; }
    public bool UseForGenerate { get; init; }
    public bool UseForRepair { get; init; }
    public bool UseForExplain { get; init; }
    public int? RetryCount { get; init; }

  public int? Generator { get; init; }
}