using System.Text.Json.Serialization;

namespace Application.Common.Models;

/// <summary>
/// Single SSE event payload. Serialized to JSON and written as the "data:" line.
/// </summary>
public sealed class ProgressEvent
{
    /// <summary>
    /// One of: phase.start | phase.done |
    ///         exec.target.start | exec.target.done | exec.target.error |
    ///         info | warning | error | final
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    /// <summary>ISO-8601 UTC timestamp.</summary>
    [JsonPropertyName("tsUtc")]
    public string TsUtc { get; init; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    /// <summary>Pipeline phase: TUNING | INTENT | MODEL_SELECT | GENERATE | EXECUTE | ANSWER</summary>
    [JsonPropertyName("phase")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Phase { get; init; }

    /// <summary>Target server name — present on exec.target.* events only.</summary>
    [JsonPropertyName("target")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Target { get; init; }

    /// <summary>Human-readable message.</summary>
    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }

    /// <summary>Structured detail payload (row count, model key, etc.).</summary>
    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Data { get; init; }

    /// <summary>Full pipeline result — present only on the terminal "final" event.</summary>
    [JsonPropertyName("response")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AskApiResponse? Response { get; init; }

    // ── Factory helpers ──────────────────────────────────────────────────────

    public static ProgressEvent PhaseStart(string phase, string? message = null) =>
        new() { Type = "phase.start", Phase = phase, Message = message };

    public static ProgressEvent PhaseDone(string phase, string? message = null) =>
        new() { Type = "phase.done", Phase = phase, Message = message };

    public static ProgressEvent TargetStart(string target) =>
        new() { Type = "exec.target.start", Phase = "EXECUTE", Target = target };

    public static ProgressEvent TargetDone(string target, int rowCount) =>
        new() { Type = "exec.target.done", Phase = "EXECUTE", Target = target, Data = new { rowCount } };

    public static ProgressEvent TargetError(string target, string errorMessage) =>
        new() { Type = "exec.target.error", Phase = "EXECUTE", Target = target, Message = errorMessage };

    public static ProgressEvent Info(string message, string? phase = null, object? data = null) =>
        new() { Type = "info", Phase = phase, Message = message, Data = data };

    public static ProgressEvent Error(string message) =>
        new() { Type = "error", Message = message };

    public static ProgressEvent Final(AskApiResponse response) =>
        new() { Type = "final", Response = response };
}
