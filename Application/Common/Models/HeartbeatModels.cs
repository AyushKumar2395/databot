using System.Text.Json.Serialization;

namespace Application.Common.Models;

// ── Request ──────────────────────────────────────────────────────────────

public sealed class HeartbeatRequest
{
    [JsonPropertyName("environment")]
    public string Environment { get; set; } = string.Empty;

    [JsonPropertyName("selectedTargets")]
    public string[] SelectedTargets { get; set; } = [];

    [JsonPropertyName("bearerToken")]
    public string? BearerToken { get; set; }

    /// <summary>Heartbeat interval in seconds (5-60). UI controls the polling speed.</summary>
    [JsonPropertyName("intervalSeconds")]
    public int IntervalSeconds { get; set; } = 10;

    /// <summary>Monitoring environment from the DB (e.g. "Altra2"). Optional — UI passes this from /api/servers so heartbeat cards can echo it for grouping.</summary>
    [JsonPropertyName("monitoringEnvironment")]
    public string? MonitoringEnvironment { get; set; }
}

// ── Response ─────────────────────────────────────────────────────────────

public sealed class HeartbeatResponse
{
    [JsonPropertyName("environment")]
    public string Environment { get; set; } = string.Empty;

    [JsonPropertyName("timestampUtc")]
    public string TimestampUtc { get; set; } = string.Empty;

    [JsonPropertyName("servers")]
    public List<HeartbeatServerCard> Servers { get; set; } = [];

    [JsonPropertyName("summary")]
    public HeartbeatSummary Summary { get; set; } = new();
}

public sealed class HeartbeatServerCard
{
    [JsonPropertyName("server")]
    public string Server { get; set; } = string.Empty;

    /// <summary>Databot request environment: "Windows_Live" or "SqlServer_Live".</summary>
    [JsonPropertyName("databotEnvironment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DatabotEnvironment { get; set; }

    /// <summary>Monitoring environment from the DB (e.g. "Altra2"). Use this for GROUP BY Environment in UI.</summary>
    [JsonPropertyName("monitoringEnvironment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MonitoringEnvironment { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "PENDING"; // SUCCESS | FAILED | TIMEOUT

    [JsonPropertyName("health")]
    public string Health { get; set; } = "unknown"; // healthy | warning | critical | unknown

    /// <summary>Weighted health score 0-100. null when status is not SUCCESS.</summary>
    [JsonPropertyName("healthScore")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? HealthScore { get; set; }

    /// <summary>Pulse rate in BPM for UI animation: 60 (calm) → 180 (critical).</summary>
    [JsonPropertyName("pulseRate")]
    public int PulseRate { get; set; } = 60;

    /// <summary>Hex color for card glow animation.</summary>
    [JsonPropertyName("glowColor")]
    public string GlowColor { get; set; } = "#22c55e"; // green

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }

    [JsonPropertyName("metrics")]
    public List<HeartbeatMetric> Metrics { get; set; } = [];

    /// <summary>SQL instances discovered on this Windows server. null if none or not Windows.</summary>
    [JsonPropertyName("sqlInstances")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<HeartbeatSqlInstance>? SqlInstances { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }

    /// <summary>Alert summary: counts by severity (24h) + latest detail. Null if fetch failed or skipped.</summary>
    [JsonPropertyName("alertSummary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HeartbeatAlertSummary? AlertSummary { get; set; }

    /// <summary>Is SQL Server service running? From Get_UserSQLServer SP. Null for Windows-only cards.</summary>
    [JsonPropertyName("sqlServiceOnline")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SqlServiceOnline { get; set; }

    /// <summary>Is SQL Agent service running? From Get_UserSQLServer SP. Null for Windows-only cards.</summary>
    [JsonPropertyName("sqlAgentOnline")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SqlAgentOnline { get; set; }

    /// <summary>Windows system profile: CPU, memory, uptime, processes. Null for SQL Server targets.</summary>
    [JsonPropertyName("systemInfo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HeartbeatSystemInfo? SystemInfo { get; set; }
}

public sealed class HeartbeatMetric
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public double? Value { get; set; }

    [JsonPropertyName("displayValue")]
    public string DisplayValue { get; set; } = string.Empty;

    [JsonPropertyName("unit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Unit { get; set; }

    [JsonPropertyName("health")]
    public string Health { get; set; } = "healthy";

    [JsonPropertyName("thresholds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HeartbeatThresholds? Thresholds { get; set; }
}

public sealed class HeartbeatThresholds
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

/// <summary>SQL Server instance heartbeat nested inside a Windows server card.</summary>
public sealed class HeartbeatSqlInstance
{
    [JsonPropertyName("instance")]
    public string Instance { get; set; } = string.Empty;

    /// <summary>The Windows server hosting this SQL instance (e.g. "CTS02").</summary>
    [JsonPropertyName("parentServer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentServer { get; set; }

    /// <summary>Databot request environment: "SqlServer_Live".</summary>
    [JsonPropertyName("databotEnvironment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DatabotEnvironment { get; set; }

    /// <summary>Monitoring environment from the DB (e.g. "Altra2"). Use for GROUP BY.</summary>
    [JsonPropertyName("monitoringEnvironment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MonitoringEnvironment { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "PENDING";

    [JsonPropertyName("health")]
    public string Health { get; set; } = "unknown";

    [JsonPropertyName("healthScore")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? HealthScore { get; set; }

    [JsonPropertyName("pulseRate")]
    public int PulseRate { get; set; } = 60;

    [JsonPropertyName("glowColor")]
    public string GlowColor { get; set; } = "#22c55e";

    [JsonPropertyName("metrics")]
    public List<HeartbeatMetric> Metrics { get; set; } = [];

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }

    /// <summary>Alert summary: counts by severity (24h) + latest detail. Null if fetch failed or skipped.</summary>
    [JsonPropertyName("alertSummary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HeartbeatAlertSummary? AlertSummary { get; set; }

    /// <summary>SQL Server version string, e.g. "SQL Server 2019 RTM CU18". Null if probe failed.</summary>
    /// <summary>Is SQL Server service running? From Get_UserSQLServer SP.</summary>
    [JsonPropertyName("sqlServiceOnline")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SqlServiceOnline { get; set; }

    /// <summary>Is SQL Agent service running? From Get_UserSQLServer SP.</summary>
    [JsonPropertyName("sqlAgentOnline")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SqlAgentOnline { get; set; }

    [JsonPropertyName("sqlVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SqlVersion { get; set; }

    /// <summary>SQL Server edition, e.g. "Enterprise", "Standard", "Developer".</summary>
    [JsonPropertyName("sqlEdition")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SqlEdition { get; set; }
}

public sealed class HeartbeatSummary
{
    [JsonPropertyName("totalServers")]
    public int TotalServers { get; set; }

    [JsonPropertyName("healthyCount")]
    public int HealthyCount { get; set; }

    [JsonPropertyName("warningCount")]
    public int WarningCount { get; set; }

    [JsonPropertyName("criticalCount")]
    public int CriticalCount { get; set; }

    [JsonPropertyName("failedCount")]
    public int FailedCount { get; set; }

    [JsonPropertyName("overallHealth")]
    public string OverallHealth { get; set; } = "unknown";
}

// ── Windows System Info (Task Manager style) ────────────────────────────

/// <summary>
/// Static/semi-static system profile for Windows servers.
/// Gives admins context: what hardware is this, how long has it been up, how busy is the OS.
/// </summary>
public sealed class HeartbeatSystemInfo
{
    // CPU Profile
    [JsonPropertyName("cpuName")]
    public string CpuName { get; set; } = string.Empty;

    [JsonPropertyName("currentSpeedGHz")]
    public double CurrentSpeedGHz { get; set; }

    [JsonPropertyName("baseSpeedGHz")]
    public double BaseSpeedGHz { get; set; }

    [JsonPropertyName("sockets")]
    public int Sockets { get; set; }

    [JsonPropertyName("cores")]
    public int Cores { get; set; }

    [JsonPropertyName("logicalProcessors")]
    public int LogicalProcessors { get; set; }

    [JsonPropertyName("virtualization")]
    public string Virtualization { get; set; } = "Unknown";

    // Cache
    [JsonPropertyName("l1CacheKB")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int L1CacheKB { get; set; }

    [JsonPropertyName("l2CacheKB")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int L2CacheKB { get; set; }

    [JsonPropertyName("l3CacheKB")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int L3CacheKB { get; set; }

    // System Activity
    [JsonPropertyName("processes")]
    public int Processes { get; set; }

    [JsonPropertyName("threads")]
    public int Threads { get; set; }

    [JsonPropertyName("handles")]
    public int Handles { get; set; }

    // Uptime
    /// <summary>System uptime as human-readable string, e.g. "5:15:43:11" (d:hh:mm:ss).</summary>
    [JsonPropertyName("uptime")]
    public string Uptime { get; set; } = string.Empty;

    /// <summary>Last boot time in UTC ISO 8601.</summary>
    [JsonPropertyName("lastBootUtc")]
    public string LastBootUtc { get; set; } = string.Empty;

    // Memory Profile
    [JsonPropertyName("totalMemoryGB")]
    public double TotalMemoryGB { get; set; }

    // OS Version
    /// <summary>E.g. "Windows Server 2022 Datacenter" or "Microsoft Windows Server 2019 Standard".</summary>
    [JsonPropertyName("osVersion")]
    public string OsVersion { get; set; } = string.Empty;

    /// <summary>OS build number, e.g. "10.0.20348".</summary>
    [JsonPropertyName("osBuild")]
    public string OsBuild { get; set; } = string.Empty;
}

// ── Alert Summary (counts + latest detail, 24h window) ──────────────────

/// <summary>
/// Severity counts (24h) plus the single most impactful event.
/// DBAs/admins see "how noisy is this server?" at a glance.
/// </summary>
public sealed class HeartbeatAlertSummary
{
    [JsonPropertyName("criticalCount")]
    public int CriticalCount { get; set; }

    [JsonPropertyName("errorCount")]
    public int ErrorCount { get; set; }

    [JsonPropertyName("warningCount")]
    public int WarningCount { get; set; }

    /// <summary>Total events across all severity levels in the 24h window.</summary>
    [JsonPropertyName("totalCount")]
    public int TotalCount => CriticalCount + ErrorCount + WarningCount;

    /// <summary>Highest severity present: "Critical" > "Error" > "Warning" > "Clean".</summary>
    [JsonPropertyName("highestSeverity")]
    public string HighestSeverity =>
        CriticalCount > 0 ? "Critical" :
        ErrorCount > 0 ? "Error" :
        WarningCount > 0 ? "Warning" : "Clean";

    /// <summary>The most impactful recent event (Critical first, then Error, then Warning). Null if clean.</summary>
    [JsonPropertyName("latestAlert")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HeartbeatAlert? LatestAlert { get; set; }
}

/// <summary>
/// Single-line alert detail: the most impactful recent event from either
/// Windows Event Log (System/Application/Security) or SQL Server error log.
/// </summary>
public sealed class HeartbeatAlert
{
    /// <summary>Source log: "System", "Application", "Security", or "SQL ErrorLog".</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    /// <summary>"Critical", "Error", or "Warning".</summary>
    [JsonPropertyName("severity")]
    public string Severity { get; set; } = string.Empty;

    /// <summary>UTC timestamp of the event.</summary>
    [JsonPropertyName("timeUtc")]
    public string TimeUtc { get; set; } = string.Empty;

    /// <summary>Single-line message (truncated to ~200 chars).</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

// ── AI Analysis Request / Response ───────────────────────────────────────

public sealed class HeartbeatAnalysisRequest
{
    [JsonPropertyName("server")]
    public string Server { get; set; } = string.Empty;

    [JsonPropertyName("environment")]
    public string Environment { get; set; } = string.Empty;
}

public sealed class HeartbeatAnalysisResponse
{
    [JsonPropertyName("server")]
    public string Server { get; set; } = string.Empty;

    [JsonPropertyName("snapshotCount")]
    public int SnapshotCount { get; set; }

    [JsonPropertyName("timeRangeUtc")]
    public string TimeRangeUtc { get; set; } = string.Empty;

    /// <summary>Overall server health verdict: healthy | degraded | critical | unknown.</summary>
    [JsonPropertyName("verdict")]
    public string Verdict { get; set; } = "unknown";

    /// <summary>Concise 1-2 sentence executive summary.</summary>
    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    /// <summary>Server hardware/OS profile for context display.</summary>
    [JsonPropertyName("systemContext")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HeartbeatAnalysisSystemContext? SystemContext { get; set; }

    /// <summary>Per-SQL-instance health digest.</summary>
    [JsonPropertyName("sqlInstanceSummaries")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<HeartbeatAnalysisSqlSummary>? SqlInstanceSummaries { get; set; }

    /// <summary>Event log noise digest (24h).</summary>
    [JsonPropertyName("alertDigest")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HeartbeatAnalysisAlertDigest? AlertDigest { get; set; }

    /// <summary>Detailed analysis sections.</summary>
    [JsonPropertyName("sections")]
    public List<HeartbeatAnalysisSection> Sections { get; set; } = [];

    /// <summary>Key metrics with trend over the captured window.</summary>
    [JsonPropertyName("metricTrends")]
    public List<HeartbeatMetricTrend> MetricTrends { get; set; } = [];

    /// <summary>Actionable recommendations ranked by priority.</summary>
    [JsonPropertyName("recommendations")]
    public List<HeartbeatRecommendation> Recommendations { get; set; } = [];

    /// <summary>Raw snapshot timestamps used for the analysis.</summary>
    [JsonPropertyName("snapshotTimestamps")]
    public List<string> SnapshotTimestamps { get; set; } = [];
}

/// <summary>Server hardware/OS context for display in the analysis header.</summary>
public sealed class HeartbeatAnalysisSystemContext
{
    [JsonPropertyName("os")]
    public string Os { get; set; } = string.Empty;

    [JsonPropertyName("cpu")]
    public string Cpu { get; set; } = string.Empty;

    [JsonPropertyName("totalRamGB")]
    public double TotalRamGB { get; set; }

    [JsonPropertyName("cores")]
    public int Cores { get; set; }

    [JsonPropertyName("logicalProcessors")]
    public int LogicalProcessors { get; set; }

    [JsonPropertyName("uptime")]
    public string Uptime { get; set; } = string.Empty;

    [JsonPropertyName("virtualization")]
    public string Virtualization { get; set; } = string.Empty;
}

/// <summary>Per-SQL-instance health digest for the analysis panel.</summary>
public sealed class HeartbeatAnalysisSqlSummary
{
    [JsonPropertyName("instance")]
    public string Instance { get; set; } = string.Empty;

    [JsonPropertyName("health")]
    public string Health { get; set; } = "unknown";

    [JsonPropertyName("healthScore")]
    public int? HealthScore { get; set; }

    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }

    [JsonPropertyName("edition")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Edition { get; set; }

    /// <summary>Key SQL metrics as label=value pairs.</summary>
    [JsonPropertyName("keyMetrics")]
    public List<string> KeyMetrics { get; set; } = [];
}

/// <summary>Event log noise summary for the analysis panel.</summary>
public sealed class HeartbeatAnalysisAlertDigest
{
    [JsonPropertyName("criticalCount")]
    public int CriticalCount { get; set; }

    [JsonPropertyName("errorCount")]
    public int ErrorCount { get; set; }

    [JsonPropertyName("warningCount")]
    public int WarningCount { get; set; }

    /// <summary>Human-readable noise assessment: "Clean", "Low noise", "Noisy", "Very noisy".</summary>
    [JsonPropertyName("noiseLevel")]
    public string NoiseLevel { get; set; } = "Clean";

    [JsonPropertyName("latestMessage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LatestMessage { get; set; }
}

public sealed class HeartbeatAnalysisSection
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    /// <summary>healthy | warning | critical</summary>
    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "healthy";
}

public sealed class HeartbeatMetricTrend
{
    [JsonPropertyName("metricKey")]
    public string MetricKey { get; set; } = string.Empty;

    [JsonPropertyName("metricLabel")]
    public string MetricLabel { get; set; } = string.Empty;

    [JsonPropertyName("current")]
    public double Current { get; set; }

    [JsonPropertyName("min")]
    public double Min { get; set; }

    [JsonPropertyName("max")]
    public double Max { get; set; }

    [JsonPropertyName("average")]
    public double Average { get; set; }

    /// <summary>rising | falling | stable | volatile</summary>
    [JsonPropertyName("trend")]
    public string Trend { get; set; } = "stable";

    [JsonPropertyName("unit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Unit { get; set; }

    /// <summary>healthy | warning | critical</summary>
    [JsonPropertyName("health")]
    public string Health { get; set; } = "healthy";
}

public sealed class HeartbeatRecommendation
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    /// <summary>critical | high | medium | low</summary>
    [JsonPropertyName("priority")]
    public string Priority { get; set; } = "medium";
}

/// <summary>A point-in-time snapshot of a server's vitals, stored in the ring buffer.</summary>
public sealed class HeartbeatSnapshot
{
    public string TimestampUtc { get; set; } = string.Empty;
    public HeartbeatServerCard Card { get; set; } = new();
}

// ── SignalR Stream Update ────────────────────────────────────────────────

public sealed class HeartbeatStreamUpdate
{
    [JsonPropertyName("event")]
    public string Event { get; set; } = string.Empty; // vitals | error | stopped

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HeartbeatResponse? Data { get; set; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }

    /// <summary>Phase name: "vitals" | "alerts" | "sql_instances" | "summary". Null for full vitals/error/stopped.</summary>
    [JsonPropertyName("phase")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Phase { get; set; }

    public static HeartbeatStreamUpdate Vitals(HeartbeatResponse response) =>
        new() { Event = "vitals", Data = response };

    /// <summary>Partial update: contains only the data for a specific phase. UI should merge, not replace.</summary>
    public static HeartbeatStreamUpdate PartialUpdate(HeartbeatResponse response, string phase) =>
        new() { Event = "update", Data = response, Phase = phase };

    public static HeartbeatStreamUpdate Error(string message) =>
        new() { Event = "error", Message = message };

    public static HeartbeatStreamUpdate Stopped() =>
        new() { Event = "stopped", Message = "Monitoring stopped." };
}
