namespace Application.Common.Models;

/// <summary>
/// Input contract for ToolRegistry template binding and rendering.
/// </summary>
public sealed class TemplateRenderRequest
{
    public string? QueryCode { get; set; }
    public string Environment { get; set; } = string.Empty;
    public string ScriptLanguage { get; set; } = string.Empty;
    public string ScriptTemplate { get; set; } = string.Empty;
    public string? ParameterSchemaJson { get; set; }
    public string RawQuestion { get; set; } = string.Empty;
    public string TunedQuestion { get; set; } = string.Empty;
}

/// <summary>
/// Input contract for rendering with already-bound parameters (after LLM_VALIDATE patching).
/// </summary>
public sealed class TemplateRenderBoundRequest
{
    public string Environment { get; set; } = string.Empty;
    public string ScriptLanguage { get; set; } = string.Empty;
    public string ScriptTemplate { get; set; } = string.Empty;
    public ToolParameterSchema Schema { get; set; } = new();
    public Dictionary<string, object?> BoundParameters { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Output contract for deterministic template rendering.
/// </summary>
public sealed class TemplateRenderResult
{
    public bool Success { get; set; }
    public string? ErrorCode { get; set; }
    public string? RenderedScript { get; set; }
    public Dictionary<string, object?> BoundParameters { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public List<string> BlockedKeywords { get; set; } = [];
    public ToolParameterSchema? Schema { get; set; }
}
