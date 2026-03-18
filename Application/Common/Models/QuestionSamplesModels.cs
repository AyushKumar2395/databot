using System.Text.Json.Serialization;

namespace Application.Common.Models;

/// <summary>Raw row from [SQLGig].[DataBOT].[QuestionSamples].</summary>
public sealed class QuestionSampleRow
{
    public int Id { get; set; }
    public string Environment { get; set; } = string.Empty;
    public string GroupKey { get; set; } = string.Empty;
    public string GroupTitle { get; set; } = string.Empty;
    public int GroupOrder { get; set; }
    public string QuestionText { get; set; } = string.Empty;
    public int QuestionOrder { get; set; }
    public string? Tags { get; set; }
    public string? Script { get; set; }
}

/// <summary>Single question inside a group.</summary>
public sealed class QuestionSampleItem
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("question")]
    public string Question { get; set; } = string.Empty;

    [JsonPropertyName("tags")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Tags { get; set; }

    [JsonPropertyName("order")]
    public int Order { get; set; }
}

/// <summary>A group of related sample questions.</summary>
public sealed class QuestionSampleGroup
{
    [JsonPropertyName("groupKey")]
    public string GroupKey { get; set; } = string.Empty;

    [JsonPropertyName("groupTitle")]
    public string GroupTitle { get; set; } = string.Empty;

    [JsonPropertyName("order")]
    public int Order { get; set; }

    [JsonPropertyName("items")]
    public List<QuestionSampleItem> Items { get; set; } = [];
}

/// <summary>API response for GET /api/question-samples.</summary>
public sealed class QuestionSamplesResponse
{
    [JsonPropertyName("environment")]
    public string Environment { get; set; } = string.Empty;

    [JsonPropertyName("groups")]
    public List<QuestionSampleGroup> Groups { get; set; } = [];
}

// ── Metrics listing models ──────────────────────────────────────────────

/// <summary>Single metric entry in a group.</summary>
public sealed class MetricItem
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    /// <summary>True for text/status metrics that are not chartable (e.g. AGHealth, SQLService).</summary>
    [JsonPropertyName("isText")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsText { get; set; }
}

/// <summary>A group of related metrics (e.g. "Memory Allocation", "CPU").</summary>
public sealed class MetricGroup
{
    [JsonPropertyName("group")]
    public string Group { get; set; } = string.Empty;

    [JsonPropertyName("metrics")]
    public List<MetricItem> Metrics { get; set; } = [];
}

/// <summary>API response for GET /api/metrics.</summary>
public sealed class MetricsResponse
{
    [JsonPropertyName("environment")]
    public string Environment { get; set; } = string.Empty;

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    [JsonPropertyName("groups")]
    public List<MetricGroup> Groups { get; set; } = [];
}
