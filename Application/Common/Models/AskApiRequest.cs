using System.Text.Json.Serialization;

namespace Application.Common.Models;

/// <summary>
/// UI payload contract for /api/ask.
/// </summary>
public sealed class AskApiRequest
{
    [JsonPropertyName("conversationId")]
    public string ConversationId { get; set; } = string.Empty;

    [JsonPropertyName("BearerToken")]
    public string BearerToken { get; set; } = string.Empty;

    [JsonPropertyName("environment")]
    public string Environment { get; set; } = string.Empty;

    [JsonPropertyName("question")]
    public string Question { get; set; } = string.Empty;

    [JsonPropertyName("selectedTargets")]
    public string[] SelectedTargets { get; set; } = [];

    /// <summary>Legacy key — clients that still send "selectedServers" are silently remapped to SelectedTargets.</summary>
    [JsonPropertyName("selectedServers")]
    public string[]? SelectedServers { get; set; }

    /// <summary>When set, the pipeline uses the pre-authored script from QuestionSamples table instead of LLM generation.</summary>
    [JsonPropertyName("sampleId")]
    public int? SampleId { get; set; }

    /// <summary>Required when SampleId is set. Identifies the question group in QuestionSamples.</summary>
    [JsonPropertyName("groupKey")]
    public string? GroupKey { get; set; }

    // ── History-mode fields (SqlServer_History / Windows_History) ──────

    /// <summary>Pre-normalized server hostname filter (e.g. "CTS02"). Used as @Server parameter in History queries.</summary>
    [JsonPropertyName("serverFilter")]
    public string? ServerFilter { get; set; }

    /// <summary>ISO 8601 UTC start time for the History query window.</summary>
    [JsonPropertyName("fromUtc")]
    public string? FromUtc { get; set; }

    /// <summary>ISO 8601 UTC end time for the History query window.</summary>
    [JsonPropertyName("toUtc")]
    public string? ToUtc { get; set; }

    /// <summary>Source of the selection: "SAMPLE" when user clicked a sample question, "USER" otherwise.</summary>
    [JsonPropertyName("selectionSource")]
    public string? SelectionSource { get; set; }

    /// <summary>Comparison mode for drift detection: "DriftDetection" triggers fleet drift analysis pipeline.</summary>
    [JsonPropertyName("comparisonMode")]
    public string? ComparisonMode { get; set; }

    // ── Metrics-tab fields (SqlServer_History / Windows_History) ────────

    /// <summary>Whitelist key identifying the metric column (e.g. "AvailableMemory_GB"). Validated against strict allowlist per environment.</summary>
    [JsonPropertyName("metricKey")]
    public string? MetricKey { get; set; }

    /// <summary>Display label of the metric (e.g. "Available Memory"). Passed through to response/chart context.</summary>
    [JsonPropertyName("metricLabel")]
    public string? MetricLabel { get; set; }

    /// <summary>Display group of the metric (e.g. "Memory Allocation"). Passed through to response/chart context.</summary>
    [JsonPropertyName("metricGroup")]
    public string? MetricGroup { get; set; }
}
