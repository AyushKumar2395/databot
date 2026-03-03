using System.Text.Json.Serialization;

namespace Application.Common.Models;

/// <summary>
/// API response contract for tune/template/generate pipeline.
/// </summary>
public sealed class AskApiResponse
{
    [JsonPropertyName("conversationId")]
    public string ConversationId { get; set; } = string.Empty;

    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("environment")]
    public string Environment { get; set; } = string.Empty;

    [JsonPropertyName("rawQuestion")]
    public string RawQuestion { get; set; } = string.Empty;

    [JsonPropertyName("tunedQuestion")]
    public string TunedQuestion { get; set; } = string.Empty;

    [JsonPropertyName("queryCode")]
    public string? QueryCode { get; set; }

    [JsonPropertyName("resolution")]
    public string Resolution { get; set; } = string.Empty;

    [JsonPropertyName("scriptLanguage")]
    public string? ScriptLanguage { get; set; }

    [JsonPropertyName("script")]
    public string? Script { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("tuningModel")]
    public PipelineModelInfo? TuningModel { get; set; }

    [JsonPropertyName("templateSelectionModel")]
    public PipelineModelInfo? TemplateSelectionModel { get; set; }

    [JsonPropertyName("scriptGenerationModel")]
    public PipelineModelInfo? ScriptGenerationModel { get; set; }
}

/// <summary>
/// Model metadata used by each LLM stage so UI can verify model linkage.
/// </summary>
public sealed class PipelineModelInfo
{
    [JsonPropertyName("modelId")]
    public int ModelId { get; set; }

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("modelKey")]
    public string ModelKey { get; set; } = string.Empty;

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;
}
