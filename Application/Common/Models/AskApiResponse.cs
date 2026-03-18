using System.Text.Json.Serialization;

namespace Application.Common.Models;

/// <summary>DataBot /api/ask unified response.</summary>
public sealed class AskApiResponse
{
    [JsonPropertyName("meta")]
    public AskResponseMeta Meta { get; set; } = new();

    [JsonPropertyName("request")]
    public AskResponseRequest Request { get; set; } = new();

    [JsonPropertyName("tuning")]
    public AskResponseTuning Tuning { get; set; } = new();

    [JsonPropertyName("plan")]
    public AskResponsePlan Plan { get; set; } = new();

    /// <summary>Null when the pipeline stopped before a script could be produced.</summary>
    [JsonPropertyName("script")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AskResponseScript? Script { get; set; }

    [JsonPropertyName("result")]
    public AskResponseResult Result { get; set; } = new();

    /// <summary>LLM-generated explanation of the execution result. Null when pipeline stopped before execution.</summary>
    [JsonPropertyName("answer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AskAnswerNode? Answer { get; set; }

    /// <summary>Per-attempt repair log. Only present when at least one retry/repair occurred.</summary>
    [JsonPropertyName("retryAttempts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AskRetryAttempt>? RetryAttempts { get; set; }

    /// <summary>Chart plan for History environments. Null for non-History routes or when charts are not applicable.</summary>
    [JsonPropertyName("chartDetails")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AskChartDetails? ChartDetails { get; set; }

    /// <summary>Lightweight profile of the result data for UI chart rendering decisions.</summary>
    [JsonPropertyName("dataProfile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AskDataProfile? DataProfile { get; set; }

    /// <summary>Concise chart interpretation metadata for UI footer/summary.</summary>
    [JsonPropertyName("chartSummary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AskChartSummary? ChartSummary { get; set; }

    /// <summary>Which LLM model was used for each pipeline stage.</summary>
    [JsonPropertyName("models")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AskPipelineModels? Models { get; set; }

    /// <summary>Full pipeline process flow from tuning to result. Always present.</summary>
    [JsonPropertyName("process")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AskPipelineProcess? Process { get; set; }

    /// <summary>Fleet drift detection report. Only present when comparisonMode=DriftDetection.</summary>
    [JsonPropertyName("driftReport")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AskDriftReport? DriftReport { get; set; }

    /// <summary>Visual data for multi-server Live results. Present for SQL Server Health / Windows Health sample executions with 2+ servers.</summary>
    [JsonPropertyName("liveVisuals")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AskLiveVisuals? LiveVisuals { get; set; }

    /// <summary>Root cause diagnostic report. Present when diagnostic evidence data (Category+MetricName+SeverityHint) is detected.</summary>
    [JsonPropertyName("diagnosticReport")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AskDiagnosticReport? DiagnosticReport { get; set; }

    /// <summary>Data quality and provenance flags. Always present — tells UI whether data is real, mock, or degraded.</summary>
    [JsonPropertyName("dataQuality")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AskDataQuality? DataQuality { get; set; }
}

/// <summary>Signals whether the response contains real LLM/execution data or mock/fallback data.</summary>
public sealed class AskDataQuality
{
    /// <summary>Overall data source: "real" | "mock" | "degraded" | "partial"</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = "real";

    /// <summary>True if any LLM call fell back to mock/deterministic output.</summary>
    [JsonPropertyName("usedMockLlm")]
    public bool UsedMockLlm { get; set; }

    /// <summary>True if any execution target failed or timed out.</summary>
    [JsonPropertyName("hasExecutionFailures")]
    public bool HasExecutionFailures { get; set; }

    /// <summary>True if any data collector (Get-Counter, sp_readerrorlog, etc.) failed.</summary>
    [JsonPropertyName("hasCollectorFailures")]
    public bool HasCollectorFailures { get; set; }

    /// <summary>Specific warnings about data quality. Empty if all data is trustworthy.</summary>
    [JsonPropertyName("warnings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Warnings { get; set; }
}

/// <summary>LLM model assignments per pipeline stage.</summary>
public sealed class AskPipelineModels
{
    [JsonPropertyName("UseForTune")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UseForTune { get; set; }

    [JsonPropertyName("UseForTemplateFind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UseForTemplateFind { get; set; }

    [JsonPropertyName("UseForGenerate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UseForGenerate { get; set; }

    [JsonPropertyName("UseForRepair")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UseForRepair { get; set; }

    [JsonPropertyName("UseForExplain")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UseForExplain { get; set; }
}

public sealed class AskResponseMeta
{
    [JsonPropertyName("conversationId")]
    public string ConversationId { get; set; } = string.Empty;

    [JsonPropertyName("BearerToken")]
    public string BearerToken { get; set; } = string.Empty;

    [JsonPropertyName("timestampUtc")]
    public string TimestampUtc { get; set; } = string.Empty;
}

public sealed class AskResponseRequest
{
    [JsonPropertyName("environment")]
    public string Environment { get; set; } = string.Empty;

    [JsonPropertyName("question")]
    public string Question { get; set; } = string.Empty;

    [JsonPropertyName("selectedTargets")]
    public string[] SelectedTargets { get; set; } = [];

    /// <summary>Derived from environment: "SqlServer" | "Windows" | null for General.</summary>
    [JsonPropertyName("targetType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetType { get; set; }

    // ── History-mode echo fields ──────────────────────────────────────

    /// <summary>History mode: normalized server filter list used in the IN (...) clause.</summary>
    [JsonPropertyName("selectedServers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? SelectedServers { get; set; }

    /// <summary>History mode: the fromUtc value from the request.</summary>
    [JsonPropertyName("fromUtc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FromUtc { get; set; }

    /// <summary>History mode: the toUtc value from the request.</summary>
    [JsonPropertyName("toUtc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToUtc { get; set; }

    // ── Metrics-tab echo fields ──────────────────────────────────────

    [JsonPropertyName("metricKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MetricKey { get; set; }

    [JsonPropertyName("metricLabel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MetricLabel { get; set; }

    [JsonPropertyName("metricGroup")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MetricGroup { get; set; }
}

public sealed class AskResponseTuning
{
    [JsonPropertyName("tunedQuestion")]
    public string TunedQuestion { get; set; } = string.Empty;

    /// <summary>"OK" | "STOPPED"</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AskModelRef? Model { get; set; }

    /// <summary>Populated only when Status == "STOPPED".</summary>
    [JsonPropertyName("stopReason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StopReason { get; set; }
}

public sealed class AskResponsePlan
{
    /// <summary>"LLM_ONLY" | "SAMPLE_ONLY" | "GENERAL" | "STOPPED"</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = string.Empty;

    /// <summary>"LLM_ONLY" | "TEMPLATE_OR_LLM" | "SAMPLE_ONLY" | "ANSWER_ONLY" — reflects the Generator flag / route used.</summary>
    [JsonPropertyName("generatorMode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GeneratorMode { get; set; }

    /// <summary>QuestionSamples SampleId when the SAMPLE_ONLY route was used.</summary>
    [JsonPropertyName("sampleId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SampleId { get; set; }

    /// <summary>QuestionSamples GroupKey when the SAMPLE_ONLY route was used.</summary>
    [JsonPropertyName("groupKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GroupKey { get; set; }

    /// <summary>True when a ToolRegistry template was found and rendered successfully.</summary>
    [JsonPropertyName("templateHit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool TemplateHit { get; set; }

    [JsonPropertyName("queryCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? QueryCode { get; set; }

    [JsonPropertyName("tool")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Tool { get; set; }

    /// <summary>"SQL" | "PS" | null (General/Stopped)</summary>
    [JsonPropertyName("scriptLanguage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ScriptLanguage { get; set; }

    [JsonPropertyName("model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AskModelRef? Model { get; set; }

    /// <summary>Deterministic topic classification result (e.g. "AgentJobs", "BlockingChains", "DiskDrives").</summary>
    [JsonPropertyName("topic")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Topic { get; set; }

    /// <summary>Topic classifier score. Higher = more confident classification.</summary>
    [JsonPropertyName("topicScore")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int TopicScore { get; set; }

    /// <summary>Whether the topic output contract check passed before execution.</summary>
    [JsonPropertyName("contractPassed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ContractPassed { get; set; }

    /// <summary>Contract violation message when contractPassed is false.</summary>
    [JsonPropertyName("contractViolation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContractViolation { get; set; }

    /// <summary>Top-5 candidate topic scores from the classifier for debug/tracing.</summary>
    [JsonPropertyName("topicCandidates")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AskTopicCandidate>? TopicCandidates { get; set; }
}

public sealed class AskTopicCandidate
{
    [JsonPropertyName("topic")]
    public string Topic { get; set; } = string.Empty;

    [JsonPropertyName("score")]
    public int Score { get; set; }
}

public sealed class AskModelRef
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("modelKey")]
    public string ModelKey { get; set; } = string.Empty;
}

public sealed class AskResponseScript
{
    [JsonPropertyName("final")]
    public string Final { get; set; } = string.Empty;

    [JsonPropertyName("validation")]
    public AskScriptValidation Validation { get; set; } = new();

    /// <summary>"TEMPLATE" when rendered from ToolRegistry; "LLM" when generated by the model.</summary>
    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; set; }

    /// <summary>Bound parameter values used during template rendering. Null for LLM-generated scripts.</summary>
    [JsonPropertyName("parameters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object?>? Parameters { get; set; }
}

public sealed class AskScriptValidation
{
    [JsonPropertyName("isSafeReadOnly")]
    public bool IsSafeReadOnly { get; set; } = true;

    [JsonPropertyName("blockedTokenFound")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BlockedTokenFound { get; set; }
}

public sealed class AskResponseResult
{
    /// <summary>"EXECUTION" | "ANSWER_ONLY"</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    /// <summary>"SUCCESS" | "PARTIAL_SUCCESS" | "FAILED" | "NOT_EXECUTED" | "STOPPED"</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("answerText")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AnswerText { get; set; }

    /// <summary>
    /// Ordered list of per-target execution results.
    /// Successful targets include null-stripped result rows.
    /// Failed targets include a single error row.
    /// </summary>
    [JsonPropertyName("items")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AskExecutionItem>? Items { get; set; }

    [JsonPropertyName("summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AskExecutionSummary? Summary { get; set; }
}

public sealed class AskExecutionItem
{
    [JsonPropertyName("target")]
    public string Target { get; set; } = string.Empty;

    /// <summary>"SUCCESS" | "FAILED"</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("rowCount")]
    public int RowCount { get; set; }

    [JsonPropertyName("rows")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Dictionary<string, object?>>? Rows { get; set; }
}

public sealed class AskExecutionSummary
{
    [JsonPropertyName("successCount")]
    public int SuccessCount { get; set; }

    [JsonPropertyName("failCount")]
    public int FailCount { get; set; }

    [JsonPropertyName("totalRowCount")]
    public int TotalRowCount { get; set; }

    [JsonPropertyName("durationMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long DurationMs { get; set; }
}

/// <summary>Tracks one execution/repair attempt for the retryAttempts JSON node.</summary>
public sealed class AskRetryAttempt
{
    [JsonPropertyName("attempt")]
    public int Attempt { get; set; }

    [JsonPropertyName("target")]
    public string Target { get; set; } = string.Empty;

    /// <summary>"EXECUTE" | "REPAIR"</summary>
    [JsonPropertyName("phase")]
    public string Phase { get; set; } = string.Empty;

    [JsonPropertyName("scriptHash")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ScriptHash { get; set; }

    [JsonPropertyName("scriptPreview")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ScriptPreview { get; set; }

    /// <summary>"SUCCESS" | "FAILED" | "BLOCKED"</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    /// <summary>"SYNTAX" | "CONNECTION" | null</summary>
    [JsonPropertyName("errorType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorType { get; set; }

    [JsonPropertyName("errorMessage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("repairedByLlm")]
    public bool RepairedByLlm { get; set; }

    [JsonPropertyName("tsUtc")]
    public string TsUtc { get; set; } = string.Empty;
}

/// <summary>LLM-generated explanation produced after script execution.</summary>
public sealed class AskAnswerNode
{
    /// <summary>"OK" | "PARTIAL" | "FAILED"</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    /// <summary>"CRITICAL" | "WARNING" | "INFO" | "OK" | "UNKNOWN"</summary>
    [JsonPropertyName("severity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Severity { get; set; }

    [JsonPropertyName("model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AskModelRef? Model { get; set; }

    /// <summary>Human-readable highlight strings for UI summary strip.</summary>
    [JsonPropertyName("highlights")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Highlights { get; set; }

    [JsonPropertyName("explanation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Explanation { get; set; }

    [JsonPropertyName("anomaly")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Anomaly { get; set; }

    [JsonPropertyName("analysis")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Analysis { get; set; }

    [JsonPropertyName("suggestion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Suggestion { get; set; }

    /// <summary>Short title for General answer-only responses.</summary>
    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    /// <summary>3–7 bullet-point summary lines for General answers.</summary>
    [JsonPropertyName("summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Summary { get; set; }

    /// <summary>Markdown-formatted details section for General answers.</summary>
    [JsonPropertyName("details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Details { get; set; }

    /// <summary>Structured content sections for General answers. 3–6 sections, each with bullets or steps.</summary>
    [JsonPropertyName("sections")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AnswerSection>? Sections { get; set; }

    /// <summary>Reference links/sources. Only populated when user explicitly asks for sources.</summary>
    [JsonPropertyName("references")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? References { get; set; }

    /// <summary>Key metrics extracted from execution results — quick-glance KPI cards.</summary>
    [JsonPropertyName("keyMetrics")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AskKeyMetric>? KeyMetrics { get; set; }

    /// <summary>Root cause analysis — why the observed state exists.</summary>
    [JsonPropertyName("rootCause")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RootCause { get; set; }

    /// <summary>Business/operational impact statement.</summary>
    [JsonPropertyName("impact")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Impact { get; set; }

    /// <summary>Actionable recommendations with priority.</summary>
    [JsonPropertyName("recommendations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AskRecommendation>? Recommendations { get; set; }

    /// <summary>Per-server or per-item comparison table.</summary>
    [JsonPropertyName("comparison")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AskComparisonEntry>? Comparison { get; set; }
}

/// <summary>A single KPI metric for quick-glance display.</summary>
public sealed class AskKeyMetric
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    [JsonPropertyName("unit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Unit { get; set; }

    /// <summary>"ok" | "info" | "warning" | "critical"</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "info";
}

/// <summary>An actionable recommendation with priority level.</summary>
public sealed class AskRecommendation
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    /// <summary>"critical" | "high" | "medium" | "low"</summary>
    [JsonPropertyName("priority")]
    public string Priority { get; set; } = "medium";
}

/// <summary>Per-server comparison entry for side-by-side analysis.</summary>
public sealed class AskComparisonEntry
{
    [JsonPropertyName("target")]
    public string Target { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "ok";

    [JsonPropertyName("metrics")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object?>? Metrics { get; set; }

    [JsonPropertyName("note")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; set; }
}

/// <summary>A structured content block within a General answer.</summary>
public sealed class AnswerSection
{
    /// <summary>Stable identifier like "overview", "grounding", "flow".</summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>One of: "Compass","Database","ShieldCheck","Workflow","TriangleAlert","Lightbulb".</summary>
    [JsonPropertyName("icon")]
    public string Icon { get; set; } = "Compass";

    /// <summary>"ok" | "info" | "warning" | "critical".</summary>
    [JsonPropertyName("tone")]
    public string Tone { get; set; } = "info";

    [JsonPropertyName("bullets")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Bullets { get; set; }

    [JsonPropertyName("steps")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Steps { get; set; }
}

// ── Chart Details (History environments only) ────────────────────────────────

/// <summary>UI-ready chart plan for History query results.</summary>
public sealed class AskChartDetails
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>True when result data is structurally chartable (has time + metric + series).</summary>
    [JsonPropertyName("chartable")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Chartable { get; set; }

    /// <summary>Reason when enabled=false.</summary>
    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; set; }

    /// <summary>ChartId of the recommended default chart for UI.</summary>
    [JsonPropertyName("defaultChartId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DefaultChartId { get; set; }

    [JsonPropertyName("charts")]
    public List<AskChartDefinition> Charts { get; set; } = [];

    [JsonPropertyName("recommendedSeriesField")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RecommendedSeriesField { get; set; }

    [JsonPropertyName("recommendedXField")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RecommendedXField { get; set; }

    [JsonPropertyName("recommendedYField")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RecommendedYField { get; set; }

    /// <summary>Diagnostic reason when forecast chart was skipped.</summary>
    [JsonPropertyName("forecastSkipReason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ForecastSkipReason { get; set; }
}

public sealed class AskChartDefinition
{
    [JsonPropertyName("chartId")]
    public string ChartId { get; set; } = string.Empty;

    /// <summary>"area" | "anomalyLine" | "heatmap" | "bar" | "pie" | "forecastLine"</summary>
    [JsonPropertyName("chartType")]
    public string ChartType { get; set; } = string.Empty;

    [JsonPropertyName("priority")]
    public int Priority { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("subtitle")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Subtitle { get; set; }

    [JsonPropertyName("xField")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? XField { get; set; }

    [JsonPropertyName("yField")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? YField { get; set; }

    [JsonPropertyName("seriesField")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SeriesField { get; set; }

    [JsonPropertyName("categoryField")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CategoryField { get; set; }

    [JsonPropertyName("stacked")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Stacked { get; set; }

    [JsonPropertyName("showLegend")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ShowLegend { get; set; }

    [JsonPropertyName("showMarkers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ShowMarkers { get; set; }

    [JsonPropertyName("xAxisLabel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? XAxisLabel { get; set; }

    [JsonPropertyName("yAxisLabel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? YAxisLabel { get; set; }

    /// <summary>"none" | "latest" | "avg" | "max" | "min" | "sum" | "count"</summary>
    [JsonPropertyName("aggregation")]
    public string Aggregation { get; set; } = "none";

    /// <summary>"auto" | "raw" | "minute" | "hour" | "day" | "none"</summary>
    [JsonPropertyName("timeGrain")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TimeGrain { get; set; }

    /// <summary>MetricGroup filter value (e.g. "CPU", "DatabaseHealth").</summary>
    [JsonPropertyName("metricGroup")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MetricGroup { get; set; }

    /// <summary>MetricName filter value (e.g. "PercentProcessorTime").</summary>
    [JsonPropertyName("metricName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MetricName { get; set; }

    /// <summary>Pre-filter applied before charting. Keys are field names, values are allowed values.</summary>
    [JsonPropertyName("filters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string[]>? Filters { get; set; }

    /// <summary>"number" | "integer" | "percent" | "seconds" | "milliseconds" | "mb" | "gb" | "count" | "rate"</summary>
    [JsonPropertyName("formatHint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FormatHint { get; set; }

    /// <summary>"trend" | "comparison" | "ranking" | "composition" | "anomaly" | "forecast" | "peak" | "distribution"</summary>
    [JsonPropertyName("goal")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Goal { get; set; }

    /// <summary>Allow UI to toggle individual series on/off in the legend.</summary>
    [JsonPropertyName("allowSeriesToggle")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AllowSeriesToggle { get; set; }

    /// <summary>Allow UI to maximize this chart to full screen.</summary>
    [JsonPropertyName("allowMaximize")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AllowMaximize { get; set; }

    /// <summary>Optional data transform hint for the UI charting layer.</summary>
    [JsonPropertyName("transform")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AskChartTransform? Transform { get; set; }

    /// <summary>Value field for heatmap/bar charts where yField is a category axis.</summary>
    [JsonPropertyName("valueField")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ValueField { get; set; }

    /// <summary>Anomaly detection config for anomalyLine charts.</summary>
    [JsonPropertyName("anomaly")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AskAnomalyConfig? Anomaly { get; set; }

    /// <summary>Forecast config for forecastLine charts.</summary>
    [JsonPropertyName("forecast")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AskForecastConfig? Forecast { get; set; }

    /// <summary>UI interactions this chart supports: legendToggle, maximize, download, tooltip, seriesHideShow.</summary>
    [JsonPropertyName("supportedInteractions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? SupportedInteractions { get; set; }

    /// <summary>Suggested chart height: "compact" | "standard" | "tall".</summary>
    [JsonPropertyName("recommendedHeight")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RecommendedHeight { get; set; }

    /// <summary>Color intent hint: "sequential" | "diverging" | "categorical" | "alert".</summary>
    [JsonPropertyName("colorIntent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ColorIntent { get; set; }

    /// <summary>True when time data has been bucketed into intervals.</summary>
    [JsonPropertyName("timeBucketed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool TimeBucketed { get; set; }

    /// <summary>True when chart should show only the latest snapshot, not full time range.</summary>
    [JsonPropertyName("latestSnapshotOnly")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool LatestSnapshotOnly { get; set; }
}

/// <summary>Data transformation hint for UI charting (group/aggregate before rendering).</summary>
public sealed class AskChartTransform
{
    /// <summary>"group" | "bucket" | "distribution"</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("groupBy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? GroupBy { get; set; }

    [JsonPropertyName("aggregateField")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AggregateField { get; set; }

    /// <summary>"avg" | "max" | "min" | "sum" | "count" | "latest"</summary>
    [JsonPropertyName("aggregateFn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AggregateFn { get; set; }

    [JsonPropertyName("bucketField")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BucketField { get; set; }

    /// <summary>"hour" | "day" for time buckets.</summary>
    [JsonPropertyName("bucketSize")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BucketSize { get; set; }
}

/// <summary>Anomaly detection configuration for anomalyLine charts.</summary>
public sealed class AskAnomalyConfig
{
    /// <summary>"zscore" | "mean" | "movingAverage"</summary>
    [JsonPropertyName("method")]
    public string Method { get; set; } = "zscore";

    /// <summary>Z-score threshold (default 2.5) for flagging anomalies.</summary>
    [JsonPropertyName("threshold")]
    public double Threshold { get; set; } = 2.5;

    /// <summary>When true, anomaly detection is computed per-server instead of globally.</summary>
    [JsonPropertyName("serverWise")]
    public bool ServerWise { get; set; }

    /// <summary>Global mean of the metric values used for anomaly detection.</summary>
    [JsonPropertyName("mean")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Mean { get; set; }

    /// <summary>Global standard deviation of the metric values.</summary>
    [JsonPropertyName("stdDev")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double StdDev { get; set; }

    /// <summary>Upper baseline threshold (mean + threshold * stdDev).</summary>
    [JsonPropertyName("thresholdUpper")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double ThresholdUpper { get; set; }

    /// <summary>Lower baseline threshold (mean - threshold * stdDev).</summary>
    [JsonPropertyName("thresholdLower")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double ThresholdLower { get; set; }

    /// <summary>The numeric field used for anomaly detection (e.g. "MetricValue").</summary>
    [JsonPropertyName("valueField")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ValueField { get; set; }

    /// <summary>The time field used for anomaly detection (e.g. "CapturedAtUtc").</summary>
    [JsonPropertyName("timeField")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TimeField { get; set; }

    /// <summary>The series/server field used for anomaly detection (e.g. "ServerName").</summary>
    [JsonPropertyName("seriesField")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SeriesField { get; set; }

    /// <summary>Whether any anomaly points were detected above threshold. NEVER true when points is empty.</summary>
    [JsonPropertyName("hasAnomalies")]
    public bool HasAnomalies { get; set; }

    /// <summary>True when real timestamped anomaly points exist (spike/drop/trend_break/sustained_low).</summary>
    [JsonPropertyName("hasPointAnomalies")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool HasPointAnomalies { get; set; }

    /// <summary>True when peer deviation was detected across servers (different from point anomalies).</summary>
    [JsonPropertyName("hasPeerDeviation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool HasPeerDeviation { get; set; }

    /// <summary>Diagnostic reason / human-readable message about anomaly status.</summary>
    [JsonPropertyName("noAnomalyReason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NoAnomalyReason { get; set; }

    /// <summary>Human-readable message about anomaly detection results.</summary>
    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }

    /// <summary>Pre-computed anomaly points for UI highlighting. Always present (empty array if none).</summary>
    [JsonPropertyName("points")]
    public List<AskAnomalyPoint> Points { get; set; } = [];

    /// <summary>Anomaly detection mode: "statistical" | "risk_and_statistical" | "hybrid".</summary>
    [JsonPropertyName("mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mode { get; set; }

    /// <summary>Operational thresholds for the primary metric (warning/critical bounds).</summary>
    [JsonPropertyName("thresholds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AskMetricThresholds? Thresholds { get; set; }

    /// <summary>Per-server/series anomaly findings with severity and type classification.</summary>
    [JsonPropertyName("seriesFindings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AskSeriesFinding>? SeriesFindings { get; set; }

    /// <summary>Scale profile when servers have massively different value ranges.</summary>
    [JsonPropertyName("scaleProfile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AskScaleProfile? ScaleProfile { get; set; }
}

/// <summary>Operational thresholds for a metric (warning/critical bounds).</summary>
public sealed class AskMetricThresholds
{
    [JsonPropertyName("warningLow")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double WarningLow { get; set; }

    [JsonPropertyName("criticalLow")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double CriticalLow { get; set; }

    [JsonPropertyName("warningHigh")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double WarningHigh { get; set; }

    [JsonPropertyName("criticalHigh")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double CriticalHigh { get; set; }
}

/// <summary>A per-server anomaly finding combining statistical and operational risk signals.</summary>
public sealed class AskSeriesFinding
{
    [JsonPropertyName("server")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Server { get; set; }

    [JsonPropertyName("metric")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Metric { get; set; }

    /// <summary>"critical" | "high" | "medium" | "low" | "info"</summary>
    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "info";

    /// <summary>Anomaly candidate types: spike, drop, threshold_breach, peer_deviation, sustained_risk.</summary>
    [JsonPropertyName("types")]
    public List<string> Types { get; set; } = [];

    [JsonPropertyName("label")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; set; }

    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; set; }

    /// <summary>Current value of the metric for this server.</summary>
    [JsonPropertyName("currentValue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double CurrentValue { get; set; }

    /// <summary>Rolling mean across the time window.</summary>
    [JsonPropertyName("rollingMean")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double RollingMean { get; set; }

    /// <summary>Rolling standard deviation.</summary>
    [JsonPropertyName("rollingStdDev")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double RollingStdDev { get; set; }

    /// <summary>Z-score of the current value relative to the rolling window.</summary>
    [JsonPropertyName("zScore")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double ZScore { get; set; }

    /// <summary>Peer median across all servers at the latest timestamp.</summary>
    [JsonPropertyName("peerMedian")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double PeerMedian { get; set; }

    /// <summary>Percent deviation from peer median.</summary>
    [JsonPropertyName("peerDeviationPct")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double PeerDeviationPct { get; set; }

    /// <summary>Anomaly data points associated with this finding.</summary>
    [JsonPropertyName("points")]
    public List<AskAnomalyPoint> Points { get; set; } = [];
}

/// <summary>Scale profile metadata when servers have massively different value ranges.</summary>
public sealed class AskScaleProfile
{
    /// <summary>True when the max/min server average ratio exceeds 10x.</summary>
    [JsonPropertyName("hasLargeScaleGap")]
    public bool HasLargeScaleGap { get; set; }

    /// <summary>"per_server_anomaly" when scale gap is large.</summary>
    [JsonPropertyName("recommendedMode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RecommendedMode { get; set; }

    /// <summary>Ratio of highest to lowest server average.</summary>
    [JsonPropertyName("scaleRatio")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double ScaleRatio { get; set; }
}

/// <summary>A single anomaly data point detected by the API. Tooltip-ready for UI.</summary>
public sealed class AskAnomalyPoint
{
    [JsonPropertyName("server")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Server { get; set; }

    [JsonPropertyName("time")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Timestamp { get; set; }

    /// <summary>Actual metric value (real units, never normalized).</summary>
    [JsonPropertyName("value")]
    public double Value { get; set; }

    /// <summary>Expected value (per-server rolling mean) for baseline comparison.</summary>
    [JsonPropertyName("expected")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double ExpectedValue { get; set; }

    /// <summary>Absolute deviation from expected (value - expected).</summary>
    [JsonPropertyName("deviation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Deviation { get; set; }

    /// <summary>Percent deviation from expected.</summary>
    [JsonPropertyName("deviationPct")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double DeviationPct { get; set; }

    [JsonPropertyName("zScore")]
    public double ZScore { get; set; }

    /// <summary>"spike" | "drop" | "trend_break" | "sustained_low" | "threshold_breach"</summary>
    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }

    /// <summary>"critical" | "high" | "medium" | "low" | "info"</summary>
    [JsonPropertyName("severity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Severity { get; set; }

    /// <summary>Short label for UI badge (e.g. "Sharp drop", "Spike detected").</summary>
    [JsonPropertyName("label")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; set; }

    [JsonPropertyName("isAnomaly")]
    public bool IsAnomaly { get; set; }

    /// <summary>Human-readable reason for anomaly classification.</summary>
    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; set; }
}

/// <summary>Forecast configuration for forecastLine charts.</summary>
public sealed class AskForecastConfig
{
    /// <summary>Forecast horizon label (e.g. "6months").</summary>
    [JsonPropertyName("horizon")]
    public string Horizon { get; set; } = "6months";

    /// <summary>"linear_regression" | "moving_average" | "exponential_smoothing"</summary>
    [JsonPropertyName("method")]
    public string Method { get; set; } = "linear_regression";

    /// <summary>Confidence level for prediction interval (e.g. 0.95).</summary>
    [JsonPropertyName("confidenceLevel")]
    public double ConfidenceLevel { get; set; } = 0.95;

    /// <summary>ISO-8601 timestamp where forecast begins (first point after actuals).</summary>
    [JsonPropertyName("forecastStartUtc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ForecastStartUtc { get; set; }

    /// <summary>Slope of the linear trend line (units per day).</summary>
    [JsonPropertyName("slope")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Slope { get; set; }

    /// <summary>Y-intercept of the regression line.</summary>
    [JsonPropertyName("intercept")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Intercept { get; set; }

    /// <summary>R² goodness-of-fit (0..1). Higher = better fit.</summary>
    [JsonPropertyName("rSquared")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double RSquared { get; set; }

    /// <summary>Number of historical data points used for regression.</summary>
    [JsonPropertyName("historicalPointCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int HistoricalPointCount { get; set; }

    /// <summary>Pre-computed forecast points for UI rendering.</summary>
    [JsonPropertyName("points")]
    public List<AskForecastPoint> Points { get; set; } = [];
}

/// <summary>A single forecast data point with confidence bounds.</summary>
public sealed class AskForecastPoint
{
    /// <summary>ISO-8601 timestamp for this forecast point.</summary>
    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = string.Empty;

    /// <summary>Predicted metric value.</summary>
    [JsonPropertyName("value")]
    public double Value { get; set; }

    /// <summary>Lower bound of prediction interval.</summary>
    [JsonPropertyName("lower")]
    public double Lower { get; set; }

    /// <summary>Upper bound of prediction interval.</summary>
    [JsonPropertyName("upper")]
    public double Upper { get; set; }
}

/// <summary>Lightweight profile of the result data structure for UI charting decisions.</summary>
public sealed class AskDataProfile
{
    [JsonPropertyName("rowCount")]
    public int RowCount { get; set; }

    [JsonPropertyName("hasTimeField")]
    public bool HasTimeField { get; set; }

    [JsonPropertyName("hasNumericMetric")]
    public bool HasNumericMetric { get; set; }

    [JsonPropertyName("hasSeriesField")]
    public bool HasSeriesField { get; set; }

    [JsonPropertyName("timeField")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TimeField { get; set; }

    [JsonPropertyName("numericField")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NumericField { get; set; }

    [JsonPropertyName("seriesField")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SeriesField { get; set; }

    [JsonPropertyName("distinctServers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int DistinctServers { get; set; }

    [JsonPropertyName("distinctMetricNames")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int DistinctMetricNames { get; set; }

    /// <summary>"time_series_multi_server" | "time_series_single_server" | "category" | "status" | "unknown"</summary>
    [JsonPropertyName("profileType")]
    public string ProfileType { get; set; } = "unknown";
}

/// <summary>Concise chart interpretation data for UI footer/summary.</summary>
public sealed class AskChartSummary
{
    [JsonPropertyName("primaryMetric")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrimaryMetric { get; set; }

    [JsonPropertyName("primaryMetricLabel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrimaryMetricLabel { get; set; }

    [JsonPropertyName("unit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Unit { get; set; }

    /// <summary>"stable" | "increasing" | "decreasing" | "volatile" | "recovering" | "unknown"</summary>
    [JsonPropertyName("trendDirection")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TrendDirection { get; set; }

    [JsonPropertyName("highestSeries")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HighestSeries { get; set; }

    [JsonPropertyName("lowestSeries")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LowestSeries { get; set; }

    /// <summary>Per-server summary with individual trend/volatility/latestValue.</summary>
    [JsonPropertyName("series")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AskSeriesSummary>? Series { get; set; }
}

/// <summary>Per-server summary entry for chartSummary.series.</summary>
public sealed class AskSeriesSummary
{
    [JsonPropertyName("server")]
    public string Server { get; set; } = string.Empty;

    /// <summary>"stable" | "increasing" | "decreasing" | "volatile" | "recovering" | "unknown"</summary>
    [JsonPropertyName("trendDirection")]
    public string TrendDirection { get; set; } = "unknown";

    /// <summary>"low" | "medium" | "high"</summary>
    [JsonPropertyName("volatility")]
    public string Volatility { get; set; } = "low";

    [JsonPropertyName("latestValue")]
    public double LatestValue { get; set; }

    [JsonPropertyName("averageValue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double AverageValue { get; set; }

    [JsonPropertyName("minValue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double MinValue { get; set; }

    [JsonPropertyName("maxValue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double MaxValue { get; set; }
}

// ── Pipeline Process Flow ──────────────────────────────────────────────────

/// <summary>Complete pipeline process flow from tuning through execution.</summary>
public sealed class AskPipelineProcess
{
    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = string.Empty;

    [JsonPropertyName("totalSteps")]
    public int TotalSteps => Steps.Count;

    [JsonPropertyName("steps")]
    public List<AskProcessStep> Steps { get; set; } = [];
}

/// <summary>One step in the pipeline process flow.</summary>
public sealed class AskProcessStep
{
    [JsonPropertyName("step")]
    public int Step { get; set; }

    /// <summary>TUNING | TOPIC_CLASSIFY | PROMPT_BUILD | GENERATE | SAFETY_CHECK | CONTRACT_CHECK | VALIDATE | REPAIR | EXECUTE | EXPLAIN</summary>
    [JsonPropertyName("phase")]
    public string Phase { get; set; } = string.Empty;

    /// <summary>SUCCESS | FAILED | SKIPPED | BLOCKED | REPAIRED</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    /// <summary>Human-readable detail of what happened in this step.</summary>
    [JsonPropertyName("detail")]
    public string Detail { get; set; } = string.Empty;

    /// <summary>Model used for this step (if LLM-based).</summary>
    [JsonPropertyName("model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; set; }

    /// <summary>Which server was used (for VALIDATE step).</summary>
    [JsonPropertyName("server")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Server { get; set; }

    /// <summary>Error message if this step failed.</summary>
    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }

    /// <summary>Per-server execution results (for EXECUTE step).</summary>
    [JsonPropertyName("servers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AskProcessServerResult>? Servers { get; set; }
}

/// <summary>Per-server result within the EXECUTE process step.</summary>
public sealed class AskProcessServerResult
{
    [JsonPropertyName("target")]
    public string Target { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("rowCount")]
    public int RowCount { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }
}

// ════════════════════════════════════════════════════════════════════════
//  DRIFT DETECTION MODELS
// ════════════════════════════════════════════════════════════════════════

/// <summary>Fleet drift detection report — structured comparison across selected servers.</summary>
public sealed class AskDriftReport
{
    [JsonPropertyName("summary")]
    public AskDriftSummary Summary { get; set; } = new();

    [JsonPropertyName("driftItems")]
    public List<AskDriftItem> DriftItems { get; set; } = [];

    [JsonPropertyName("alignedSettings")]
    public int AlignedSettings { get; set; }

    /// <summary>Chart-ready visual data — no text parsing needed by frontend.</summary>
    [JsonPropertyName("visuals")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AskDriftVisuals? Visuals { get; set; }
}

/// <summary>High-level drift statistics.</summary>
public sealed class AskDriftSummary
{
    [JsonPropertyName("totalServers")]
    public int TotalServers { get; set; }

    [JsonPropertyName("totalSettingsCompared")]
    public int TotalSettingsCompared { get; set; }

    [JsonPropertyName("driftCount")]
    public int DriftCount { get; set; }

    [JsonPropertyName("criticalCount")]
    public int CriticalCount { get; set; }

    [JsonPropertyName("highCount")]
    public int HighCount { get; set; }

    [JsonPropertyName("mediumCount")]
    public int MediumCount { get; set; }
}

/// <summary>A single drift item — one setting that differs across servers.</summary>
public sealed class AskDriftItem
{
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("settingName")]
    public string SettingName { get; set; } = string.Empty;

    /// <summary>"Critical" | "High" | "Medium" | "Low"</summary>
    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "Medium";

    /// <summary>Pre-computed severity hint for LLM context.</summary>
    [JsonPropertyName("severityHint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SeverityHint { get; set; }

    [JsonPropertyName("servers")]
    public List<AskDriftServerValue> Servers { get; set; } = [];

    [JsonPropertyName("distinctValues")]
    public List<string> DistinctValues { get; set; } = [];

    /// <summary>Server name that holds the majority value, or null if no clear majority.</summary>
    [JsonPropertyName("majorityValue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MajorityValue { get; set; }

    /// <summary>Server(s) that deviate from the majority — the outliers.</summary>
    [JsonPropertyName("outlierServers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? OutlierServers { get; set; }

    /// <summary>LLM-generated or deterministic recommendation for this drift.</summary>
    [JsonPropertyName("recommendation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Recommendation { get; set; }

    /// <summary>Description of the setting from the source data.</summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
}

/// <summary>A single server's value for a drift item.</summary>
public sealed class AskDriftServerValue
{
    [JsonPropertyName("server")]
    public string Server { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    /// <summary>True if this server is an outlier (deviates from majority).</summary>
    [JsonPropertyName("isOutlier")]
    public bool IsOutlier { get; set; }
}

// ════════════════════════════════════════════════════════════════════════
//  DRIFT VISUALS — chart-ready structured data for UI rendering
// ════════════════════════════════════════════════════════════════════════

/// <summary>Chart-ready visual data for drift detection UI. No text parsing needed by frontend.</summary>
public sealed class AskDriftVisuals
{
    /// <summary>Breakdown of drift items by severity level.</summary>
    [JsonPropertyName("severityCounts")]
    public AskDriftSeverityCounts SeverityCounts { get; set; } = new();

    /// <summary>Drift count per category — for bar/donut chart.</summary>
    [JsonPropertyName("categoryMetrics")]
    public List<AskDriftCategoryMetric> CategoryMetrics { get; set; } = [];

    /// <summary>Deviation count per server — for bar chart.</summary>
    [JsonPropertyName("serverMetrics")]
    public List<AskDriftServerMetric> ServerMetrics { get; set; } = [];

    /// <summary>Setting × Server value matrix — for heatmap / comparison grid. Top drift items only.</summary>
    [JsonPropertyName("settingMatrix")]
    public List<AskDriftSettingRow> SettingMatrix { get; set; } = [];
}

/// <summary>Severity breakdown counts for donut/pie chart.</summary>
public sealed class AskDriftSeverityCounts
{
    [JsonPropertyName("critical")]
    public int Critical { get; set; }

    [JsonPropertyName("high")]
    public int High { get; set; }

    [JsonPropertyName("medium")]
    public int Medium { get; set; }

    [JsonPropertyName("low")]
    public int Low { get; set; }
}

/// <summary>Drift count for one category — drives category bar chart.</summary>
public sealed class AskDriftCategoryMetric
{
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("critical")]
    public int Critical { get; set; }

    [JsonPropertyName("high")]
    public int High { get; set; }

    [JsonPropertyName("medium")]
    public int Medium { get; set; }

    [JsonPropertyName("low")]
    public int Low { get; set; }
}

/// <summary>Per-server deviation summary — drives server drift bar chart.</summary>
public sealed class AskDriftServerMetric
{
    [JsonPropertyName("server")]
    public string Server { get; set; } = string.Empty;

    [JsonPropertyName("deviationCount")]
    public int DeviationCount { get; set; }

    [JsonPropertyName("criticalCount")]
    public int CriticalCount { get; set; }

    /// <summary>"critical" | "warning" | "ok"</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "ok";
}

/// <summary>One row in the setting × server heatmap matrix.</summary>
public sealed class AskDriftSettingRow
{
    [JsonPropertyName("setting")]
    public string Setting { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    /// <summary>"Critical" | "High" | "Medium" | "Low"</summary>
    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "Medium";

    /// <summary>Majority value across fleet (null if no clear majority).</summary>
    [JsonPropertyName("majorityValue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MajorityValue { get; set; }

    /// <summary>Server name → value dictionary. Keys are exact server names.</summary>
    [JsonPropertyName("values")]
    public Dictionary<string, string> Values { get; set; } = new();
}

// ════════════════════════════════════════════════════════════════════════
//  LIVE VISUALS — chart-ready data for any multi-server Live execution
// ════════════════════════════════════════════════════════════════════════

/// <summary>Visual data for any multi-server Live result — server comparison, status, KPIs.</summary>
public sealed class AskLiveVisuals
{
    /// <summary>Per-server execution summary — for status bar / comparison.</summary>
    [JsonPropertyName("serverStatus")]
    public List<AskLiveServerStatus> ServerStatus { get; set; } = [];

    /// <summary>Top numeric metrics per server — for grouped bar / heatmap.</summary>
    [JsonPropertyName("metricMatrix")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AskLiveMetricRow>? MetricMatrix { get; set; }

    /// <summary>Status distribution across servers — for donut chart.</summary>
    [JsonPropertyName("statusCounts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, int>? StatusCounts { get; set; }

    /// <summary>Top-level KPI values extracted from result data.</summary>
    [JsonPropertyName("kpiCards")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AskLiveKpiCard>? KpiCards { get; set; }
}

/// <summary>Per-server execution status — drives server status strip.</summary>
public sealed class AskLiveServerStatus
{
    [JsonPropertyName("server")]
    public string Server { get; set; } = string.Empty;

    /// <summary>"SUCCESS" | "FAILED" | "PARTIAL"</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "SUCCESS";

    [JsonPropertyName("rowCount")]
    public int RowCount { get; set; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }
}

/// <summary>One row in the metric × server comparison matrix.</summary>
public sealed class AskLiveMetricRow
{
    [JsonPropertyName("metric")]
    public string Metric { get; set; } = string.Empty;

    [JsonPropertyName("unit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Unit { get; set; }

    /// <summary>Server name → value dictionary.</summary>
    [JsonPropertyName("values")]
    public Dictionary<string, string> Values { get; set; } = new();
}

/// <summary>KPI card for Live visuals — headline number from result data.</summary>
public sealed class AskLiveKpiCard
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    /// <summary>"count" | "percent" | "gb" | "ms" | "status"</summary>
    [JsonPropertyName("unit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Unit { get; set; }

    /// <summary>"ok" | "warning" | "critical" | "info"</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "info";
}

// ════════════════════════════════════════════════════════════════════════
//  ROOT CAUSE DIAGNOSTIC REPORT
// ════════════════════════════════════════════════════════════════════════

/// <summary>Root cause diagnostic report — structured evidence + scored cause buckets.</summary>
public sealed class AskDiagnosticReport
{
    /// <summary>Primary root cause statement (fleet-wide).</summary>
    [JsonPropertyName("primaryCause")]
    public string PrimaryCause { get; set; } = string.Empty;

    /// <summary>Confidence score 0.0–1.0.</summary>
    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    /// <summary>"DBA Team" | "Application Team" | "Infra Team" | "IIS Team" | "Shared"</summary>
    [JsonPropertyName("owner")]
    public string Owner { get; set; } = "Shared";

    /// <summary>Scored cause buckets sorted by score descending (fleet-wide).</summary>
    [JsonPropertyName("causeScores")]
    public List<AskCauseScore> CauseScores { get; set; } = [];

    /// <summary>Evidence items supporting the diagnosis.</summary>
    [JsonPropertyName("evidence")]
    public List<string> Evidence { get; set; } = [];

    /// <summary>Secondary contributing factors.</summary>
    [JsonPropertyName("contributingFactors")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ContributingFactors { get; set; }

    /// <summary>Items confirmed NOT to be the primary cause.</summary>
    [JsonPropertyName("notPrimaryCause")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? NotPrimaryCause { get; set; }

    /// <summary>Recommended actions.</summary>
    [JsonPropertyName("actions")]
    public List<string> Actions { get; set; } = [];

    /// <summary>Per-server diagnostic findings.</summary>
    [JsonPropertyName("serverDiagnostics")]
    public List<AskServerDiagnostic> ServerDiagnostics { get; set; } = [];
}

/// <summary>Per-server root cause diagnostic.</summary>
public sealed class AskServerDiagnostic
{
    /// <summary>Server name (e.g. CTS02\ADMIN).</summary>
    [JsonPropertyName("server")]
    public string Server { get; set; } = string.Empty;

    /// <summary>"healthy" | "warning" | "critical"</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "healthy";

    /// <summary>Primary cause for this server.</summary>
    [JsonPropertyName("primaryCause")]
    public string PrimaryCause { get; set; } = string.Empty;

    /// <summary>"DBA Team" | "Application Team" | "Infra Team" | "Shared"</summary>
    [JsonPropertyName("owner")]
    public string Owner { get; set; } = "Shared";

    /// <summary>Top cause bucket scores for this server.</summary>
    [JsonPropertyName("causeScores")]
    public List<AskCauseScore> CauseScores { get; set; } = [];

    /// <summary>Key findings: blocking, heavy queries, maintenance jobs, I/O issues.</summary>
    [JsonPropertyName("findings")]
    public List<AskDiagnosticFinding> Findings { get; set; } = [];

    /// <summary>Recommended actions specific to this server.</summary>
    [JsonPropertyName("actions")]
    public List<string> Actions { get; set; } = [];
}

/// <summary>A single diagnostic finding (blocking chain, heavy query, maintenance job, etc.).</summary>
public sealed class AskDiagnosticFinding
{
    /// <summary>"blocking" | "heavy_query" | "maintenance_job" | "io_latency" | "memory_pressure" | "lock_wait" | "config_concern" | "wait_pressure"</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>"critical" | "high" | "medium" | "info"</summary>
    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "info";

    /// <summary>Human-readable title.</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Detailed description of the finding.</summary>
    [JsonPropertyName("detail")]
    public string Detail { get; set; } = string.Empty;

    /// <summary>SPID involved (if applicable).</summary>
    [JsonPropertyName("spid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Spid { get; set; }

    /// <summary>SQL text or command being executed (if applicable).</summary>
    [JsonPropertyName("sqlText")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SqlText { get; set; }

    /// <summary>Database name (if applicable).</summary>
    [JsonPropertyName("database")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Database { get; set; }

    /// <summary>Number of blocked sessions (for blocking findings).</summary>
    [JsonPropertyName("blockedCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BlockedCount { get; set; }

    /// <summary>Wait time or duration in ms (if applicable).</summary>
    [JsonPropertyName("waitTimeMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? WaitTimeMs { get; set; }

    /// <summary>CPU time in ms (if applicable).</summary>
    [JsonPropertyName("cpuTimeMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? CpuTimeMs { get; set; }

    /// <summary>I/O latency in ms (if applicable).</summary>
    [JsonPropertyName("latencyMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? LatencyMs { get; set; }
}

/// <summary>A scored root-cause bucket.</summary>
public sealed class AskCauseScore
{
    /// <summary>Cause bucket name (e.g. "SQL_BLOCKING", "WINDOWS_DISK").</summary>
    [JsonPropertyName("bucket")]
    public string Bucket { get; set; } = string.Empty;

    /// <summary>Human-readable label (e.g. "SQL Blocking", "Disk I/O").</summary>
    [JsonPropertyName("label")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; set; }

    /// <summary>Score 0–100.</summary>
    [JsonPropertyName("score")]
    public int Score { get; set; }

    /// <summary>Evidence items that contributed to this score.</summary>
    [JsonPropertyName("signals")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Signals { get; set; }
}
