using System.Text.Json.Serialization;

namespace Application.Common.Models;

/// <summary>
/// Unified response for tune+generate (and later execute+explain).
/// </summary>
public sealed class ChatExecuteResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "OK"; // OK | MISMATCH | ERROR

    [JsonPropertyName("environmentTag")]
    public string EnvironmentTag { get; set; } = "<SqlServer_Live>";

    [JsonPropertyName("rawQuestion")]
    public string RawQuestion { get; set; } = string.Empty;

    /// <summary>Full tuning output: "<tuned>||<queryCode>" or "MISMATCH: ...||<queryCode>"</summary>
    [JsonPropertyName("tunedLine")]
    public string TunedLine { get; set; } = string.Empty;

    [JsonPropertyName("tunedQuestion")]
    public string TunedQuestion { get; set; } = string.Empty;

    [JsonPropertyName("queryCode")]
    public string QueryCode { get; set; } = string.Empty;

    /// <summary>Template | LLM | None</summary>
    [JsonPropertyName("scriptSource")]
    public string ScriptSource { get; set; } = "LLM";

    [JsonPropertyName("script")]
    public string Script { get; set; } = string.Empty;

    /// <summary>Per-target execution outputs (empty if Execute=false)</summary>
    [JsonPropertyName("executionResult")]
    public List<ExecutionResultItem> ExecutionResult { get; set; } = new();

    /// <summary>Final natural language answer (empty if Execute=false)</summary>
    [JsonPropertyName("answer")]
    public string Answer { get; set; } = string.Empty;

    /// <summary>Any non-fatal warnings or fatal errors.</summary>
    [JsonPropertyName("errors")]
    public List<ApiError> Errors { get; set; } = new();
}

public sealed class ExecutionResultItem
{
    [JsonPropertyName("server")]
    public string Server { get; set; } = string.Empty;

    /// <summary>Success | Error | Timeout | Skipped</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "Success";

    /// <summary>
    /// Rows returned by SQL or PowerShell (normalized to row objects).
    /// Keep it flexible for now.
    /// </summary>
    [JsonPropertyName("rows")]
    public List<Dictionary<string, object?>> Rows { get; set; } = new();

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("durationMs")]
    public long? DurationMs { get; set; }
}

public sealed class ApiError
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty; // e.g. "POLICY_BLOCKED", "DB_TEMPLATE_NOT_FOUND", "EXEC_FAILED"

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("details")]
    public string? Details { get; set; }
}