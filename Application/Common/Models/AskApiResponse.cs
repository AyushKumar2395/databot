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

    [JsonPropertyName("selectedServers")]
    public string[] SelectedServers { get; set; } = [];

    [JsonPropertyName("rawQuestion")]
    public string RawQuestion { get; set; } = string.Empty;

    [JsonPropertyName("tunedQuestion")]
    public string TunedQuestion { get; set; } = string.Empty;

    [JsonPropertyName("queryCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? QueryCode { get; set; }

    [JsonPropertyName("selectionMethod")]
    public string? SelectionMethod { get; set; }

    [JsonPropertyName("score")]
    public double? Score { get; set; }

    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }

    [JsonPropertyName("resolution")]
    public string Resolution { get; set; } = string.Empty;

    [JsonPropertyName("scriptLanguage")]
    public string? ScriptLanguage { get; set; }

    [JsonPropertyName("script")]
    public string? Script { get; set; }

    [JsonPropertyName("scriptTemplate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ScriptTemplate { get; set; }

    [JsonPropertyName("renderedScript")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RenderedScript { get; set; }

    [JsonPropertyName("boundParameters")]
    public Dictionary<string, object?> BoundParameters { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("tuningModel")]
    public PipelineModelInfo? TuningModel { get; set; }

    [JsonPropertyName("templateSelectionModel")]
    public PipelineModelInfo? TemplateSelectionModel { get; set; }

    [JsonPropertyName("scriptGenerationModel")]
    public PipelineModelInfo? ScriptGenerationModel { get; set; }

    [JsonPropertyName("executionPayload")]
    public AskExecutionPayload? ExecutionPayload { get; set; }

    [JsonPropertyName("templateMatch")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TemplateMatchInfo? TemplateMatch { get; set; }

    [JsonPropertyName("template")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TemplateResponseInfo? Template { get; set; }

    [JsonPropertyName("llm")]
    public LlmResponseInfo Llm { get; set; } = new();

    [JsonPropertyName("result")]
    public AskResultInfo Result { get; set; } = new();
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

/// <summary>
/// Ready-to-dispatch payload for downstream execution service.
/// ScriptTemplate is populated from ToolRegistry template or LLM-generated script.
/// </summary>
public sealed class AskExecutionPayload
{
    [JsonPropertyName("executionMode")]
    public string ExecutionMode { get; set; } = string.Empty; // TEMPLATE | LLM_GENERATE

    [JsonPropertyName("dispatchMethod")]
    public string DispatchMethod { get; set; } = string.Empty; // DB_RANK | DB_RANK_PLUS_LLM | LLM_GENERATE

    [JsonPropertyName("environment")]
    public string Environment { get; set; } = string.Empty;

    [JsonPropertyName("selectedServers")]
    public string[] SelectedServers { get; set; } = [];

    [JsonPropertyName("queryCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? QueryCode { get; set; }

    [JsonPropertyName("toolId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ToolId { get; set; }

    [JsonPropertyName("toolName")]
    public string? ToolName { get; set; }

    [JsonPropertyName("scriptLanguage")]
    public string? ScriptLanguage { get; set; }

    [JsonPropertyName("scriptTemplate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ScriptTemplate { get; set; }

    [JsonPropertyName("renderedScript")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RenderedScript { get; set; }

    [JsonPropertyName("boundParameters")]
    public Dictionary<string, object?> BoundParameters { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("parameterSchema")]
    public string? ParameterSchema { get; set; }

    [JsonPropertyName("outputSchema")]
    public string? OutputSchema { get; set; }

    [JsonPropertyName("templateSelectionModelKey")]
    public string? TemplateSelectionModelKey { get; set; }

    [JsonPropertyName("scriptGenerationModelKey")]
    public string? ScriptGenerationModelKey { get; set; }

    [JsonPropertyName("templateSelectionModelProvider")]
    public string? TemplateSelectionModelProvider { get; set; }

    [JsonPropertyName("scriptGenerationModelProvider")]
    public string? ScriptGenerationModelProvider { get; set; }
}

/// <summary>
/// Template resolution metadata from deterministic ToolRegistry selection.
/// </summary>
public sealed class TemplateMatchInfo
{
    [JsonPropertyName("found")]
    public bool Found { get; set; }

    [JsonPropertyName("toolId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ToolId { get; set; }

    [JsonPropertyName("queryCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? QueryCode { get; set; }

    [JsonPropertyName("score")]
    public double? Score { get; set; }

    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }

    [JsonPropertyName("selectionMethod")]
    public string? SelectionMethod { get; set; }
}

public sealed class TemplateResponseInfo
{
    [JsonPropertyName("toolId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ToolId { get; set; }

    [JsonPropertyName("toolName")]
    public string? ToolName { get; set; }

    [JsonPropertyName("scriptLanguage")]
    public string? ScriptLanguage { get; set; }

    [JsonPropertyName("scriptTemplate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ScriptTemplate { get; set; }

    [JsonPropertyName("renderedScript")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RenderedScript { get; set; }

    [JsonPropertyName("boundParameters")]
    public Dictionary<string, object?> BoundParameters { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("templateFailReason")]
    public string? TemplateFailReason { get; set; }
}

public sealed class LlmResponseInfo
{
    [JsonPropertyName("generatedScript")]
    public string? GeneratedScript { get; set; }

    [JsonPropertyName("modelKey")]
    public string? ModelKey { get; set; }

    [JsonPropertyName("provider")]
    public string? Provider { get; set; }
}

public sealed class AskResultInfo
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty; // ANSWER_ONLY | EXECUTION

    [JsonPropertyName("answerText")]
    public string? AnswerText { get; set; }

    [JsonPropertyName("execution")]
    public AskExecutionResultStub? Execution { get; set; } = new();
}

public sealed class AskExecutionResultStub
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "NOT_EXECUTED";
}
