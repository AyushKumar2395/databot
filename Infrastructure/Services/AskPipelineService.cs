using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Application.Common.Interfaces;
using Application.Common.Models;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

/// <summary>
/// Implements strict LLM-only orchestration:
/// tune -> answer (General) OR tune -> plan -> generate (SQL/Windows).
/// </summary>
public sealed class AskPipelineService(
    IModelSelector modelSelector,
    IEnumerable<ILLMClient> llmClients,
    IScriptAutoFixOrchestrator scriptAutoFixOrchestrator,
    IToolRegistryResolver toolRegistryResolver,
    ITemplateRenderer templateRenderer,
    IRequestPolicyService requestPolicyService,
    IQuestionSamplesRepository questionSamplesRepository,
    IUserServerRepository userServerRepository,
    ILogger<AskPipelineService> logger) : IAskPipelineService
{
    private readonly IQuestionSamplesRepository _questionSamplesRepository = questionSamplesRepository;
    // Detects alias in: FROM sys.databases [AS] alias
    private static readonly Regex SysDatabasesAliasPattern = new(
        @"\bFROM\s+sys\.databases\b(?:\s+(?:AS\s+)?(\w+))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Matches DB_NAME() used as a column value (with optional alias)
    private static readonly Regex DbNameColumnPattern = new(
        @"\bDB_NAME\s*\(\s*\)(\s+AS\s+\[[^\]]+\]|\s+AS\s+\w+)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] SqlRescueKeywords =
    [
        "sql", "database", "databases", "table", "index", "view", "query", "stored procedure", "agent", "deadlock",
        "blocking", "wait stats", "backup", "restore", "instance", "job", "dmv", "sys.",
        "configuration", "configure", "sp_configure", "maxdop", "max degree", "max memory",
        "cost threshold", "tempdb", "server property", "serverproperty", "sysconfig",
        "permission", "login", "role", "user", "schema", "object", "column",
        "filegroup", "partition", "statistics", "replication", "availability",
        "always on", "alwayson", "mirror", "log shipping", "linked server",
        "trace flag", "errorlog", "error log", "suspect", "recovery", "compatibility"
    ];

    private static readonly string[] WindowsRescueKeywords =
    [
        "windows", "server", "disk", "drives", "drive", "event log", "service", "process", "cpu", "memory",
        "port", "firewall", "volume", "iis", "cluster", "patch"
    ];

    private const string BlockedStateChangingRequestMessage =
        "BLOCKED: STATE_CHANGING_REQUEST - Only read-only diagnostics/inventory queries are allowed.";

    private static readonly string[] SqlBlockedProcedureTokens =
    [
        "xp_cmdshell",
        "sp_configure",
        "sp_OACreate",
        "OPENROWSET",
        "OPENDATASOURCE",
        "sp_add_job",
        "sp_update_job",
        "sp_delete_job",
        "sp_add_jobstep",
        "sp_update_jobstep",
        "sp_delete_jobstep",
        "sp_add_jobschedule",
        "sp_update_jobschedule",
        "sp_delete_jobschedule"
    ];

    private static readonly string[] WindowsBlockedCommands =
    [
        "Restart-Computer",
        "Stop-Computer",
        "shutdown",
        "Start-Service",
        "Stop-Service",
        "Restart-Service",
        "Stop-Process",
        "taskkill",
        "Set-ItemProperty",
        "New-ItemProperty",
        "Remove-Item",
        "Format-Volume",
        "Clear-EventLog",
        "Disable-NetAdapter",
        "New-NetFirewallRule",
        "Set-NetFirewallRule",
        "Remove-NetFirewallRule"
    ];

    // ── History sample transformation patterns ─────────────────────────

    private static readonly Regex HistoryFromUtcDeclarePattern = new(
        @"DECLARE\s+@FromUtc\s+datetime2\(\d+\)\s*=\s*[^;]+;",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HistoryToUtcDeclarePattern = new(
        @"DECLARE\s+@ToUtc\s+datetime2\(\d+\)\s*=\s*[^;]+;",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HistoryTopDeclarePattern = new(
        @"DECLARE\s+@Top\s+int\s*=\s*\d+\s*;",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HistoryServerDeclarePattern = new(
        @"DECLARE\s+@Server\s+nvarchar\(\d+\)\s*=\s*[^;]+;",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Matches @Server WHERE predicates:
    /// (@Server IS NULL OR h.col = @Server)  or  h.col = @Server</summary>
    private static readonly Regex HistoryServerWherePattern = new(
        @"\(\s*@Server\s+IS\s+NULL\s+OR\s+(\w+\.\w+)\s*=\s*@Server\s*\)|(\w+\.\w+)\s*=\s*@Server",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Matches standalone GO batch separators on their own line.</summary>
    private static readonly Regex GoBatchPattern = new(
        @"^\s*GO\s*;?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>The centralized History target label returned in result.items.</summary>
    private const string HistoryTargetLabel = "CTS03::SQLGig";

    // ── Metric whitelist (History environments) ─────────────────────────

    /// <summary>Describes a safe metric entry with pre-built SQL fragments.</summary>
    public sealed record MetricWhitelistEntry(
        string MetricKey,
        string MetricLabel,
        string MetricGroup,
        string MetricSelectSql,
        string DetailSelectSql);

    /// <summary>Builds a numeric metric SELECT: TRY_CONVERT(decimal(18,2),h.[col]) AS MetricValue.</summary>
    private static string NumericSelect(string column) =>
        $"TRY_CONVERT(decimal(18,2),h.[{column}]) AS MetricValue";

    /// <summary>Builds a Detail SELECT from related (label, column) pairs.</summary>
    private static string DetailOf(params (string label, string col)[] parts)
    {
        var fragments = parts.Select(p =>
            $"N'{p.label}=',COALESCE(CONVERT(nvarchar(50),h.[{p.col}]),N'NULL')");
        var joined = string.Join(",N'; ',", fragments);
        return $"CAST(CONCAT({joined}) AS nvarchar(4000)) AS Detail";
    }

    /// <summary>Builds a Detail SELECT for text/status columns: shows column name + value.</summary>
    private static string TextDetailOf(string column) =>
        $"CAST(CONCAT(N'Column={column}, Value=', h.[{column}]) COLLATE DATABASE_DEFAULT AS nvarchar(4000)) AS Detail";

    /// <summary>Builds a Detail SELECT for text/status column that also shows a related column.</summary>
    private static string TextDetailWithRelated(string column, params (string label, string col)[] related)
    {
        var mainPart = $"N'Column={column}, Value=', h.[{column}]";
        var relatedParts = related.Select(r =>
            $"N'; {r.label}=',COALESCE(CONVERT(nvarchar(50),h.[{r.col}]),N'NULL')");
        var all = string.Join(",", new[] { mainPart }.Concat(relatedParts));
        return $"CAST(CONCAT({all}) COLLATE DATABASE_DEFAULT AS nvarchar(4000)) AS Detail";
    }

    /// <summary>SqlServer_History metric whitelist keyed by metricKey (column name).</summary>
    internal static readonly Dictionary<string, MetricWhitelistEntry> SqlServerHistoryMetricMap = BuildSqlServerMetricMap();

    /// <summary>Windows_History metric whitelist keyed by metricKey (column name).</summary>
    internal static readonly Dictionary<string, MetricWhitelistEntry> WindowsHistoryMetricMap = BuildWindowsMetricMap();

    private static Dictionary<string, MetricWhitelistEntry> BuildSqlServerMetricMap()
    {
        var map = new Dictionary<string, MetricWhitelistEntry>(StringComparer.Ordinal);

        void AddText(string key, string label, string group, string detailSql) =>
            map[key] = new(key, label, group, "NULL AS MetricValue", detailSql);

        void Add(string key, string label, string group, string detailSql) =>
            map[key] = new(key, label, group, NumericSelect(key), detailSql);

        // ── Server Availability ──
        AddText("AGHealth", "AG Health", "Server Availability", TextDetailOf("AGHealth"));
        AddText("Replication", "Replication", "Server Availability", TextDetailOf("Replication"));
        AddText("LS", "Log Shipping", "Server Availability", TextDetailOf("LS"));
        AddText("SQLServerStartTime", "Start Time", "Server Availability",
            TextDetailWithRelated("SQLServerStartTime", ("UptimeMin", "UptimeMinutes")));
        Add("UptimeMinutes", "Uptime Minutes", "Server Availability",
            DetailOf(("StartTime", "SQLServerStartTime")));
        AddText("SQLService", "SQL Service", "Server Availability",
            TextDetailWithRelated("SQLService", ("Agent", "SQLAgentService")));
        AddText("SQLAgentService", "SQL Agent Service", "Server Availability",
            TextDetailWithRelated("SQLAgentService", ("SQL", "SQLService")));

        // ── Memory Allocation ──
        Add("TotalMemory_GB", "Total Memory", "Memory Allocation",
            DetailOf(("AvailableGB", "AvailableMemory_GB"), ("UsedGB", "UsedMemory_GB"), ("MaxMemGB", "MaxMemory_GB")));
        Add("MaxMemory_GB", "Max Memory", "Memory Allocation",
            DetailOf(("TotalGB", "TotalMemory_GB"), ("AvailableGB", "AvailableMemory_GB"), ("TargetGB", "SQLServerTargetServerMemory_GB")));
        Add("AvailableMemory_GB", "Available Memory", "Memory Allocation",
            DetailOf(("TotalGB", "TotalMemory_GB"), ("UsedGB", "UsedMemory_GB"), ("MaxMemGB", "MaxMemory_GB")));
        Add("UsedMemory_GB", "Used Memory", "Memory Allocation",
            DetailOf(("TotalGB", "TotalMemory_GB"), ("AvailableGB", "AvailableMemory_GB"), ("MaxMemGB", "MaxMemory_GB")));
        Add("SQLServerTargetServerMemory_GB", "Target Server Memory", "Memory Allocation",
            DetailOf(("TotalGB", "SQLServerTotalServerMemory_GB"), ("ProcessGB", "SQLServerProcessMemoryUsage_GB")));
        Add("SQLServerTotalServerMemory_GB", "Total Server Memory", "Memory Allocation",
            DetailOf(("TargetGB", "SQLServerTargetServerMemory_GB"), ("ProcessGB", "SQLServerProcessMemoryUsage_GB")));
        Add("SQLServerProcessMemoryUsage_GB", "Process Memory Usage", "Memory Allocation",
            DetailOf(("TotalGB", "SQLServerTotalServerMemory_GB"), ("TargetGB", "SQLServerTargetServerMemory_GB")));

        // ── Buffer Performance ──
        Add("PageLifeExpectancy_seconds", "Page Life Expectancy", "Buffer Performance",
            DetailOf(("CacheHitRatio", "BufferCacheHitRatio"), ("GrantsPending", "MemoryGrantsPending")));
        Add("BufferCacheHitRatio", "Buffer Cache Hit Ratio", "Buffer Performance",
            DetailOf(("PLE", "PageLifeExpectancy_seconds"), ("PageReads", "PageReads_sec"), ("PageWrites", "PageWrites_sec")));
        Add("PageReads_sec", "Page Reads/sec", "Buffer Performance",
            DetailOf(("PageWrites", "PageWrites_sec"), ("Checkpoint", "CheckpointPages_sec"), ("CacheHitRatio", "BufferCacheHitRatio")));
        Add("PageWrites_sec", "Page Writes/sec", "Buffer Performance",
            DetailOf(("PageReads", "PageReads_sec"), ("Checkpoint", "CheckpointPages_sec")));
        Add("CheckpointPages_sec", "Checkpoint Pages/sec", "Buffer Performance",
            DetailOf(("PageReads", "PageReads_sec"), ("PageWrites", "PageWrites_sec")));

        // ── User Activity ──
        Add("UserConnections", "User Connections", "User Activity",
            DetailOf(("Connected", "ConnectedUserCount"), ("BatchReq", "BatchRequests_sec"), ("Txn", "Transactions_sec")));
        Add("ConnectedUserCount", "Connected User Count", "User Activity",
            DetailOf(("UserConn", "UserConnections"), ("BatchReq", "BatchRequests_sec")));
        Add("BatchRequests_sec", "Batch Requests/sec", "User Activity",
            DetailOf(("UserConn", "UserConnections"), ("Txn", "Transactions_sec")));
        Add("Transactions_sec", "Transactions/sec", "User Activity",
            DetailOf(("BatchReq", "BatchRequests_sec"), ("UserConn", "UserConnections")));
        Add("TransactionCount", "Transaction Count", "User Activity",
            DetailOf(("TxnSec", "Transactions_sec"), ("BatchReq", "BatchRequests_sec")));

        // ── Compilation & Execution ──
        Add("SQLCompilations_sec", "SQL Compilations/sec", "Compilation & Execution",
            DetailOf(("ReCompilations", "SQLReCompilations_sec")));
        Add("SQLReCompilations_sec", "SQL Re-Compilations/sec", "Compilation & Execution",
            DetailOf(("Compilations", "SQLCompilations_sec")));

        // ── Memory Contention ──
        Add("MemoryGrantsPending", "Memory Grants Pending", "Memory Contention",
            DetailOf(("PLE", "PageLifeExpectancy_seconds"), ("CacheHitRatio", "BufferCacheHitRatio")));

        // ── Locking & Blocking ──
        Add("LockRequests_sec", "Lock Requests/sec", "Locking & Blocking",
            DetailOf(("LockCount", "LockCount"), ("Blocking", "BlockingCount"), ("Deadlock", "DeadlockCount")));
        Add("LockCount", "Lock Count", "Locking & Blocking",
            DetailOf(("LockReq", "LockRequests_sec"), ("Blocking", "BlockingCount")));
        Add("BlockingCount", "Blocking Count", "Locking & Blocking",
            DetailOf(("LockCount", "LockCount"), ("Deadlock", "DeadlockCount")));
        Add("DeadlockCount", "Deadlock Count", "Locking & Blocking",
            DetailOf(("Blocking", "BlockingCount"), ("LockCount", "LockCount")));

        // ── Jobs & Backups ──
        Add("FailedJobCount", "Failed Job Count", "Jobs & Backups",
            DetailOf(("FailedFull", "FailedFullBackupCount"), ("FailedDiff", "FailedDiffBackupCount"), ("FailedLog", "FailedLogBackupCount")));
        Add("FailedFullBackupCount", "Failed Full Backup Count", "Jobs & Backups",
            DetailOf(("FailedDiff", "FailedDiffBackupCount"), ("FailedLog", "FailedLogBackupCount")));
        Add("FailedDiffBackupCount", "Failed Diff Backup Count", "Jobs & Backups",
            DetailOf(("FailedFull", "FailedFullBackupCount"), ("FailedLog", "FailedLogBackupCount")));
        Add("FailedLogBackupCount", "Failed Log Backup Count", "Jobs & Backups",
            DetailOf(("FailedFull", "FailedFullBackupCount"), ("FailedDiff", "FailedDiffBackupCount")));

        return map;
    }

    private static Dictionary<string, MetricWhitelistEntry> BuildWindowsMetricMap()
    {
        var map = new Dictionary<string, MetricWhitelistEntry>(StringComparer.Ordinal);

        void Add(string key, string label, string group, string detailSql) =>
            map[key] = new(key, label, group, NumericSelect(key), detailSql);

        // ── Availability ──
        Add("Online", "Online", "Availability",
            DetailOf(("CPU", "PercentProcessorTime"), ("Memory", "AvailableMemory")));

        // ── CPU ──
        Add("PercentProcessorTime", "CPU %", "CPU",
            DetailOf(("Privileged", "PercentPrivilegedTime"), ("User", "PercentUserTime"), ("Queue", "ProcessorQueueLength")));
        Add("PercentPrivilegedTime", "Privileged Time %", "CPU",
            DetailOf(("CPU", "PercentProcessorTime"), ("User", "PercentUserTime"), ("Queue", "ProcessorQueueLength")));
        Add("PercentUserTime", "User Time %", "CPU",
            DetailOf(("CPU", "PercentProcessorTime"), ("Privileged", "PercentPrivilegedTime"), ("Queue", "ProcessorQueueLength")));

        // ── CPU Core ──
        Add("PercentProcessorTimeCore", "CPU % (Core)", "CPU Core",
            DetailOf(("PrivilegedCore", "PercentPrivilegedTimeCore"), ("UserCore", "PercentUserTimeCore")));
        Add("PercentPrivilegedTimeCore", "Privileged Time % (Core)", "CPU Core",
            DetailOf(("CPUCore", "PercentProcessorTimeCore"), ("UserCore", "PercentUserTimeCore")));
        Add("PercentUserTimeCore", "User Time % (Core)", "CPU Core",
            DetailOf(("CPUCore", "PercentProcessorTimeCore"), ("PrivilegedCore", "PercentPrivilegedTimeCore")));

        // ── Process ──
        Add("ProcessorQueueLength", "Processor Queue Length", "Process",
            DetailOf(("CPU", "PercentProcessorTime"), ("Processes", "ProcessCount")));
        Add("ProcessCount", "Process Count", "Process",
            DetailOf(("Threads", "ThreadCount"), ("Handles", "TotalHandles")));
        Add("ThreadCount", "Thread Count", "Process",
            DetailOf(("Processes", "ProcessCount"), ("Handles", "TotalHandles")));
        Add("TotalHandles", "Total Handles", "Process",
            DetailOf(("Processes", "ProcessCount"), ("Threads", "ThreadCount")));

        // ── Memory ──
        Add("AvailableMemory", "Available Memory", "Memory",
            DetailOf(("Usage", "MemoryUsage"), ("Committed", "Committed"), ("Cached", "Cached")));
        Add("MemoryUsage", "Memory Usage", "Memory",
            DetailOf(("Available", "AvailableMemory"), ("Committed", "Committed")));
        Add("FreeZeroPageListBytes", "Free Zero Page List Bytes", "Memory",
            DetailOf(("Available", "AvailableMemory"), ("Usage", "MemoryUsage")));
        Add("Committed", "Committed", "Memory",
            DetailOf(("Available", "AvailableMemory"), ("Usage", "MemoryUsage")));
        Add("Cached", "Cached", "Memory",
            DetailOf(("Available", "AvailableMemory"), ("Committed", "Committed")));

        // ── Memory Pages ──
        Add("PageWritesPerSec", "Page Writes/sec", "Memory Pages",
            DetailOf(("PageReads", "PageReadsPerSec"), ("PageFaults", "PageFaultsPerSec")));
        Add("PageReadsPerSec", "Page Reads/sec", "Memory Pages",
            DetailOf(("PageWrites", "PageWritesPerSec"), ("PageFaults", "PageFaultsPerSec")));
        Add("PageFaultsPerSec", "Page Faults/sec", "Memory Pages",
            DetailOf(("PageReads", "PageReadsPerSec"), ("PageWrites", "PageWritesPerSec")));
        Add("PagedPool", "Paged Pool", "Memory Pages",
            DetailOf(("NonPaged", "NonPagedPool"), ("Available", "AvailableMemory")));
        Add("NonPagedPool", "Non-Paged Pool", "Memory Pages",
            DetailOf(("Paged", "PagedPool"), ("Available", "AvailableMemory")));

        // ── Disk Total ──
        Add("AvgDiskQueueLengthTotal", "Avg Disk Queue Length (Total)", "Disk Total",
            DetailOf(("DiskReads", "DiskReadsPerSecTotal"), ("DiskWrites", "DiskWritesPerSecTotal")));
        Add("DiskReadsPerSecTotal", "Disk Reads/sec (Total)", "Disk Total",
            DetailOf(("DiskWrites", "DiskWritesPerSecTotal"), ("DiskBytes", "DiskBytesPerSecTotal")));
        Add("DiskWritesPerSecTotal", "Disk Writes/sec (Total)", "Disk Total",
            DetailOf(("DiskReads", "DiskReadsPerSecTotal"), ("DiskBytes", "DiskBytesPerSecTotal")));
        Add("DiskBytesPerSecTotal", "Disk Bytes/sec (Total)", "Disk Total",
            DetailOf(("DiskReads", "DiskReadsPerSecTotal"), ("DiskWrites", "DiskWritesPerSecTotal")));
        Add("PercentageDiskTimeTotal", "Disk Time % (Total)", "Disk Total",
            DetailOf(("IdleTime", "PercentageIdleTimeTotal"), ("AvgQueue", "AvgDiskQueueLengthTotal")));
        Add("PercentageIdleTimeTotal", "Idle Time % (Total)", "Disk Total",
            DetailOf(("DiskTime", "PercentageDiskTimeTotal"), ("AvgQueue", "AvgDiskQueueLengthTotal")));

        // ── Disk Drives ──
        Add("AvgDiskQueueLengthDrives", "Avg Disk Queue Length (Drives)", "Disk Drives",
            DetailOf(("DiskReads", "DiskReadsPerSecDrives"), ("DiskWrites", "DiskWritesPerSecDrives")));
        Add("DiskReadsPerSecDrives", "Disk Reads/sec (Drives)", "Disk Drives",
            DetailOf(("DiskWrites", "DiskWritesPerSecDrives"), ("DiskBytes", "DiskBytesPerSecDrives")));
        Add("DiskWritesPerSecDrives", "Disk Writes/sec (Drives)", "Disk Drives",
            DetailOf(("DiskReads", "DiskReadsPerSecDrives"), ("DiskBytes", "DiskBytesPerSecDrives")));
        Add("DiskBytesPerSecDrives", "Disk Bytes/sec (Drives)", "Disk Drives",
            DetailOf(("DiskReads", "DiskReadsPerSecDrives"), ("DiskWrites", "DiskWritesPerSecDrives")));
        Add("PercentageDiskTimeDrives", "Disk Time % (Drives)", "Disk Drives",
            DetailOf(("IdleTime", "PercentageIdleTimeDrives"), ("AvgQueue", "AvgDiskQueueLengthDrives")));
        Add("PercentageIdleTimeDrives", "Idle Time % (Drives)", "Disk Drives",
            DetailOf(("DiskTime", "PercentageDiskTimeDrives"), ("AvgQueue", "AvgDiskQueueLengthDrives")));
        Add("PhysicalDiskAvgDiskSecReadMS", "Avg Disk Read Latency (ms)", "Disk Drives",
            DetailOf(("WriteLatency", "PhysicalDiskAvgDiskSecWriteMS")));
        Add("PhysicalDiskAvgDiskSecWriteMS", "Avg Disk Write Latency (ms)", "Disk Drives",
            DetailOf(("ReadLatency", "PhysicalDiskAvgDiskSecReadMS")));

        // ── Network ──
        Add("NetworkBytesSent_KBPS", "Network Bytes Sent (KBPS)", "Network",
            DetailOf(("Received", "NetworkBytesReceived_KBPS"), ("Packets", "TotalNetworkPacketsSec")));
        Add("TotalCurrentBandwidth", "Total Current Bandwidth", "Network",
            DetailOf(("BytesSent", "NetworkBytesSent_KBPS"), ("BytesRecv", "NetworkBytesReceived_KBPS")));
        Add("TotalNetworkPacketsSec", "Total Network Packets/sec", "Network",
            DetailOf(("BytesSent", "NetworkBytesSent_KBPS"), ("BytesRecv", "NetworkBytesReceived_KBPS")));
        Add("TotalNetworkOutputQueueLength", "Network Output Queue Length", "Network",
            DetailOf(("Packets", "TotalNetworkPacketsSec"), ("BytesSent", "NetworkBytesSent_KBPS")));
        Add("NetworkBytesReceived_KBPS", "Network Bytes Received (KBPS)", "Network",
            DetailOf(("BytesSent", "NetworkBytesSent_KBPS"), ("Packets", "TotalNetworkPacketsSec")));
        Add("NetworkBytesSent_Percentage", "Network Bytes Sent %", "Network Utilization",
            DetailOf(("RecvPct", "NetworkBytesReceived_Percentage"), ("Bandwidth", "TotalCurrentBandwidth")));
        Add("NetworkBytesReceived_Percentage", "Network Bytes Received %", "Network Utilization",
            DetailOf(("SentPct", "NetworkBytesSent_Percentage"), ("Bandwidth", "TotalCurrentBandwidth")));
        Add("PacketsOutboundErrors", "Packets Outbound Errors", "Network Errors",
            DetailOf(("RecvErrors", "PacketsReceivedErrors"), ("Packets", "TotalNetworkPacketsSec")));
        Add("PacketsReceivedErrors", "Packets Received Errors", "Network Errors",
            DetailOf(("OutErrors", "PacketsOutboundErrors"), ("Packets", "TotalNetworkPacketsSec")));

        // ── SQL Server on Windows ──
        Add("SqlServerCPU", "SQL Server CPU", "SQL Server",
            DetailOf(("CPU", "PercentProcessorTime"), ("Queue", "ProcessorQueueLength")));
        Add("SQLMemoryUsage", "SQL Memory Usage", "SQL Server",
            DetailOf(("Available", "AvailableMemory"), ("Usage", "MemoryUsage")));

        return map;
    }

    /// <summary>Returns the metric whitelist for the given environment, or null if not a History environment.</summary>
    public static Dictionary<string, MetricWhitelistEntry>? GetMetricMapForEnvironment(string environment)
    {
        if (string.Equals(environment, "SqlServer_History", StringComparison.OrdinalIgnoreCase))
            return SqlServerHistoryMetricMap;
        if (string.Equals(environment, "Windows_History", StringComparison.OrdinalIgnoreCase))
            return WindowsHistoryMetricMap;
        return null;
    }

    /// <summary>Validates metricKey against the environment-specific whitelist. Returns null if valid, error message if invalid.</summary>
    internal static string? ValidateMetricKey(string? metricKey, string environment)
    {
        if (string.IsNullOrWhiteSpace(metricKey))
            return "metricKey is required for metric sample execution.";

        var map = GetMetricMapForEnvironment(environment);
        if (map is null)
            return $"Metric whitelist not available for environment '{environment}'.";

        if (!map.ContainsKey(metricKey))
            return $"metricKey '{metricKey}' is not in the allowed metric whitelist for {environment}.";

        return null;
    }

    // ── Operational Threshold Registry ─────────────────────────────────────────
    // Per-metric thresholds for operational risk detection.
    // warningLow/criticalLow = values below these are concerning (e.g. PLE < 300).
    // warningHigh/criticalHigh = values above these are concerning (e.g. CPU > 80%).
    // Zero means "no threshold in that direction".

    internal sealed record MetricThreshold(double WarningLow, double CriticalLow, double WarningHigh, double CriticalHigh);

    internal static readonly Dictionary<string, MetricThreshold> MetricThresholdRegistry = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── SQL Server: Buffer Performance ──
        ["PageLifeExpectancy_seconds"] = new(300, 60, 0, 0),
        ["BufferCacheHitRatio"]        = new(95, 90, 0, 0),

        // ── SQL Server: Memory Contention ──
        ["MemoryGrantsPending"] = new(0, 0, 1, 5),

        // ── SQL Server: Locking & Blocking ──
        ["BlockingCount"] = new(0, 0, 1, 5),
        ["DeadlockCount"] = new(0, 0, 1, 3),

        // ── SQL Server: CPU Saturation ──
        ["SignalWaitPct"] = new(0, 0, 15, 25),

        // ── SQL Server: I/O Latency ──
        ["MaxDataFileReadLatency_ms"] = new(0, 0, 20, 50),

        // ── SQL Server: Long-running queries ──
        ["LongRunningQueries"] = new(0, 0, 2, 5),

        // ── SQL Server: Jobs & Backups ──
        ["FailedJobCount"]        = new(0, 0, 1, 3),
        ["FailedFullBackupCount"] = new(0, 0, 1, 2),
        ["FailedDiffBackupCount"] = new(0, 0, 1, 2),
        ["FailedLogBackupCount"]  = new(0, 0, 1, 3),

        // ── SQL Server: Memory Allocation ──
        ["AvailableMemory_GB"] = new(2, 0.5, 0, 0),

        // ── SQL Server: Compilation ──
        ["SQLReCompilations_sec"] = new(0, 0, 50, 100),

        // ── Windows: CPU ──
        ["PercentProcessorTime"]     = new(0, 0, 80, 95),
        ["PercentPrivilegedTime"]    = new(0, 0, 50, 75),
        ["ProcessorQueueLength"]     = new(0, 0, 4, 10),
        ["SqlServerCPU"]             = new(0, 0, 80, 95),

        // ── Windows: Memory ──
        ["AvailableMemory"] = new(500, 100, 0, 0),
        ["MemoryUsage"]     = new(0, 0, 90, 98),

        // ── Windows: Disk ──
        ["PercentageDiskTimeTotal"]  = new(0, 0, 80, 95),
        ["PercentageDiskTimeDrives"] = new(0, 0, 80, 95),
        ["AvgDiskQueueLengthTotal"]  = new(0, 0, 2, 5),
        ["AvgDiskQueueLengthDrives"] = new(0, 0, 2, 5),
        ["PhysicalDiskAvgDiskSecReadMS"]  = new(0, 0, 20, 50),
        ["PhysicalDiskAvgDiskSecWriteMS"] = new(0, 0, 20, 50),

        // ── Windows: Network ──
        ["TotalNetworkOutputQueueLength"] = new(0, 0, 2, 5),
        ["PacketsOutboundErrors"]         = new(0, 0, 1, 10),
        ["PacketsReceivedErrors"]         = new(0, 0, 1, 10),
    };

    /// <summary>
    /// Computes per-server anomaly findings using a multi-type deterministic engine.
    /// Anomaly types: spike, drop, trend_break, sustained_low, threshold_breach, peer_deviation.
    /// IMPORTANT: peer_deviation NEVER creates plot points — it's a series-level finding only.
    /// Only spike/drop/trend_break/sustained_low/threshold_breach produce timestamped anomaly points.
    /// </summary>
    internal static List<AskSeriesFinding> ComputeSeriesFindings(
        AskResponseResult result,
        string numericField,
        string? seriesField,
        string? timeField,
        string? metricKey,
        double zScoreThreshold = 2.5,
        double peerDeviationPct = 30.0)
    {
        var findings = new List<AskSeriesFinding>();

        var allRows = result.Items?
            .Where(i => string.Equals(i.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase) && i.Rows is { Count: > 0 })
            .SelectMany(i => i.Rows!)
            .ToList();

        if (allRows is null or { Count: < 3 })
            return findings;

        // Extract (server, timestamp, value) tuples
        var dataPoints = allRows
            .Select(r =>
            {
                var server = seriesField is not null && r.TryGetValue(seriesField, out var sv) ? sv?.ToString() : null;
                var ts = timeField is not null && r.TryGetValue(timeField, out var tv) ? tv?.ToString() : null;
                double val = 0;
                var hasVal = r.TryGetValue(numericField, out var mv) && mv is not null && double.TryParse(mv.ToString(), out val);
                return hasVal ? (Server: server, Timestamp: ts, Value: val, Valid: true) : (Server: (string?)null, Timestamp: (string?)null, Value: 0.0, Valid: false);
            })
            .Where(dp => dp.Valid)
            .ToList();

        if (dataPoints.Count < 3)
            return findings;

        // Resolve operational thresholds
        MetricThreshold? opThreshold = null;
        if (metricKey is not null)
            MetricThresholdRegistry.TryGetValue(metricKey, out opThreshold);

        // Group by server
        var serverGroups = seriesField is not null
            ? dataPoints.GroupBy(dp => dp.Server ?? "(unknown)", StringComparer.OrdinalIgnoreCase).ToList()
            : [dataPoints.GroupBy(dp => "(all)").First()];

        // Compute peer median from latest values (for peer_deviation)
        var latestPerServer = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (seriesField is not null)
        {
            foreach (var g in serverGroups)
            {
                var lastVal = g.Last().Value;
                latestPerServer[g.Key] = lastVal;
            }
        }
        var peerValues = latestPerServer.Values.OrderBy(v => v).ToList();
        var peerMedian = peerValues.Count > 0
            ? (peerValues.Count % 2 == 0
                ? (peerValues[peerValues.Count / 2 - 1] + peerValues[peerValues.Count / 2]) / 2.0
                : peerValues[peerValues.Count / 2])
            : 0.0;

        foreach (var group in serverGroups)
        {
            var serverName = group.Key;
            var values = group.Select(dp => dp.Value).ToList();
            if (values.Count < 2) continue;

            var mean = values.Average();
            var stdDev = Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / values.Count);
            var latestValue = values[^1];
            var latestZScore = stdDev >= 0.0001 ? (latestValue - mean) / stdDev : 0.0;

            var types = new List<string>();
            var reasons = new List<string>();
            string? label = null;
            var severity = "info";
            var anomalyPointsForSeries = new List<AskAnomalyPoint>();

            string? serverOrNull = serverName == "(all)" ? null : serverName;

            AskAnomalyPoint MakePoint(
                (string? Server, string? Timestamp, double Value, bool Valid) dp,
                double expected, double z, string type, string pointSeverity, string pointLabel, string reason)
            {
                var dev = dp.Value - expected;
                var devPct = expected != 0 ? dev / Math.Abs(expected) * 100.0 : 0;
                return new AskAnomalyPoint
                {
                    Server = serverOrNull,
                    Timestamp = dp.Timestamp,
                    Value = Math.Round(dp.Value, 4),
                    ExpectedValue = Math.Round(expected, 4),
                    Deviation = Math.Round(dev, 4),
                    DeviationPct = Math.Round(devPct, 2),
                    ZScore = Math.Round(z, 2),
                    Type = type,
                    Severity = pointSeverity,
                    Label = pointLabel,
                    IsAnomaly = true,
                    Reason = reason
                };
            }

            // ── Per-server spike/drop detection (z-score from server's own baseline) ──
            if (stdDev >= 0.0001)
            {
                foreach (var dp in group)
                {
                    var z = (dp.Value - mean) / stdDev;
                    if (Math.Abs(z) >= zScoreThreshold)
                    {
                        var type = z > 0 ? "spike" : "drop";
                        if (!types.Contains(type)) types.Add(type);
                        var pointLabel = z > 0 ? "Spike detected" : "Sharp drop";
                        var pointSev = Math.Abs(z) >= zScoreThreshold * 1.5 ? "high" : "medium";
                        var direction = z > 0 ? "above" : "below";

                        anomalyPointsForSeries.Add(MakePoint(dp, mean, z, type, pointSev, pointLabel,
                            $"Value {direction} rolling baseline (z={Math.Round(z, 2):F2}, threshold={zScoreThreshold}σ)"));
                    }
                }

                if (anomalyPointsForSeries.Count > 0)
                    reasons.Add($"Statistical: {anomalyPointsForSeries.Count} point(s) exceed {zScoreThreshold}σ threshold");
            }

            // ── Trend break: detect sudden direction change at tail ──
            if (values.Count >= 6)
            {
                var tailSize = Math.Min(3, values.Count / 3);
                var headSize = values.Count - tailSize;
                var headMean = values.Take(headSize).Average();
                var tailMean = values.Skip(headSize).Average();
                var headStdDev = Math.Sqrt(values.Take(headSize).Sum(v => (v - headMean) * (v - headMean)) / headSize);

                if (headStdDev >= 0.0001)
                {
                    var trendZ = (tailMean - headMean) / headStdDev;
                    if (Math.Abs(trendZ) >= zScoreThreshold)
                    {
                        types.Add("trend_break");
                        var breakDir = trendZ > 0 ? "upward" : "downward";
                        reasons.Add($"Trend break: tail mean shifted {breakDir} by {Math.Round(Math.Abs(trendZ), 2)}σ from prior baseline");

                        // Mark the first point in the tail as the trend break point
                        var tailPoints = group.Skip(headSize).ToList();
                        if (tailPoints.Count > 0)
                        {
                            var bp = tailPoints[0];
                            var bz = (bp.Value - headMean) / headStdDev;
                            var bSev = Math.Abs(trendZ) >= zScoreThreshold * 1.5 ? "high" : "medium";
                            // Only add if not already flagged as spike/drop at this timestamp
                            if (!anomalyPointsForSeries.Any(p => p.Timestamp == bp.Timestamp))
                            {
                                anomalyPointsForSeries.Add(MakePoint(bp, headMean, bz, "trend_break", bSev,
                                    $"Trend break ({breakDir})",
                                    $"Sudden {breakDir} shift from prior baseline ({Math.Round(headMean, 2)} → {Math.Round(tailMean, 2)})"));
                            }
                        }
                    }
                }
            }

            // ── Operational threshold breach ──
            if (opThreshold is not null)
            {
                var breached = false;
                string? breachReason = null;
                if (opThreshold.CriticalLow > 0 && latestValue <= opThreshold.CriticalLow)
                {
                    types.Add("threshold_breach");
                    breachReason = $"Critical low: {Math.Round(latestValue, 2)} ≤ {opThreshold.CriticalLow}";
                    reasons.Add(breachReason);
                    severity = ElevateSeverity(severity, "critical");
                    label = $"Critically low {metricKey}";
                    breached = true;
                }
                else if (opThreshold.WarningLow > 0 && latestValue <= opThreshold.WarningLow)
                {
                    types.Add("threshold_breach");
                    breachReason = $"Warning low: {Math.Round(latestValue, 2)} ≤ {opThreshold.WarningLow}";
                    reasons.Add(breachReason);
                    severity = ElevateSeverity(severity, "high");
                    label = $"Low {metricKey}";
                    breached = true;
                }
                if (opThreshold.CriticalHigh > 0 && latestValue >= opThreshold.CriticalHigh)
                {
                    if (!types.Contains("threshold_breach")) types.Add("threshold_breach");
                    breachReason = $"Critical high: {Math.Round(latestValue, 2)} ≥ {opThreshold.CriticalHigh}";
                    reasons.Add(breachReason);
                    severity = ElevateSeverity(severity, "critical");
                    label = $"Critically high {metricKey}";
                    breached = true;
                }
                else if (opThreshold.WarningHigh > 0 && latestValue >= opThreshold.WarningHigh)
                {
                    if (!types.Contains("threshold_breach")) types.Add("threshold_breach");
                    breachReason = $"Warning high: {Math.Round(latestValue, 2)} ≥ {opThreshold.WarningHigh}";
                    reasons.Add(breachReason);
                    severity = ElevateSeverity(severity, "high");
                    label ??= $"High {metricKey}";
                    breached = true;
                }

                // Add the latest value as a threshold breach point if not already an anomaly point
                if (breached && !anomalyPointsForSeries.Any(p => p.Timestamp == group.Last().Timestamp))
                {
                    anomalyPointsForSeries.Add(MakePoint(group.Last(), mean, latestZScore,
                        "threshold_breach",
                        severity,
                        label ?? "Threshold breach",
                        $"Operational threshold breach: {breachReason}"));
                }
            }

            // ── Sustained low/high (≥50% of readings in breach zone) ──
            if (opThreshold is not null && values.Count >= 5)
            {
                var breachLowCount = 0;
                var breachHighCount = 0;
                foreach (var v in values)
                {
                    if (opThreshold.WarningLow > 0 && v <= opThreshold.WarningLow) breachLowCount++;
                    if (opThreshold.WarningHigh > 0 && v >= opThreshold.WarningHigh) breachHighCount++;
                }

                var breachCount = Math.Max(breachLowCount, breachHighCount);
                var breachPct = (double)breachCount / values.Count * 100;
                if (breachPct >= 50)
                {
                    var sustainedType = breachLowCount >= breachHighCount ? "sustained_low" : "sustained_risk";
                    types.Add(sustainedType);
                    reasons.Add($"Sustained: {Math.Round(breachPct, 0)}% of {values.Count} readings in warning zone");
                    severity = ElevateSeverity(severity, breachPct >= 80 ? "critical" : "high");
                    label ??= breachLowCount >= breachHighCount
                        ? $"Persistently low {metricKey}"
                        : $"Sustained high {metricKey}";

                    // Mark sustained points that are in the warning zone (but not already marked)
                    foreach (var dp in group)
                    {
                        var inBreach = (opThreshold.WarningLow > 0 && dp.Value <= opThreshold.WarningLow) ||
                                       (opThreshold.WarningHigh > 0 && dp.Value >= opThreshold.WarningHigh);
                        if (inBreach && !anomalyPointsForSeries.Any(p => p.Timestamp == dp.Timestamp))
                        {
                            var sz = stdDev >= 0.0001 ? (dp.Value - mean) / stdDev : 0.0;
                            anomalyPointsForSeries.Add(MakePoint(dp, mean, sz,
                                sustainedType, "medium", $"Sustained {(breachLowCount >= breachHighCount ? "low" : "high")}",
                                $"Value in operational warning zone ({dp.Value})"));
                        }
                    }
                }
            }

            // ── Peer deviation (series-level only — NEVER creates plot points) ──
            if (seriesField is not null && peerValues.Count >= 2 && peerMedian != 0)
            {
                var devPctVal = Math.Abs(latestValue - peerMedian) / Math.Abs(peerMedian) * 100;
                if (devPctVal >= peerDeviationPct)
                {
                    types.Add("peer_deviation");
                    var direction = latestValue < peerMedian ? "below" : "above";
                    reasons.Add($"Peer deviation: {Math.Round(devPctVal, 1)}% {direction} peer median ({Math.Round(peerMedian, 2)})");
                    severity = ElevateSeverity(severity, devPctVal >= 60 ? "high" : "medium");
                    label ??= $"{metricKey} deviates from peers";
                    // NOTE: No anomaly points added for peer_deviation — it's a series-level finding
                }
            }

            // Elevate severity for statistical anomalies
            if (types.Contains("spike") || types.Contains("drop"))
            {
                severity = ElevateSeverity(severity, anomalyPointsForSeries.Count >= 3 ? "high" : "medium");
                label ??= anomalyPointsForSeries.Count > 1
                    ? $"Multiple {(types.Contains("spike") ? "spikes" : "drops")} detected"
                    : $"Statistical {(types.Contains("spike") ? "spike" : "drop")} detected";
            }

            // Only emit finding if there's at least one type
            if (types.Count > 0)
            {
                findings.Add(new AskSeriesFinding
                {
                    Server = serverOrNull,
                    Metric = metricKey,
                    Severity = severity,
                    Types = types.Distinct().ToList(),
                    Label = label ?? "Anomaly detected",
                    Reason = string.Join("; ", reasons),
                    CurrentValue = Math.Round(latestValue, 4),
                    RollingMean = Math.Round(mean, 4),
                    RollingStdDev = Math.Round(stdDev, 4),
                    ZScore = Math.Round(latestZScore, 2),
                    PeerMedian = seriesField is not null ? Math.Round(peerMedian, 4) : 0,
                    PeerDeviationPct = seriesField is not null && peerMedian != 0
                        ? Math.Round(Math.Abs(latestValue - peerMedian) / Math.Abs(peerMedian) * 100, 1)
                        : 0,
                    Points = anomalyPointsForSeries
                });
            }
        }

        return findings;
    }

    /// <summary>Elevates severity only upward: info → low → medium → high → critical.</summary>
    internal static string ElevateSeverity(string current, string candidate)
    {
        var order = new[] { "info", "low", "medium", "high", "critical" };
        var currentIdx = Array.IndexOf(order, current);
        var candidateIdx = Array.IndexOf(order, candidate);
        return candidateIdx > currentIdx ? candidate : current;
    }

    /// <summary>Detects large scale gaps between servers. Returns scaleProfile metadata.</summary>
    internal static AskScaleProfile? ComputeScaleProfile(
        AskResponseResult result, string numericField, string? seriesField)
    {
        if (seriesField is null) return null;

        var allRows = result.Items?
            .Where(i => string.Equals(i.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase) && i.Rows is { Count: > 0 })
            .SelectMany(i => i.Rows!)
            .ToList();

        if (allRows is null or { Count: < 2 }) return null;

        var serverAvgs = allRows
            .Select(r =>
            {
                var server = r.TryGetValue(seriesField, out var sv) ? sv?.ToString() : null;
                double val = 0;
                var hasVal = r.TryGetValue(numericField, out var mv) && mv is not null && double.TryParse(mv.ToString(), out val);
                return (Server: server, Value: val, Valid: hasVal);
            })
            .Where(dp => dp.Valid && dp.Server is not null)
            .GroupBy(dp => dp.Server!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { Server = g.Key, Avg = g.Average(dp => dp.Value) })
            .ToList();

        if (serverAvgs.Count < 2) return null;

        var maxAvg = serverAvgs.Max(s => s.Avg);
        var minAvg = serverAvgs.Min(s => s.Avg);

        if (minAvg <= 0) minAvg = 0.001; // Prevent division by zero
        var ratio = maxAvg / minAvg;

        if (ratio < 10) return null; // Only flag when difference is 10x+

        return new AskScaleProfile
        {
            HasLargeScaleGap = true,
            RecommendedMode = "per_server_anomaly",
            ScaleRatio = Math.Round(ratio, 2)
        };
    }

    private readonly IModelSelector _modelSelector = modelSelector;
    private readonly IScriptAutoFixOrchestrator _scriptAutoFixOrchestrator = scriptAutoFixOrchestrator;
    private readonly IToolRegistryResolver _toolRegistryResolver = toolRegistryResolver;
    private readonly ITemplateRenderer _templateRenderer = templateRenderer;
    private readonly IRequestPolicyService _requestPolicyService = requestPolicyService;
    private readonly ILogger<AskPipelineService> _logger = logger;

    private readonly IReadOnlyDictionary<string, ILLMClient> _clientsByProvider =
        llmClients.ToDictionary(client => client.Provider, StringComparer.OrdinalIgnoreCase);

    public async Task<AskApiResponse> ExecuteAsync(AskApiRequest request, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Ask pipeline started. ConversationId={ConversationId}, BearerToken={BearerToken}, Environment={Environment}",
            request.ConversationId,
            request.BearerToken,
            request.Environment);

        // ── TRUSTED SAMPLE DETECTION ──────────────────────────────────────────
        // Sample requests (SampleId or GroupKey present) are pre-curated and trusted.
        // They bypass the policy gate (ENV_MISMATCH, intent classification, dangerous-action
        // heuristics) because the sample catalog is the source of truth, not the question text.
        var isTrustedSample = request.SampleId.HasValue
                           || !string.IsNullOrWhiteSpace(request.GroupKey);

        // ── REQUEST_POLICY_CHECK (skip for trusted samples) ──────────────────
        if (!isTrustedSample)
        {
            var policyDecision = _requestPolicyService.Evaluate(request, request.Question);
            if (!policyDecision.Allowed)
            {
                AskAnswerNode? blockExplanation = null;
                if (!policyDecision.NeedsClarification)
                {
                    blockExplanation = await GenerateBlockExplanationAsync(
                        request, policyDecision, cancellationToken);
                }

                return CreatePolicyBlockedResponse(request, policyDecision, blockExplanation);
            }
        }
        else
        {
            _logger.LogInformation(
                "Trusted sample request — bypassing policy gate. SampleId={SampleId}, GroupKey={GroupKey}",
                request.SampleId, request.GroupKey);
        }

        var tuneModel = _modelSelector.SelectTuneModel();
        var planModel = _modelSelector.SelectPlanModel();
        var generateModel = _modelSelector.SelectGenerateModel();

        // ── DETERMINISTIC ROUTING ─────────────────────────────────────────────
        var route = ExecutionRouter.Route(request, generateModel.Generator ?? 0);
        if (route.RouteKind == "SAMPLE_ONLY")
        {
            return await BuildSampleExecutionAsync(
                request, route, tuneModel, planModel, generateModel, cancellationToken);
        }

        // ── TUNING (skipped for SAMPLE_ONLY above) ───────────────────────────
        var routedQueryCode = string.Empty;
        var tuneClient = ResolveClient(tuneModel.Provider);
        var tuningPrompt = PromptTemplates.Tuning
            .Replace("{{$rawUserQuestion}}", request.Question, StringComparison.Ordinal)
            .Replace("{{$environmentTag}}", request.Environment, StringComparison.Ordinal)
            .Replace("{{$routedQueryCode}}", routedQueryCode, StringComparison.Ordinal);

        var tunedLine = await tuneClient.TuneAsync(
            tuningPrompt,
            request.Question,
            request.Environment,
            routedQueryCode,
            tuneModel.ModelKey,
            cancellationToken,
            apiKey: tuneModel.ApiKeyEncrypted);

        var (leftText, _) = ParseTunedLine(tunedLine);
        if (TryRecoverFalseMismatch(leftText, request.Question, request.Environment, out var recoveredLeftText))
        {
            _logger.LogWarning(
                "Recovered false mismatch from tuning model. Environment={Environment}, Before='{Before}', After='{After}'",
                request.Environment,
                leftText,
                recoveredLeftText);
            leftText = recoveredLeftText;
        }

        AskApiResponse response;

        if (EnvironmentRules.IsGeneral(request.Environment))
        {
            if (IsStopped(leftText))
                return WithProcess(CreateStoppedResponse(request, leftText, leftText, tuneModel, planModel, generateModel));

            var generalTunedQuestion = TunedQuestionRefiner.Refine(leftText, request.Environment);
            response = await BuildGeneralAnswerResponseAsync(
                request,
                generalTunedQuestion,
                tuneModel,
                planModel,
                generateModel,
                cancellationToken);
        }
        else if (EnvironmentRules.IsHistory(request.Environment))
        {
            response = await BuildHistoryQueryAsync(
                request, leftText, tuneModel, planModel, generateModel, cancellationToken);
        }
        else if (!EnvironmentRules.IsSqlServer(request.Environment) && !EnvironmentRules.IsWindows(request.Environment))
        {
            return WithProcess(CreateStoppedResponse(
                request,
                leftText,
                "MISMATCH: NEEDS_CLARIFICATION - Unsupported environment. Use General, SqlServer_Live, Windows_Live, SqlServer_History, or Windows_History.",
                tuneModel,
                planModel,
                generateModel));
        }
        else if (EnvironmentRules.IsWindows(request.Environment))
        {
            response = generateModel.Generator == 2
                ? await BuildTemplateFirstWindowsAsync(request, leftText, tuneModel, planModel, generateModel, cancellationToken)
                : await BuildLlmOnlyWindowsAsync(request, leftText, tuneModel, planModel, generateModel, cancellationToken);
        }
        else
        {
            response = generateModel.Generator == 2
                ? await BuildTemplateFirstSqlAsync(request, leftText, tuneModel, planModel, generateModel, cancellationToken)
                : await BuildLlmOnlySqlAsync(request, leftText, tuneModel, planModel, generateModel, cancellationToken);
        }

        return WithProcess(response);
    }

    /// <summary>Attaches the pipeline process flow to a response.</summary>
    private static AskApiResponse WithProcess(AskApiResponse response)
    {
        response.Process ??= BuildPipelineProcess(response);
        return response;
    }

    public async Task<AskApiResponse> ExecuteWithProgressAsync(
        AskApiRequest request,
        IProgressStream progress,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Ask SSE pipeline started. ConversationId={ConversationId}, BearerToken={BearerToken}, Environment={Environment}",
            request.ConversationId,
            request.BearerToken,
            request.Environment);

        // ── TRUSTED SAMPLE DETECTION (same as non-stream path) ────────────────
        var isTrustedSampleStream = request.SampleId.HasValue
                                 || !string.IsNullOrWhiteSpace(request.GroupKey);

        // ── REQUEST_POLICY_CHECK (skip for trusted samples) ──────────────────
        await progress.EmitAsync(ProgressEvent.PhaseStart("POLICY"), cancellationToken);
        if (!isTrustedSampleStream)
        {
            var policyDecision = _requestPolicyService.Evaluate(request, request.Question);
            if (!policyDecision.Allowed)
            {
                var policyVerdict = policyDecision.NeedsClarification
                    ? $"NEEDS_CLARIFICATION: {policyDecision.Message}"
                    : $"BLOCKED: {policyDecision.ReasonCode}";
                await progress.EmitAsync(ProgressEvent.PhaseDone("POLICY", policyVerdict), cancellationToken);
                var blockedResponse = CreatePolicyBlockedResponse(request, policyDecision);
                await progress.EmitAsync(ProgressEvent.Final(blockedResponse), cancellationToken);
                return blockedResponse;
            }
        }
        else
        {
            _logger.LogInformation(
                "Trusted sample (stream) — bypassing policy gate. SampleId={SampleId}, GroupKey={GroupKey}",
                request.SampleId, request.GroupKey);
        }
        await progress.EmitAsync(ProgressEvent.PhaseDone("POLICY"), cancellationToken);

        // ── DETERMINISTIC ROUTING ─────────────────────────────────────────────
        var tuneModel = _modelSelector.SelectTuneModel();
        var planModel = _modelSelector.SelectPlanModel();
        var generateModel = _modelSelector.SelectGenerateModel();

        var route = ExecutionRouter.Route(request, generateModel.Generator ?? 0);
        if (route.RouteKind == "SAMPLE_ONLY")
        {
            var sampleResponse = await BuildSampleExecutionAsync(
                request, route, tuneModel, planModel, generateModel, cancellationToken, progress);
            await progress.EmitAsync(ProgressEvent.Final(sampleResponse), cancellationToken);
            return sampleResponse;
        }

        // ── TUNING (skipped for SAMPLE_ONLY above) ───────────────────────────
        await progress.EmitAsync(ProgressEvent.PhaseStart("TUNING"), cancellationToken);

        var routedQueryCode = string.Empty;
        var tuneClient = ResolveClient(tuneModel.Provider);
        var tuningPrompt = PromptTemplates.Tuning
            .Replace("{{$rawUserQuestion}}", request.Question, StringComparison.Ordinal)
            .Replace("{{$environmentTag}}", request.Environment, StringComparison.Ordinal)
            .Replace("{{$routedQueryCode}}", routedQueryCode, StringComparison.Ordinal);

        var tunedLine = await tuneClient.TuneAsync(
            tuningPrompt,
            request.Question,
            request.Environment,
            routedQueryCode,
            tuneModel.ModelKey,
            cancellationToken,
            apiKey: tuneModel.ApiKeyEncrypted);

        var (leftText, _) = ParseTunedLine(tunedLine);
        if (TryRecoverFalseMismatch(leftText, request.Question, request.Environment, out var recoveredLeftText))
        {
            _logger.LogWarning(
                "Recovered false mismatch. Environment={Environment}, Before='{Before}', After='{After}'",
                request.Environment, leftText, recoveredLeftText);
            leftText = recoveredLeftText;
        }

        await progress.EmitAsync(ProgressEvent.PhaseDone("TUNING"), cancellationToken);

        // ── INTENT ───────────────────────────────────────────────────────────
        await progress.EmitAsync(ProgressEvent.PhaseStart("INTENT"), cancellationToken);
        await progress.EmitAsync(ProgressEvent.PhaseDone("INTENT"), cancellationToken);

        // ── MODEL_SELECT ─────────────────────────────────────────────────────
        await progress.EmitAsync(ProgressEvent.PhaseStart("MODEL_SELECT"), cancellationToken);
        await progress.EmitAsync(ProgressEvent.Info(
            $"tuneModel={tuneModel.ModelKey}, planModel={planModel.ModelKey}, generateModel={generateModel.ModelKey}",
            "MODEL_SELECT"), cancellationToken);
        await progress.EmitAsync(ProgressEvent.PhaseDone("MODEL_SELECT"), cancellationToken);

        // ── Route to builder — each emits GENERATE + EXECUTE + ANSWER ────────
        AskApiResponse response;

        if (EnvironmentRules.IsGeneral(request.Environment))
        {
            if (IsStopped(leftText))
            {
                response = CreateStoppedResponse(request, leftText, leftText, tuneModel, planModel, generateModel);
                await progress.EmitAsync(ProgressEvent.Final(response), cancellationToken);
                return response;
            }

            var generalTunedQuestion = TunedQuestionRefiner.Refine(leftText, request.Environment);
            response = await BuildGeneralAnswerResponseAsync(
                request, generalTunedQuestion, tuneModel, planModel, generateModel, cancellationToken, progress);
        }
        else if (EnvironmentRules.IsHistory(request.Environment))
        {
            response = await BuildHistoryQueryAsync(
                request, leftText, tuneModel, planModel, generateModel, cancellationToken, progress);
        }
        else if (!EnvironmentRules.IsSqlServer(request.Environment) && !EnvironmentRules.IsWindows(request.Environment))
        {
            response = CreateStoppedResponse(
                request, leftText,
                "MISMATCH: NEEDS_CLARIFICATION - Unsupported environment. Use General, SqlServer_Live, Windows_Live, SqlServer_History, or Windows_History.",
                tuneModel, planModel, generateModel);
        }
        else if (EnvironmentRules.IsWindows(request.Environment))
        {
            response = generateModel.Generator == 2
                ? await BuildTemplateFirstWindowsAsync(request, leftText, tuneModel, planModel, generateModel, cancellationToken, progress)
                : await BuildLlmOnlyWindowsAsync(request, leftText, tuneModel, planModel, generateModel, cancellationToken, progress);
        }
        else
        {
            response = generateModel.Generator == 2
                ? await BuildTemplateFirstSqlAsync(request, leftText, tuneModel, planModel, generateModel, cancellationToken, progress)
                : await BuildLlmOnlySqlAsync(request, leftText, tuneModel, planModel, generateModel, cancellationToken, progress);
        }

        // Build pipeline process flow for the "Process" tab
        response.Process = BuildPipelineProcess(response);

        await progress.EmitAsync(ProgressEvent.Final(response), cancellationToken);
        return response;
    }

    private async Task<AskApiResponse> BuildGeneralAnswerResponseAsync(
        AskApiRequest request,
        string tunedQuestion,
        LlmModelDefinition tuneModel,
        LlmModelDefinition planModel,
        LlmModelDefinition generateModel,
        CancellationToken cancellationToken,
        IProgressStream? progress = null)
    {
        var prog = progress ?? NullProgressStream.Instance;
        await prog.EmitAsync(ProgressEvent.PhaseStart("ANSWER"), cancellationToken);

        var answerModel = tuneModel;
        var answerClient = ResolveClient(answerModel.Provider);
        var answerPrompt = PromptTemplates.AnswerOnly
            .Replace("{{$question}}", tunedQuestion, StringComparison.Ordinal);

        var raw = await answerClient.GenerateAsync(
            answerPrompt,
            tunedQuestion,
            request.Environment,
            answerModel.ModelKey,
            cancellationToken,
            apiKey: answerModel.ApiKeyEncrypted);

        var answerNode = TryParseGeneralAnswerJson(raw, answerModel);

        // Format-only fallback: if LLM returned unstructured text, ask it to reformat.
        if (answerNode is null && !string.IsNullOrWhiteSpace(raw))
        {
            _logger.LogWarning("General answer was unstructured; requesting reformat.");
            var reformatPrompt = PromptTemplates.AnswerOnlyReformat
                .Replace("{{$text}}", raw.Trim(), StringComparison.Ordinal);

            var reformatted = await answerClient.GenerateAsync(
                reformatPrompt,
                tunedQuestion,
                request.Environment,
                answerModel.ModelKey,
                cancellationToken,
                apiKey: answerModel.ApiKeyEncrypted);

            answerNode = TryParseGeneralAnswerJson(reformatted, answerModel);
        }

        // Plain-text fallback: if both JSON parses failed but we have raw text,
        // wrap it into a structured answer so the UI gets real content.
        if (answerNode is null && !string.IsNullOrWhiteSpace(raw))
        {
            answerNode = BuildPlainTextFallbackAnswer(raw.Trim(), answerModel);
        }

        // Final fallback: use mock structured answer.
        answerNode ??= MockLlmBehavior.BuildStructuredGeneralAnswer(tunedQuestion, answerModel);

        // Post-processing: strip any markdown artifacts from details.
        SanitizeGeneralAnswerDetails(answerNode);

        // Ensure minimum 4 sections (deterministic fallback if LLM didn't produce them)
        EnsureMinimumSections(answerNode);

        // Shape validator: fix explanation == title duplication
        DeduplicateExplanation(answerNode);

        // Ensure at least one section has Steps for procedural questions
        EnsureStepsForProceduralQuestions(answerNode, tunedQuestion);

        var response = CreateBaseResponse(request, tunedQuestion, tuneModel, planModel, generateModel, _modelSelector);
        response.Plan.Mode = "GENERAL";
        response.Result.Kind = "ANSWER_ONLY";
        response.Result.Status = "NOT_EXECUTED";
        // answerText is a SHORT single-sentence fallback; real content is in answer.sections
        response.Result.AnswerText = answerNode.Title ?? "See the answer sections below for details.";
        response.Answer = answerNode;

        // Data quality: General answers may use mock if LLM key not configured
        var isMockAnswer = answerNode.Sections?.Any(s =>
            s.Key == "setup" && s.Title?.Contains("Enable Real Answers", StringComparison.OrdinalIgnoreCase) == true) == true;
        response.DataQuality = new AskDataQuality
        {
            Source = isMockAnswer ? "mock" : "real",
            UsedMockLlm = isMockAnswer,
            Warnings = isMockAnswer ? ["LLM unavailable — showing placeholder. Configure API key for real answers."] : null
        };

        await prog.EmitAsync(ProgressEvent.PhaseDone("ANSWER"), cancellationToken);
        return response;
    }

    private static readonly string[] ValidIcons =
        ["Compass", "Database", "ShieldCheck", "Workflow", "Lightbulb", "ListChecks", "Search", "Wrench", "BookOpen", "Info", "AlertTriangle"];
    private static readonly string[] ValidTones = ["ok", "info", "warning", "critical"];

    private static AskAnswerNode? TryParseGeneralAnswerJson(string? raw, LlmModelDefinition model)
    {
        var text = (raw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
            return null;

        // Strip optional markdown fence.
        var fenced = Regex.Match(text, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
        if (fenced.Success)
            text = fenced.Groups[1].Value.Trim();

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            string? GetStr(string name) =>
                root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
                    ? p.GetString()
                    : null;

            string[]? GetArr(string name)
            {
                if (!root.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Array)
                    return null;
                return p.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToArray();
            }

            var title = GetStr("title");
            var explanation = GetStr("explanation");
            var summary = GetArr("summary");
            var details = GetStr("details");
            var suggestion = GetStr("suggestion");
            var sections = ParseSections(root);

            if (title is null && summary is null && details is null && sections is null)
                return null;

            // Enforce summary range 4–6
            if (summary is { Length: > 6 })
                summary = summary[..6];

            return new AskAnswerNode
            {
                Status = "OK",
                Severity = "INFO",
                Title = title,
                Summary = summary,
                Sections = sections,
                Details = details,
                References = GetArr("references"),
                Explanation = explanation
                    ?? (summary is { Length: >= 2 } ? string.Join(" ", summary.Take(3)) : null)
                    ?? details
                    ?? title,
                Suggestion = suggestion,
                Model = new AskModelRef { Provider = model.Provider, ModelKey = model.ModelKey }
            };
        }
        catch
        {
            return null;
        }
    }

    private static List<AnswerSection>? ParseSections(JsonElement root)
    {
        if (!root.TryGetProperty("sections", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;

        var sections = new List<AnswerSection>();
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;

            var key = el.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String
                ? k.GetString()! : string.Empty;
            var title = el.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()! : key;
            var icon = el.TryGetProperty("icon", out var ic) && ic.ValueKind == JsonValueKind.String
                ? ic.GetString()! : "Compass";
            var tone = el.TryGetProperty("tone", out var tn) && tn.ValueKind == JsonValueKind.String
                ? tn.GetString()! : "info";

            // Validate icon/tone
            if (!ValidIcons.Contains(icon, StringComparer.Ordinal)) icon = "Compass";
            if (!ValidTones.Contains(tone, StringComparer.Ordinal)) tone = "info";

            List<string>? bullets = null;
            List<string>? steps = null;

            if (el.TryGetProperty("bullets", out var b) && b.ValueKind == JsonValueKind.Array)
            {
                bullets = b.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToList();
                if (bullets.Count == 0) bullets = null;
            }

            if (el.TryGetProperty("steps", out var s) && s.ValueKind == JsonValueKind.Array)
            {
                steps = s.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToList();
                if (steps.Count == 0) steps = null;
            }

            // Skip sections with neither bullets nor steps
            if (bullets is null && steps is null) continue;

            sections.Add(new AnswerSection
            {
                Key = key,
                Title = title,
                Icon = icon,
                Tone = tone,
                Bullets = bullets,
                Steps = steps
            });
        }

        // Enforce 4–8 range; accept fewer if LLM produced at least 1
        return sections.Count >= 4 ? sections.Take(8).ToList()
            : sections.Count > 0 ? sections
            : null;
    }

    /// <summary>
    /// Builds deterministic fallback sections when LLM returns none or invalid sections.
    /// Extracts best-effort content from title, summary, and details.
    /// </summary>
    internal static void EnsureMinimumSections(AskAnswerNode node)
    {
        if (node.Sections is { Count: >= 4 })
            return;

        // Build 4 minimal sections from whatever content we have
        var overviewBullets = new List<string>();
        if (node.Summary is { Length: > 0 })
            overviewBullets.AddRange(node.Summary.Take(3));
        else if (!string.IsNullOrWhiteSpace(node.Title))
            overviewBullets.Add(node.Title);
        if (overviewBullets.Count == 0)
            overviewBullets.Add("General information on the requested topic.");

        var pointsBullets = new List<string>();
        if (node.Summary is { Length: > 3 })
            pointsBullets.AddRange(node.Summary.Skip(3));
        if (!string.IsNullOrWhiteSpace(node.Details))
        {
            var lines = node.Details.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var line in lines.Take(3))
            {
                var clean = line.TrimStart('-', ' ', '*');
                if (!string.IsNullOrWhiteSpace(clean) && pointsBullets.Count < 3)
                    pointsBullets.Add(clean);
            }
        }
        if (pointsBullets.Count == 0)
            pointsBullets.Add("See the overview section for key information.");

        var contextBullets = new List<string>();
        if (!string.IsNullOrWhiteSpace(node.Explanation))
            contextBullets.Add(node.Explanation);
        else if (!string.IsNullOrWhiteSpace(node.Title))
            contextBullets.Add($"This answer covers: {node.Title}.");
        if (contextBullets.Count == 0)
            contextBullets.Add("Additional context is available upon follow-up.");

        node.Sections =
        [
            new AnswerSection
            {
                Key = "overview",
                Title = "Overview",
                Icon = "Compass",
                Tone = "info",
                Bullets = overviewBullets
            },
            new AnswerSection
            {
                Key = "principles",
                Title = "Key Points",
                Icon = "BookOpen",
                Tone = "info",
                Bullets = pointsBullets
            },
            new AnswerSection
            {
                Key = "context",
                Title = "Context",
                Icon = "Info",
                Tone = "info",
                Bullets = contextBullets
            },
            new AnswerSection
            {
                Key = "next_steps",
                Title = "Next Steps",
                Icon = "Lightbulb",
                Tone = "ok",
                Steps = ["Review the information above and refine your question if needed."]
            }
        ];
    }

    /// <summary>
    /// Shape validator: if explanation is identical to title, synthesize a better explanation
    /// from summary or details so the UI never shows a duplicated short string.
    /// </summary>
    internal static void DeduplicateExplanation(AskAnswerNode node)
    {
        if (node.Explanation is null || node.Title is null)
            return;

        if (!string.Equals(node.Explanation, node.Title, StringComparison.Ordinal))
            return;

        // Try summary join
        if (node.Summary is { Length: >= 2 })
        {
            node.Explanation = string.Join(" ", node.Summary.Take(3));
            return;
        }

        // Try details first line
        if (!string.IsNullOrWhiteSpace(node.Details))
        {
            var firstLines = node.Details
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => l.Length > 10)
                .Take(2);
            var joined = string.Join(" ", firstLines).Trim();
            if (joined.Length > 0 && !string.Equals(joined, node.Title, StringComparison.Ordinal))
            {
                node.Explanation = joined;
                return;
            }
        }

        // Last resort: prefix with context
        node.Explanation = $"This answer covers: {node.Title}.";
    }

    private static readonly string[] ProceduralKeywords = ["how", "design", "steps", "plan", "guide", "process", "procedure", "workflow"];

    /// <summary>
    /// For procedural questions (how/design/steps/plan), ensures at least one section has Steps.
    /// Converts the last section's Bullets to Steps if none already has Steps.
    /// </summary>
    internal static void EnsureStepsForProceduralQuestions(AskAnswerNode node, string question)
    {
        if (node.Sections is not { Count: > 0 })
            return;

        var words = question.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool isProcedural = words.Any(w =>
            ProceduralKeywords.Contains(w, StringComparer.OrdinalIgnoreCase));

        if (!isProcedural)
            return;

        // Already has at least one section with steps — nothing to do
        if (node.Sections.Any(s => s.Steps is { Count: > 0 }))
            return;

        // Convert the last section's bullets to steps
        var last = node.Sections[^1];
        if (last.Bullets is { Count: > 0 })
        {
            last.Steps = last.Bullets;
            last.Bullets = null;
        }
        else
        {
            last.Steps = ["Follow the guidance in the sections above to proceed."];
        }
    }

    /// <summary>
    /// Wraps raw unstructured LLM text into a structured AskAnswerNode.
    /// Used when both JSON parse and reformat attempts failed but we have real content.
    /// </summary>
    private static AskAnswerNode BuildPlainTextFallbackAnswer(string rawText, LlmModelDefinition model)
    {
        var lines = rawText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Title: first line, capped at 80 chars
        var title = lines.Length > 0 ? lines[0] : "General Answer";
        if (title.Length > 80) title = title[..80];

        // Explanation: remaining text (or first line if only one line)
        var explanation = lines.Length > 1
            ? string.Join(" ", lines.Skip(1).Take(4)).Trim()
            : title;

        // Summary: extract up to 5 sentences from the text
        var summaryLines = lines
            .Where(l => l.Length > 10 && !l.EndsWith(':'))
            .Take(5)
            .ToArray();
        if (summaryLines.Length == 0)
            summaryLines = [title];

        return new AskAnswerNode
        {
            Status = "OK",
            Severity = "INFO",
            Title = title,
            Explanation = explanation,
            Summary = summaryLines,
            Details = rawText,
            // Sections left null — EnsureMinimumSections will fill them from summary/details
            Model = new AskModelRef { Provider = model.Provider, ModelKey = model.ModelKey }
        };
    }

    // Regex patterns for markdown artifacts that must not appear in General answer details.
    private static readonly Regex MarkdownHeadingPattern = new(
        @"^#{1,6}\s+", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex MarkdownBoldPattern = new(
        @"\*\*([^*]+)\*\*", RegexOptions.Compiled);
    private static readonly Regex MarkdownCodeFencePattern = new(
        @"```[a-z]*\s*\n?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MarkdownLinkPattern = new(
        @"\[([^\]]+)\]\([^)]+\)", RegexOptions.Compiled);

    /// <summary>
    /// Strips markdown artifacts (##, **, ```, [text](url)) from General answer details,
    /// converting them to plain text section titles.
    /// </summary>
    private static void SanitizeGeneralAnswerDetails(AskAnswerNode node)
    {
        if (string.IsNullOrWhiteSpace(node.Details))
            return;

        var text = node.Details;

        // ## Heading → Heading:
        text = MarkdownHeadingPattern.Replace(text, m =>
        {
            // The heading text follows on the same line — append colon.
            return string.Empty;
        });
        // Fix heading lines: "## Foo bar" → we removed "## ", leaving "Foo bar".
        // Detect lines that were headings (now start after removal) and ensure they end with ":"
        // by re-running on the original to capture the heading text.
        text = ConvertHeadingsToPlainSections(node.Details);

        // **bold** → bold
        text = MarkdownBoldPattern.Replace(text, "$1");

        // ``` code fences → remove
        text = MarkdownCodeFencePattern.Replace(text, "");

        // [link text](url) → link text
        text = MarkdownLinkPattern.Replace(text, "$1");

        node.Details = text.Trim();

        // Keep explanation in sync.
        if (node.Explanation == node.Details || node.Explanation is not null)
            node.Explanation = node.Details;
    }

    private static string ConvertHeadingsToPlainSections(string text)
    {
        // Replace "## Title Text" with "Title Text:" on its own line.
        var result = MarkdownHeadingPattern.Replace(text, "");

        // For each line that was a heading (originally started with #), ensure it ends with ":"
        var originalLines = text.Split('\n');
        var resultLines = result.Split('\n');

        for (var i = 0; i < originalLines.Length && i < resultLines.Length; i++)
        {
            if (MarkdownHeadingPattern.IsMatch(originalLines[i]))
            {
                var line = resultLines[i].TrimEnd();
                if (line.Length > 0 && !line.EndsWith(':'))
                    resultLines[i] = line + ":";
            }
        }

        return string.Join('\n', resultLines);
    }

    private async Task<AskApiResponse> BuildLlmOnlySqlAsync(
        AskApiRequest request,
        string tunedLineLeftText,
        LlmModelDefinition tuneModel,
        LlmModelDefinition planModel,
        LlmModelDefinition generateModel,
        CancellationToken cancellationToken,
        IProgressStream? progress = null)
    {
        var prog = progress ?? NullProgressStream.Instance;
        await prog.EmitAsync(ProgressEvent.PhaseStart("GENERATE"), cancellationToken);

        // Recover from false-positive BLOCKED: if our own safety check says the question is safe,
        // the LLM gate was too aggressive — proceed with the raw question as the tuned question.
        if (tunedLineLeftText.StartsWith("BLOCKED:", StringComparison.OrdinalIgnoreCase)
            && !IsClearlyDangerousSqlRequest(request.Question))
        {
            _logger.LogWarning(
                "LLM incorrectly blocked safe SQL question; recovering. Question={Question}", request.Question);
            tunedLineLeftText = request.Question;
        }

        var tunedQuestion = ResolveSqlTunedQuestion(tunedLineLeftText, request.Question, request.Environment);
        if (IsStopped(tunedLineLeftText))
        {
            var message = tunedLineLeftText.StartsWith("BLOCKED:", StringComparison.OrdinalIgnoreCase)
                ? BlockedStateChangingRequestMessage
                : tunedLineLeftText;

            await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "stopped"), cancellationToken);
            return CreateStoppedResponse(request, tunedQuestion, message, tuneModel, planModel, generateModel);
        }

        if (IsClearlyDangerousSqlRequest(request.Question) || IsClearlyDangerousSqlRequest(tunedQuestion))
        {
            await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "blocked"), cancellationToken);
            return CreateStoppedResponse(
                request, tunedQuestion, BlockedStateChangingRequestMessage, tuneModel, planModel, generateModel);
        }

        // Generator=1: LLM is 100% responsible for script generation. No templates, no fallbacks.
        // Classify topic for anchored prompt generation (with candidate scores for debug).
        var (topic, candidates) = TopicClassifier.ClassifyWithCandidates(tunedQuestion, request.Environment);
        _logger.LogInformation(
            "Topic classified: {TopicName} (score={Score}) for question={TunedQuestion}. Candidates: {Candidates}",
            topic.TopicName, topic.Score, tunedQuestion,
            string.Join(", ", candidates.Select(c => $"{c.TopicName}={c.Score}")));

        var generateClient = ResolveClient(generateModel.Provider);
        var generatePrompt = TopicPromptBuilder.BuildPrompt(topic, tunedQuestion, request.Environment);
        var generatedScriptRaw = await generateClient.GenerateAsync(
            generatePrompt,
            tunedQuestion,
            request.Environment,
            generateModel.ModelKey,
            cancellationToken,
            apiKey: generateModel.ApiKeyEncrypted);

        var sanitizedScript = SanitizeGeneratedScript(generatedScriptRaw);
        var generatedScript = NormalizeGeneratedScript(request.Environment, sanitizedScript);

        // Topic contract enforcement: regenerate up to 3 times if contract fails.
        var contractViolation = TopicContractChecker.Check(topic, generatedScript);
        var contractPassed = contractViolation is null;
        for (var attempt = 0; attempt < 3 && contractViolation is not null; attempt++)
        {
            _logger.LogWarning(
                "SQL topic contract violation (attempt {Attempt}): {Violation}",
                attempt + 1, contractViolation);

            var repairPrompt = BuildContractRepairPrompt(
                generatePrompt, generatedScript, contractViolation, topic);
            generatedScriptRaw = await generateClient.GenerateAsync(
                repairPrompt,
                tunedQuestion,
                request.Environment,
                generateModel.ModelKey,
                cancellationToken,
                apiKey: generateModel.ApiKeyEncrypted);

            sanitizedScript = SanitizeGeneratedScript(generatedScriptRaw);
            generatedScript = NormalizeGeneratedScript(request.Environment, sanitizedScript);
            contractViolation = TopicContractChecker.Check(topic, generatedScript);
            contractPassed = contractViolation is null;
        }

        if (contractViolation is not null)
            _logger.LogWarning("SQL topic contract still failing after 3 repair attempts: {Violation}", contractViolation);

        if (TryFindDangerousCommand(request.Environment, generatedScript, out var blockedToken))
        {
            await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "blocked"), cancellationToken);
            return CreateStoppedResponse(
                request, tunedQuestion,
                $"{BlockedStateChangingRequestMessage} Token={blockedToken}",
                tuneModel, planModel, generateModel);
        }

        if (string.IsNullOrWhiteSpace(generatedScript))
        {
            _logger.LogWarning("SQL generation returned empty script. TunedQuestion={TunedQuestion}", tunedQuestion);
            await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "empty-script"), cancellationToken);
            return CreateStoppedResponse(
                request, tunedQuestion,
                "STOPPED: LLM_GENERATE returned empty script — cannot execute.",
                tuneModel, planModel, generateModel);
        }

        await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE"), cancellationToken);
        var response = await BuildExecutionResponseAsync(
            request, tunedQuestion, generatedScript,
            "SQL", "EXECUTION_READY", "LLM_GENERATE",
            tuneModel, planModel, generateModel, cancellationToken, prog);
        response.Plan.Topic = topic.TopicName;
        response.Plan.TopicScore = topic.Score;
        response.Plan.ContractPassed = contractPassed;
        response.Plan.ContractViolation = contractViolation;
        response.Plan.TopicCandidates = candidates
            .Select(c => new AskTopicCandidate { Topic = c.TopicName, Score = c.Score })
            .ToList();
        return response;
    }

    private async Task<AskApiResponse> BuildLlmOnlyWindowsAsync(
        AskApiRequest request,
        string tunedLineLeftText,
        LlmModelDefinition tuneModel,
        LlmModelDefinition planModel,
        LlmModelDefinition generateModel,
        CancellationToken cancellationToken,
        IProgressStream? progress = null)
    {
        var prog = progress ?? NullProgressStream.Instance;
        await prog.EmitAsync(ProgressEvent.PhaseStart("GENERATE"), cancellationToken);

        // Recover from false-positive BLOCKED: if our own safety check says the question is safe,
        // the LLM gate was too aggressive — proceed with the raw question as the tuned question.
        if (tunedLineLeftText.StartsWith("BLOCKED:", StringComparison.OrdinalIgnoreCase)
            && !IsClearlyDangerousWindowsRequest(request.Question))
        {
            _logger.LogWarning(
                "LLM incorrectly blocked safe Windows question; recovering. Question={Question}", request.Question);
            tunedLineLeftText = request.Question;
        }

        var tunedQuestion = ResolveWindowsTunedQuestion(tunedLineLeftText, request.Question, request.Environment);
        if (IsStopped(tunedLineLeftText))
        {
            var message = tunedLineLeftText.StartsWith("BLOCKED:", StringComparison.OrdinalIgnoreCase)
                ? BlockedStateChangingRequestMessage
                : tunedLineLeftText;
            await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "stopped"), cancellationToken);
            return CreateStoppedResponse(request, tunedQuestion, message, tuneModel, planModel, generateModel);
        }

        if (IsClearlyDangerousWindowsRequest(request.Question) ||
            IsClearlyDangerousWindowsRequest(tunedQuestion))
        {
            await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "blocked"), cancellationToken);
            return CreateStoppedResponse(
                request, tunedQuestion, BlockedStateChangingRequestMessage, tuneModel, planModel, generateModel);
        }

        // Generator=1: LLM is 100% responsible for script generation. No templates, no fallbacks.
        // Classify topic for anchored prompt generation (with candidate scores for debug).
        var (topic, candidates) = TopicClassifier.ClassifyWithCandidates(tunedQuestion, request.Environment);
        _logger.LogInformation(
            "Topic classified: {TopicName} (score={Score}) for question={TunedQuestion}. Candidates: {Candidates}",
            topic.TopicName, topic.Score, tunedQuestion,
            string.Join(", ", candidates.Select(c => $"{c.TopicName}={c.Score}")));

        var generateClient = ResolveClient(generateModel.Provider);
        var generatePrompt = TopicPromptBuilder.BuildPrompt(topic, tunedQuestion, request.Environment);
        var generatedScriptRaw = await generateClient.GenerateAsync(
            generatePrompt,
            tunedQuestion,
            request.Environment,
            generateModel.ModelKey,
            cancellationToken,
            apiKey: generateModel.ApiKeyEncrypted);

        var sanitizedScript = SanitizeWindowsGeneratedScript(generatedScriptRaw);
        if (!LooksLikePowerShell(sanitizedScript))
        {
            var retryPrompt =
                $"{generatePrompt}{System.Environment.NewLine}{System.Environment.NewLine}Your previous output contained non-script text. Return ONLY raw PowerShell code.";
            var retryScriptRaw = await generateClient.GenerateAsync(
                retryPrompt,
                tunedQuestion,
                request.Environment,
                generateModel.ModelKey,
                cancellationToken,
                apiKey: generateModel.ApiKeyEncrypted);
            sanitizedScript = SanitizeWindowsGeneratedScript(retryScriptRaw);
        }

        sanitizedScript = EnsureWindowsScriptContract(sanitizedScript, tunedQuestion);

        // Topic contract enforcement: regenerate up to 3 times if contract fails.
        var contractViolation = TopicContractChecker.Check(topic, sanitizedScript);
        var contractPassed = contractViolation is null;
        for (var attempt = 0; attempt < 3 && contractViolation is not null; attempt++)
        {
            _logger.LogWarning(
                "Windows topic contract violation (attempt {Attempt}): {Violation}",
                attempt + 1, contractViolation);

            var repairPrompt = BuildContractRepairPrompt(
                generatePrompt, sanitizedScript, contractViolation, topic);
            generatedScriptRaw = await generateClient.GenerateAsync(
                repairPrompt,
                tunedQuestion,
                request.Environment,
                generateModel.ModelKey,
                cancellationToken,
                apiKey: generateModel.ApiKeyEncrypted);

            sanitizedScript = SanitizeWindowsGeneratedScript(generatedScriptRaw);
            sanitizedScript = EnsureWindowsScriptContract(sanitizedScript, tunedQuestion);
            contractViolation = TopicContractChecker.Check(topic, sanitizedScript);
            contractPassed = contractViolation is null;
        }

        if (contractViolation is not null)
            _logger.LogWarning("Windows topic contract still failing after 3 repair attempts: {Violation}", contractViolation);

        if (TryFindDangerousCommand(request.Environment, sanitizedScript, out var blockedToken))
        {
            await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "blocked"), cancellationToken);
            return CreateStoppedResponse(
                request, tunedQuestion,
                $"{BlockedStateChangingRequestMessage} Token={blockedToken}",
                tuneModel, planModel, generateModel);
        }

        if (string.IsNullOrWhiteSpace(sanitizedScript))
        {
            _logger.LogWarning("Windows PS generation returned empty script. TunedQuestion={TunedQuestion}", tunedQuestion);
            await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "empty-script"), cancellationToken);
            return CreateStoppedResponse(
                request, tunedQuestion,
                "STOPPED: LLM_GENERATE returned empty script — cannot execute.",
                tuneModel, planModel, generateModel);
        }

        await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE"), cancellationToken);
        var response = await BuildExecutionResponseAsync(
            request, tunedQuestion, sanitizedScript,
            "PS", "EXECUTION_READY", "LLM_GENERATE",
            tuneModel, planModel, generateModel, cancellationToken, prog);
        response.Plan.Topic = topic.TopicName;
        response.Plan.TopicScore = topic.Score;
        response.Plan.ContractPassed = contractPassed;
        response.Plan.ContractViolation = contractViolation;
        response.Plan.TopicCandidates = candidates
            .Select(c => new AskTopicCandidate { Topic = c.TopicName, Score = c.Score })
            .ToList();
        return response;
    }

    private async Task<AskApiResponse> BuildHistoryQueryAsync(
        AskApiRequest request,
        string tunedLineLeftText,
        LlmModelDefinition tuneModel,
        LlmModelDefinition planModel,
        LlmModelDefinition generateModel,
        CancellationToken cancellationToken,
        IProgressStream? progress = null)
    {
        var prog = progress ?? NullProgressStream.Instance;
        await prog.EmitAsync(ProgressEvent.PhaseStart("GENERATE"), cancellationToken);

        // Recover from false-positive BLOCKED.
        if (tunedLineLeftText.StartsWith("BLOCKED:", StringComparison.OrdinalIgnoreCase)
            && !IsClearlyDangerousSqlRequest(request.Question))
        {
            _logger.LogWarning(
                "LLM incorrectly blocked safe History question; recovering. Question={Question}", request.Question);
            tunedLineLeftText = request.Question;
        }

        var tunedQuestion = ResolveSqlTunedQuestion(tunedLineLeftText, request.Question, request.Environment);
        if (IsStopped(tunedLineLeftText))
        {
            var message = tunedLineLeftText.StartsWith("BLOCKED:", StringComparison.OrdinalIgnoreCase)
                ? BlockedStateChangingRequestMessage
                : tunedLineLeftText;
            await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "stopped"), cancellationToken);
            return CreateStoppedResponse(request, tunedQuestion, message, tuneModel, planModel, generateModel);
        }

        if (IsClearlyDangerousSqlRequest(request.Question) || IsClearlyDangerousSqlRequest(tunedQuestion))
        {
            await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "blocked"), cancellationToken);
            return CreateStoppedResponse(
                request, tunedQuestion, BlockedStateChangingRequestMessage, tuneModel, planModel, generateModel);
        }

        // Build History-specific prompt with parameter substitutions.
        var normalizedServers = NormalizeHistorySelectedServers(
            request.SelectedServers ?? request.SelectedTargets);

        var selectedServerDisplay = normalizedServers.Count > 0
            ? string.Join(", ", normalizedServers)
            : "(all servers)";
        var serverLiteral = normalizedServers.Count == 1
            ? $"N'{EscapeSqlLiteral(normalizedServers[0])}'"
            : "NULL";
        var fromUtcExpr = !string.IsNullOrWhiteSpace(request.FromUtc)
            ? $"'{ParseIsoToSqlLiteral(request.FromUtc) ?? "DATEADD(HOUR,-24,SYSUTCDATETIME())"}'"
            : "DATEADD(HOUR,-24,SYSUTCDATETIME())";
        var topValue = "5000";

        var generatePrompt = PromptTemplates.HistoryGenerate
            .Replace("{{$environmentTag}}", request.Environment, StringComparison.Ordinal)
            .Replace("{{$question}}", tunedQuestion, StringComparison.Ordinal)
            .Replace("{{$selectedServer}}", selectedServerDisplay, StringComparison.Ordinal)
            .Replace("{{$serverLiteral}}", serverLiteral, StringComparison.Ordinal)
            .Replace("{{$fromUtcExpr}}", fromUtcExpr, StringComparison.Ordinal)
            .Replace("{{$topValue}}", topValue, StringComparison.Ordinal);

        var generateClient = ResolveClient(generateModel.Provider);
        var generatedScriptRaw = await generateClient.GenerateAsync(
            generatePrompt,
            tunedQuestion,
            request.Environment,
            generateModel.ModelKey,
            cancellationToken,
            apiKey: generateModel.ApiKeyEncrypted);

        var generatedScript = SanitizeGeneratedScript(generatedScriptRaw);

        // Safety check: History scripts must be read-only SQL.
        if (TryFindDangerousCommand(request.Environment, generatedScript, out var blockedToken))
        {
            await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "blocked"), cancellationToken);
            return CreateStoppedResponse(
                request, tunedQuestion,
                $"{BlockedStateChangingRequestMessage} Token={blockedToken}",
                tuneModel, planModel, generateModel);
        }

        if (string.IsNullOrWhiteSpace(generatedScript))
        {
            _logger.LogWarning("History generation returned empty script. TunedQuestion={TunedQuestion}", tunedQuestion);
            await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "empty-script"), cancellationToken);
            return CreateStoppedResponse(
                request, tunedQuestion,
                "STOPPED: LLM_GENERATE returned empty script — cannot execute.",
                tuneModel, planModel, generateModel);
        }

        // Post-process the LLM-generated script: bind servers, dates, @Top — same as sample path.
        generatedScript = TransformHistorySampleSql(
            generatedScript, request.Environment, normalizedServers,
            request.FromUtc, request.ToUtc, 5000);

        _logger.LogDebug("History LLM final script preview: {Preview}",
            generatedScript.Length > 500 ? generatedScript[..500] + "..." : generatedScript);

        await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE"), cancellationToken);

        // Execute on CTS03 (centralized history server), not on user-selected targets.
        var historyRequest = new AskApiRequest
        {
            ConversationId = request.ConversationId,
            BearerToken = request.BearerToken,
            Environment = request.Environment,
            Question = request.Question,
            SelectedTargets = [EnvironmentRules.HistoryExecutionTarget]
        };

        var response = await BuildExecutionResponseAsync(
            historyRequest, tunedQuestion, generatedScript,
            "SQL", "EXECUTION_READY", "LLM_GENERATE",
            tuneModel, planModel, generateModel, cancellationToken, prog);

        // Populate History echo fields so UI knows the filters applied.
        response.Request.TargetType = "CentralizedHistory";
        response.Request.SelectedServers = normalizedServers.Count > 0 ? normalizedServers.ToArray() : null;
        response.Request.FromUtc = request.FromUtc;
        response.Request.ToUtc = request.ToUtc;

        // Script parameters for debugging / UI reference.
        if (response.Script is not null)
        {
            response.Script.Parameters = new Dictionary<string, object?>
            {
                ["selectedServers"] = normalizedServers.Count > 0 ? normalizedServers.ToArray() : null,
                ["fromUtc"] = request.FromUtc,
                ["toUtc"] = request.ToUtc,
                ["top"] = 5000
            };
        }

        // Explain answer: analyse the result data and produce insights, recommendations, key metrics.
        await prog.EmitAsync(ProgressEvent.PhaseStart("ANSWER"), cancellationToken);
        var (histAnswer, histDrift, histDiag) = await BuildExplainAnswerAsync(
            request, tunedQuestion, response.Result, cancellationToken);
        response.Answer = histAnswer;
        response.DriftReport = histDrift;
        response.DiagnosticReport = histDiag;
        await prog.EmitAsync(ProgressEvent.PhaseDone("ANSWER"), cancellationToken);

        // Chart plan for History LLM-generated queries
        var (chartDetails, dataProfile, chartSummary) = await GenerateChartPlanAsync(
            request, tunedQuestion, response.Result, cancellationToken);
        response.ChartDetails = chartDetails;
        response.DataProfile = dataProfile;
        response.ChartSummary = chartSummary;

        // Build pipeline process flow for the "Process" tab
        response.Process = BuildPipelineProcess(response);

        return response;
    }

    private async Task<AskApiResponse> BuildTemplateFirstSqlAsync(
        AskApiRequest request,
        string tunedLineLeftText,
        LlmModelDefinition tuneModel,
        LlmModelDefinition planModel,
        LlmModelDefinition generateModel,
        CancellationToken cancellationToken,
        IProgressStream? progress = null)
    {
        var prog = progress ?? NullProgressStream.Instance;
        await prog.EmitAsync(ProgressEvent.PhaseStart("GENERATE"), cancellationToken);

        if (tunedLineLeftText.StartsWith("BLOCKED:", StringComparison.OrdinalIgnoreCase)
            && !IsClearlyDangerousSqlRequest(request.Question))
        {
            _logger.LogWarning(
                "LLM incorrectly blocked safe SQL question; recovering. Question={Question}", request.Question);
            tunedLineLeftText = request.Question;
        }

        var tunedQuestion = ResolveSqlTunedQuestion(tunedLineLeftText, request.Question, request.Environment);
        if (IsStopped(tunedLineLeftText))
        {
            var message = tunedLineLeftText.StartsWith("BLOCKED:", StringComparison.OrdinalIgnoreCase)
                ? BlockedStateChangingRequestMessage
                : tunedLineLeftText;
            await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "stopped"), cancellationToken);
            return CreateStoppedResponse(request, tunedQuestion, message, tuneModel, planModel, generateModel);
        }

        if (IsClearlyDangerousSqlRequest(request.Question) || IsClearlyDangerousSqlRequest(tunedQuestion))
        {
            await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "blocked"), cancellationToken);
            return CreateStoppedResponse(
                request, tunedQuestion, BlockedStateChangingRequestMessage, tuneModel, planModel, generateModel);
        }

        if (HealthScriptGenerator.IsHealthIntent(tunedQuestion) || HealthScriptGenerator.IsHealthIntent(request.Question))
        {
            _logger.LogInformation("Health intent detected for SQL. TunedQuestion={TunedQuestion}", tunedQuestion);
            await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "health-script"), cancellationToken);
            var healthResp = await BuildExecutionResponseAsync(
                request, tunedQuestion, HealthScriptGenerator.GetSqlHealthScript(),
                "SQL", "HEALTH", "UNIFIED_HEALTH", tuneModel, planModel, generateModel, cancellationToken, prog);
            healthResp.Plan.GeneratorMode = "TEMPLATE_OR_LLM";
            if (healthResp.Script is not null)
                healthResp.Script.Source = "LLM";
            return healthResp;
        }

        // ── Template lookup ──────────────────────────────────────────────────
        var toolResult = await _toolRegistryResolver.ResolveBestToolAsync(
            request.Environment, tunedQuestion, cancellationToken);

        if (toolResult.Found
            && !string.IsNullOrWhiteSpace(toolResult.ScriptTemplate)
            && !string.IsNullOrWhiteSpace(toolResult.ScriptLanguage))
        {
            _logger.LogInformation(
                "Template hit: QueryCode={QueryCode}, ToolName={ToolName}, Score={Score}",
                toolResult.QueryCode, toolResult.ToolName, toolResult.Score);

            var renderResult = _templateRenderer.Render(new TemplateRenderRequest
            {
                QueryCode = toolResult.QueryCode,
                Environment = request.Environment,
                ScriptLanguage = toolResult.ScriptLanguage,
                ScriptTemplate = toolResult.ScriptTemplate,
                ParameterSchemaJson = toolResult.ParameterSchema,
                RawQuestion = request.Question,
                TunedQuestion = tunedQuestion
            });

            if (renderResult.Success && !string.IsNullOrWhiteSpace(renderResult.RenderedScript))
            {
                if (TryFindDangerousCommand(request.Environment, renderResult.RenderedScript, out var blockedToken))
                {
                    _logger.LogWarning(
                        "Template script blocked. QueryCode={QueryCode}, Token={Token}",
                        toolResult.QueryCode, blockedToken);
                    await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "blocked"), cancellationToken);
                    return CreateStoppedResponse(
                        request, tunedQuestion,
                        $"{BlockedStateChangingRequestMessage} Token={blockedToken}",
                        tuneModel, planModel, generateModel);
                }

                await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "template-hit"), cancellationToken);
                var templateResp = await BuildExecutionResponseAsync(
                    request, tunedQuestion, renderResult.RenderedScript,
                    toolResult.ScriptLanguage, "EXECUTION_READY", "TEMPLATE",
                    tuneModel, planModel, generateModel, cancellationToken, prog);
                templateResp.Plan.GeneratorMode = "TEMPLATE_OR_LLM";
                templateResp.Plan.TemplateHit = true;
                templateResp.Plan.QueryCode = toolResult.QueryCode;
                if (templateResp.Script is not null)
                {
                    templateResp.Script.Source = "TEMPLATE";
                    templateResp.Script.Parameters = renderResult.BoundParameters.Count > 0
                        ? new Dictionary<string, object?>(renderResult.BoundParameters, StringComparer.OrdinalIgnoreCase)
                        : null;
                }
                return templateResp;
            }

            _logger.LogWarning(
                "Template render failed for QueryCode={QueryCode}, ErrorCode={ErrorCode}. Falling back to LLM.",
                toolResult.QueryCode, renderResult.ErrorCode);
        }
        else
        {
            _logger.LogInformation(
                "Template miss. SelectionMethod={SelectionMethod}. Falling back to LLM.",
                toolResult.SelectionMethod);
        }

        // ── LLM fallback ──────────────────────────────────────────────────────
        var generateClient = ResolveClient(generateModel.Provider);
        var generatePrompt = BuildSqlGeneratePrompt(tunedQuestion, request.Environment);
        var generatedScriptRaw = await generateClient.GenerateAsync(
            generatePrompt, tunedQuestion, request.Environment, generateModel.ModelKey, cancellationToken,
            apiKey: generateModel.ApiKeyEncrypted);

        var sanitizedScript = SanitizeGeneratedScript(generatedScriptRaw);
        if (!LooksLikeSqlScript(sanitizedScript))
            sanitizedScript = BuildFallbackSqlScript(tunedQuestion);

        var generatedScript = NormalizeGeneratedScript(request.Environment, sanitizedScript);
        if (!LooksLikeSqlScript(generatedScript)
            || !HasSqlMandatoryOutputColumns(generatedScript)
            || !IsSqlShapeSafe(generatedScript))
        {
            generatedScript = BuildFallbackSqlScript(tunedQuestion);
        }

        if (TryFindDangerousCommand(request.Environment, generatedScript, out var llmBlockedToken))
        {
            await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "blocked"), cancellationToken);
            return CreateStoppedResponse(
                request, tunedQuestion,
                $"{BlockedStateChangingRequestMessage} Token={llmBlockedToken}",
                tuneModel, planModel, generateModel);
        }

        if (string.IsNullOrWhiteSpace(generatedScript))
        {
            _logger.LogWarning("SQL generation returned empty script. TunedQuestion={TunedQuestion}", tunedQuestion);
            await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "empty-script"), cancellationToken);
            return CreateStoppedResponse(
                request, tunedQuestion,
                "STOPPED: LLM_GENERATE returned empty script — cannot execute.",
                tuneModel, planModel, generateModel);
        }

        await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE"), cancellationToken);
        var llmResp = await BuildExecutionResponseAsync(
            request, tunedQuestion, generatedScript,
            "SQL", "EXECUTION_READY", "LLM_GENERATE",
            tuneModel, planModel, generateModel, cancellationToken, prog);
        llmResp.Plan.GeneratorMode = "TEMPLATE_OR_LLM";
        if (llmResp.Script is not null)
            llmResp.Script.Source = "LLM";
        return llmResp;
    }

    private async Task<AskApiResponse> BuildTemplateFirstWindowsAsync(
        AskApiRequest request,
        string tunedLineLeftText,
        LlmModelDefinition tuneModel,
        LlmModelDefinition planModel,
        LlmModelDefinition generateModel,
        CancellationToken cancellationToken,
        IProgressStream? progress = null)
    {
        var prog = progress ?? NullProgressStream.Instance;
        await prog.EmitAsync(ProgressEvent.PhaseStart("GENERATE"), cancellationToken);

        if (tunedLineLeftText.StartsWith("BLOCKED:", StringComparison.OrdinalIgnoreCase)
            && !IsClearlyDangerousWindowsRequest(request.Question))
        {
            _logger.LogWarning(
                "LLM incorrectly blocked safe Windows question; recovering. Question={Question}", request.Question);
            tunedLineLeftText = request.Question;
        }

        var tunedQuestion = ResolveWindowsTunedQuestion(tunedLineLeftText, request.Question, request.Environment);
        if (IsStopped(tunedLineLeftText))
        {
            var message = tunedLineLeftText.StartsWith("BLOCKED:", StringComparison.OrdinalIgnoreCase)
                ? BlockedStateChangingRequestMessage
                : tunedLineLeftText;
            await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "stopped"), cancellationToken);
            return CreateStoppedResponse(request, tunedQuestion, message, tuneModel, planModel, generateModel);
        }

        if (IsClearlyDangerousWindowsRequest(request.Question) || IsClearlyDangerousWindowsRequest(tunedQuestion))
        {
            await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "blocked"), cancellationToken);
            return CreateStoppedResponse(
                request, tunedQuestion, BlockedStateChangingRequestMessage, tuneModel, planModel, generateModel);
        }

        if (HealthScriptGenerator.IsHealthIntent(tunedQuestion) || HealthScriptGenerator.IsHealthIntent(request.Question))
        {
            _logger.LogInformation("Health intent detected for Windows. TunedQuestion={TunedQuestion}", tunedQuestion);
            await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "health-script"), cancellationToken);
            var healthResp = await BuildExecutionResponseAsync(
                request, tunedQuestion, HealthScriptGenerator.GetWindowsHealthScript(),
                "PS", "HEALTH", "UNIFIED_HEALTH", tuneModel, planModel, generateModel, cancellationToken, prog);
            healthResp.Plan.GeneratorMode = "TEMPLATE_OR_LLM";
            if (healthResp.Script is not null)
                healthResp.Script.Source = "LLM";
            return healthResp;
        }

        // ── Template lookup ──────────────────────────────────────────────────
        var toolResult = await _toolRegistryResolver.ResolveBestToolAsync(
            request.Environment, tunedQuestion, cancellationToken);

        if (toolResult.Found
            && !string.IsNullOrWhiteSpace(toolResult.ScriptTemplate)
            && !string.IsNullOrWhiteSpace(toolResult.ScriptLanguage))
        {
            _logger.LogInformation(
                "Template hit: QueryCode={QueryCode}, ToolName={ToolName}, Score={Score}",
                toolResult.QueryCode, toolResult.ToolName, toolResult.Score);

            var renderResult = _templateRenderer.Render(new TemplateRenderRequest
            {
                QueryCode = toolResult.QueryCode,
                Environment = request.Environment,
                ScriptLanguage = toolResult.ScriptLanguage,
                ScriptTemplate = toolResult.ScriptTemplate,
                ParameterSchemaJson = toolResult.ParameterSchema,
                RawQuestion = request.Question,
                TunedQuestion = tunedQuestion
            });

            if (renderResult.Success && !string.IsNullOrWhiteSpace(renderResult.RenderedScript))
            {
                if (TryFindDangerousCommand(request.Environment, renderResult.RenderedScript, out var blockedToken))
                {
                    _logger.LogWarning(
                        "Template script blocked. QueryCode={QueryCode}, Token={Token}",
                        toolResult.QueryCode, blockedToken);
                    await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "blocked"), cancellationToken);
                    return CreateStoppedResponse(
                        request, tunedQuestion,
                        $"{BlockedStateChangingRequestMessage} Token={blockedToken}",
                        tuneModel, planModel, generateModel);
                }

                await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "template-hit"), cancellationToken);
                var templateResp = await BuildExecutionResponseAsync(
                    request, tunedQuestion, renderResult.RenderedScript,
                    toolResult.ScriptLanguage, "EXECUTION_READY", "TEMPLATE",
                    tuneModel, planModel, generateModel, cancellationToken, prog);
                templateResp.Plan.GeneratorMode = "TEMPLATE_OR_LLM";
                templateResp.Plan.TemplateHit = true;
                templateResp.Plan.QueryCode = toolResult.QueryCode;
                if (templateResp.Script is not null)
                {
                    templateResp.Script.Source = "TEMPLATE";
                    templateResp.Script.Parameters = renderResult.BoundParameters.Count > 0
                        ? new Dictionary<string, object?>(renderResult.BoundParameters, StringComparer.OrdinalIgnoreCase)
                        : null;
                }
                return templateResp;
            }

            _logger.LogWarning(
                "Template render failed for QueryCode={QueryCode}, ErrorCode={ErrorCode}. Falling back to LLM.",
                toolResult.QueryCode, renderResult.ErrorCode);
        }
        else
        {
            _logger.LogInformation(
                "Template miss. SelectionMethod={SelectionMethod}. Falling back to LLM.",
                toolResult.SelectionMethod);
        }

        // ── LLM fallback ──────────────────────────────────────────────────────
        var generateClient = ResolveClient(generateModel.Provider);
        var generatePrompt = BuildWindowsGeneratePrompt(tunedQuestion);
        var generatedScriptRaw = await generateClient.GenerateAsync(
            generatePrompt, tunedQuestion, request.Environment, generateModel.ModelKey, cancellationToken,
            apiKey: generateModel.ApiKeyEncrypted);

        var sanitizedScript = SanitizeWindowsGeneratedScript(generatedScriptRaw);
        if (!LooksLikePowerShell(sanitizedScript))
        {
            var retryPrompt =
                $"{generatePrompt}{Environment.NewLine}{Environment.NewLine}Your previous output contained non-script text. Return ONLY raw PowerShell code.";
            var retryScriptRaw = await generateClient.GenerateAsync(
                retryPrompt, tunedQuestion, request.Environment, generateModel.ModelKey, cancellationToken,
                apiKey: generateModel.ApiKeyEncrypted);
            sanitizedScript = SanitizeWindowsGeneratedScript(retryScriptRaw);
        }

        sanitizedScript = EnsureWindowsScriptContract(sanitizedScript, tunedQuestion);

        if (TryFindDangerousCommand(request.Environment, sanitizedScript, out var llmBlockedToken))
        {
            await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "blocked"), cancellationToken);
            return CreateStoppedResponse(
                request, tunedQuestion,
                $"{BlockedStateChangingRequestMessage} Token={llmBlockedToken}",
                tuneModel, planModel, generateModel);
        }

        if (string.IsNullOrWhiteSpace(sanitizedScript))
        {
            _logger.LogWarning("Windows PS generation returned empty script. TunedQuestion={TunedQuestion}", tunedQuestion);
            await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "empty-script"), cancellationToken);
            return CreateStoppedResponse(
                request, tunedQuestion,
                "STOPPED: LLM_GENERATE returned empty script — cannot execute.",
                tuneModel, planModel, generateModel);
        }

        await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE"), cancellationToken);
        var llmResp = await BuildExecutionResponseAsync(
            request, tunedQuestion, sanitizedScript,
            "PS", "EXECUTION_READY", "LLM_GENERATE",
            tuneModel, planModel, generateModel, cancellationToken, prog);
        llmResp.Plan.GeneratorMode = "TEMPLATE_OR_LLM";
        if (llmResp.Script is not null)
            llmResp.Script.Source = "LLM";
        return llmResp;
    }

    private static string BuildSqlGeneratePrompt(string tunedQuestion, string environment = "SqlServer_Live")
    {
        return PromptTemplates.ScriptGenerateSql
            .Replace("{{$environmentTag}}", environment ?? "SqlServer_Live", StringComparison.Ordinal)
            .Replace("{{$question}}", tunedQuestion ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{$planJson}}", string.Empty, StringComparison.Ordinal);
    }

    private static string BuildWindowsGeneratePrompt(string tunedQuestion)
    {
        return PromptTemplates.WindowsGenerate
            .Replace("{{$question}}", tunedQuestion ?? string.Empty, StringComparison.Ordinal);
    }

    private static string BuildContractRepairPrompt(
        string originalPrompt, string failedScript, string violation, TopicClassification topic,
        string repairReason = "WRONG_TOPIC")
    {
        return $"""
            {originalPrompt}

            ========================================================
            CONTRACT VIOLATION — YOU MUST FIX THIS
            ========================================================
            REPAIR REASON: {repairReason}
            TOPIC: {topic.TopicName}
            VIOLATION: {violation}

            FAILED SCRIPT:
            {failedScript}

            INSTRUCTIONS:
            - Regenerate the script using ONLY the allowed sources for topic [{topic.TopicName}].
            - Include ALL required output columns listed in the TOPIC CONSTRAINTS section.
            - Do NOT use sources from other topics (e.g. do NOT use dm_os_wait_stats for a BlockingChains query).
            - Fix the minimum necessary — keep format rules and output column names unchanged.
            - Do NOT add any dangerous operations (INSERT, UPDATE, DELETE, DROP, ALTER, etc.).
            - Return CODE ONLY. No markdown. No explanations.
            """;
    }

    private static string SanitizeGeneratedScript(string generatedScriptRaw)
    {
        var script = generatedScriptRaw ?? string.Empty;
        if (string.IsNullOrWhiteSpace(script))
            return string.Empty;

        var fencedCode = Regex.Match(
            script,
            @"```(?:[^\r\n`]*)\r?\n(?<code>[\s\S]*?)```",
            RegexOptions.IgnoreCase);
        if (fencedCode.Success)
            script = fencedCode.Groups["code"].Value;

        script = script.Replace("```", string.Empty, StringComparison.Ordinal);

        var normalized = script.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        var index = 0;
        while (index < lines.Length)
        {
            var trimmed = lines[index].Trim();
            if (trimmed.Length == 0)
            {
                index++;
                continue;
            }

            if (trimmed.StartsWith("PLAN:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("EXPLANATION:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("STEPS:", StringComparison.OrdinalIgnoreCase))
            {
                index++;
                continue;
            }

            break;
        }

        return string.Join(Environment.NewLine, lines.Skip(index)).Trim();
    }

    private static string SanitizeWindowsGeneratedScript(string generatedScriptRaw)
    {
        var script = SanitizeGeneratedScript(generatedScriptRaw);
        if (string.IsNullOrWhiteSpace(script))
            return string.Empty;

        var normalized = script.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        var firstScriptLine = -1;

        for (var i = 0; i < lines.Length; i++)
        {
            if (LooksLikePowerShellLine(lines[i]))
            {
                firstScriptLine = i;
                break;
            }
        }

        if (firstScriptLine > 0)
            lines = lines.Skip(firstScriptLine).ToArray();

        var cleaned = string.Join(Environment.NewLine, lines).Trim();
        if (ContainsWindowsPreambleMarkers(cleaned))
            return string.Empty;

        return cleaned;
    }

    private static string NormalizeGeneratedScript(string environment, string generatedScriptRaw)
    {
        var script = (generatedScriptRaw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(script))
            return string.Empty;

        if (EnvironmentRules.IsSqlServer(environment))
        {
            script = Regex.Replace(script, @"^\s*SET\s+NOCOUNT\s+ON;\s*", string.Empty, RegexOptions.IgnoreCase);
            script = CorrectSqlAntiPatterns(script);
        }

        return script.Trim();
    }

    /// <summary>
    /// Corrects known LLM anti-patterns in generated SQL without altering query intent.
    /// Currently fixes: DB_NAME() used as a column value when sys.databases is in the FROM clause.
    /// DB_NAME() always returns the connection database; correct source is the aliased .name column.
    /// </summary>
    private static string CorrectSqlAntiPatterns(string script)
    {
        // If sys.databases is in FROM, DB_NAME() in SELECT is wrong — it always returns the
        // connection database (master), not the iterated row's database name.
        var aliasMatch = SysDatabasesAliasPattern.Match(script);
        if (!aliasMatch.Success || !DbNameColumnPattern.IsMatch(script))
            return script;

        // Determine alias: e.g. "FROM sys.databases d" → "d"; no alias → use bare "name"
        var alias = aliasMatch.Groups[1].Success ? aliasMatch.Groups[1].Value : string.Empty;

        script = DbNameColumnPattern.Replace(script, m =>
        {
            // Preserve the original AS [alias] clause if present
            var columnAlias = m.Groups[1].Value; // e.g. " AS [DatabaseName]"
            var nameExpr = string.IsNullOrEmpty(alias) ? "name" : $"{alias}.name";
            return $"{nameExpr}{columnAlias}";
        });

        return script;
    }

    private static bool LooksLikePowerShellLine(string line)
    {
        var text = (line ?? string.Empty).TrimStart();
        if (text.Length == 0)
            return false;

        return text.StartsWith("Get-", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("Select-Object", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("$", StringComparison.Ordinal)
               || text.StartsWith("param(", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("function", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("foreach", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("try", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("catch", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikePowerShell(string script)
    {
        if (string.IsNullOrWhiteSpace(script))
            return false;

        return script.Contains("Get-", StringComparison.OrdinalIgnoreCase) ||
               script.Contains("Select-Object", StringComparison.OrdinalIgnoreCase) ||
               script.Contains("$", StringComparison.Ordinal) ||
               script.Contains("param(", StringComparison.OrdinalIgnoreCase) ||
               script.Contains("function", StringComparison.OrdinalIgnoreCase) ||
               script.Contains("foreach", StringComparison.OrdinalIgnoreCase) ||
               script.Contains("try", StringComparison.OrdinalIgnoreCase) ||
               script.Contains("catch", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsWindowsPreambleMarkers(string script)
    {
        if (string.IsNullOrWhiteSpace(script))
            return false;

        var lines = script.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("PLAN:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("STEPS:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("EXPLANATION:", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string EnsureWindowsScriptContract(string script, string tunedQuestion)
    {
        var candidate = (script ?? string.Empty).Trim();
        if (!LooksLikePowerShell(candidate))
            return BuildFallbackWindowsScript(tunedQuestion);

        var hasTargetServerParam = Regex.IsMatch(
            candidate,
            @"param\s*\(\s*\[string\]\s*\$TargetServer",
            RegexOptions.IgnoreCase);
        var outputsResult = Regex.IsMatch(candidate, @"\$\s*Result\b", RegexOptions.IgnoreCase);
        var hasMandatoryFields = candidate.Contains("ServerName", StringComparison.OrdinalIgnoreCase)
                                 && candidate.Contains("CapturedAt", StringComparison.OrdinalIgnoreCase)
                                 && candidate.Contains("Status", StringComparison.OrdinalIgnoreCase)
                                 && candidate.Contains("ErrorMessage", StringComparison.OrdinalIgnoreCase);

        if (!hasTargetServerParam || !outputsResult || !hasMandatoryFields)
            return BuildFallbackWindowsScript(tunedQuestion);

        return candidate;
    }

    private static bool LooksLikeSqlScript(string script)
    {
        if (string.IsNullOrWhiteSpace(script))
            return false;

        var normalized = script.TrimStart();
        return Regex.IsMatch(normalized, @"^(DECLARE|WITH|SELECT)\b", RegexOptions.IgnoreCase);
    }

    private static bool HasSqlMandatoryOutputColumns(string script)
    {
        if (string.IsNullOrWhiteSpace(script))
            return false;

        // Only require the two columns that are always correct: ServerName and CapturedAt.
        // DatabaseName is NOT checked here because correct scripts use d.name AS [DatabaseName]
        // (not DB_NAME()) when iterating sys.databases — requiring DB_NAME() would reject them.
        return Regex.IsMatch(script, @"@@SERVERNAME\s+AS\s+\[ServerName\]", RegexOptions.IgnoreCase)
               && Regex.IsMatch(script, @"GETDATE\s*\(\s*\)\s+AS\s+\[CapturedAt\]", RegexOptions.IgnoreCase);
    }

    private static bool IsSqlShapeSafe(string script)
    {
        if (string.IsNullOrWhiteSpace(script))
            return false;

        if (Regex.IsMatch(script, @"(?im)^\s*USE\s+\S+", RegexOptions.IgnoreCase))
            return false;

        if (Regex.IsMatch(script, @"(?im)\bSELECT\s+\*", RegexOptions.IgnoreCase))
            return false;

        if (Regex.IsMatch(script, @"\bTOP\s*\(", RegexOptions.IgnoreCase)
            && !Regex.IsMatch(script, @"\bORDER\s+BY\b", RegexOptions.IgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static bool IsClearlyDangerousSqlRequest(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return Regex.IsMatch(
                   text,
                   @"\b(insert\s+into|update\s+\S+\s+set\b|delete\s+from|merge\s+into|truncate\s+table|drop\s+(table|database|index|view|procedure|proc|login|user|schema|role|function|trigger)|alter\s+(table|database|index|view|procedure|proc|login|user|schema|role|function|trigger|event\s+session)|create\s+(table|database|index|view|procedure|proc|login|user|schema|role|function|trigger)|grant\s+\S+\s+to|revoke\s+\S+\s+from|deny\s+\S+\s+to|kill\s+\d+|reconfigure)\b",
                   RegexOptions.IgnoreCase)
               || Regex.IsMatch(text, @"\b(backup|restore)\s+(database|log)\b", RegexOptions.IgnoreCase)
               || Regex.IsMatch(
                   text,
                   @"\b(xp_cmdshell|sp_OACreate|OPENROWSET|OPENDATASOURCE)\b",
                   RegexOptions.IgnoreCase)
               || Regex.IsMatch(text, @"\bsp_(add|update|delete)_job\b", RegexOptions.IgnoreCase)
               || Regex.IsMatch(
                   text,
                   @"\b(alter\s+event\s+session|xevent\s+(start|stop)|event\s+session\s+(start|stop))\b",
                   RegexOptions.IgnoreCase);
    }

    private static string ResolveSqlTunedQuestion(string tunedLineLeftText, string rawQuestion, string environment)
    {
        var candidate = (tunedLineLeftText ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(candidate)
            || candidate.StartsWith("MISMATCH:", StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith("GENERAL_REFUSAL:", StringComparison.OrdinalIgnoreCase))
        {
            candidate = rawQuestion ?? string.Empty;
        }

        // If the LLM accidentally returned SQL instead of a natural-language sentence, fall back to raw question.
        if (LooksLikeSqlScript(candidate))
            candidate = (rawQuestion ?? string.Empty).Trim();

        var refined = TunedQuestionRefiner.Refine(candidate, environment);
        if (string.IsNullOrWhiteSpace(refined))
            refined = (rawQuestion ?? string.Empty).Trim();
        return refined;
    }

    private static string BuildFallbackSqlScript(string tunedQuestion)
    {
        var question = tunedQuestion ?? string.Empty;

        // Specific helpers that inject dynamic values (day counts, GB thresholds, etc.)
        if (MentionsFailedJobs(question))
            return BuildFailedJobsSqlScript(question);

        if (question.Contains("backup history", StringComparison.OrdinalIgnoreCase)
            || question.Contains("backup", StringComparison.OrdinalIgnoreCase) && question.Contains("history", StringComparison.OrdinalIgnoreCase))
        {
            return BuildBackupHistorySqlScript(question);
        }

        if (MentionsDatabaseSizeComparison(question))
            return BuildDatabaseSizeSqlScript(question);

        // Delegate all other patterns to MockLlmBehavior's comprehensive library.
        var script = MockLlmBehavior.BuildGenerateScript("SqlServer_Live", question);
        return string.IsNullOrWhiteSpace(script) ? BuildDefaultSqlScript(question) : script;
    }

    private static bool MentionsDatabaseSizeComparison(string question) =>
        ContainsAny(question, "database", "databases") &&
        question.Contains("gb", StringComparison.OrdinalIgnoreCase) &&
        ContainsAny(question, "larger than", "greater than", "bigger than", "more than", "> ", ">", "exceed", "over ");

    private static int ResolveRequestedGb(string question)
    {
        var match = Regex.Match(question ?? string.Empty, @"(\d+)\s*gb", RegexOptions.IgnoreCase);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var gb))
            return 10;
        return Math.Max(1, gb);
    }

    private static string BuildDatabaseSizeSqlScript(string question)
    {
        var gb = ResolveRequestedGb(question);
        return $"""
WITH db_size AS (
    SELECT
        mf.database_id,
        SUM(CAST(mf.size AS bigint) * 8192) AS TotalSizeBytes
    FROM sys.master_files AS mf
    GROUP BY mf.database_id
)
SELECT TOP (50)
    @@SERVERNAME AS [ServerName],
    GETDATE() AS [CapturedAt],
    d.name AS [DatabaseName],
    CAST(ds.TotalSizeBytes / (1024.0 * 1024 * 1024) AS decimal(18, 2)) AS [SizeGB]
FROM sys.databases AS d
INNER JOIN db_size AS ds ON d.database_id = ds.database_id
WHERE ds.TotalSizeBytes > CAST({gb} AS bigint) * 1024 * 1024 * 1024
ORDER BY ds.TotalSizeBytes DESC;
""";
    }

    private static string BuildFailedJobsSqlScript(string question)
    {
        var topClause = ShouldLimitResults(question) ? "TOP (50) " : string.Empty;
        return $"""
WITH failed_jobs AS (
    SELECT {topClause}
        j.name AS [JobName],
        msdb.dbo.agent_datetime(h.run_date, h.run_time) AS [FailedAt],
        h.step_id AS [StepId],
        h.step_name AS [StepName],
        h.message AS [Message]
    FROM msdb.dbo.sysjobhistory AS h
    INNER JOIN msdb.dbo.sysjobs AS j ON h.job_id = j.job_id
    WHERE h.run_status = 0
      AND h.step_id > 0
    ORDER BY h.run_date DESC, h.run_time DESC
)
SELECT
    @@SERVERNAME AS [ServerName],
    GETDATE() AS [CapturedAt],
    f.[JobName],
    f.[FailedAt],
    f.[StepId],
    f.[StepName],
    f.[Message]
FROM failed_jobs AS f
ORDER BY f.[FailedAt] DESC;
""";
    }

    private static string BuildBackupHistorySqlScript(string question)
    {
        var days = ResolveRequestedDays(question);
        var topClause = ShouldLimitResults(question) ? "TOP (50) " : string.Empty;
        return $"""
DECLARE @Days int = {days};
WITH backup_history AS (
    SELECT {topClause}
        bs.database_name AS [BackupDatabaseName],
        bs.type AS [BackupType],
        bs.backup_start_date AS [BackupStartDate],
        bs.backup_finish_date AS [BackupFinishDate],
        bs.backup_size AS [BackupSizeBytes],
        bmf.physical_device_name AS [PhysicalDeviceName]
    FROM msdb.dbo.backupset AS bs
    LEFT JOIN msdb.dbo.backupmediafamily AS bmf
        ON bs.media_set_id = bmf.media_set_id
    WHERE bs.backup_finish_date >= DATEADD(DAY, -@Days, GETDATE())
    ORDER BY bs.backup_finish_date DESC
)
SELECT
    @@SERVERNAME AS [ServerName],
    GETDATE() AS [CapturedAt],
    bh.[BackupDatabaseName],
    bh.[BackupType],
    bh.[BackupStartDate],
    bh.[BackupFinishDate],
    bh.[BackupSizeBytes],
    bh.[PhysicalDeviceName]
FROM backup_history AS bh
ORDER BY bh.[BackupFinishDate] DESC;
""";
    }

    private static string BuildDefaultSqlScript(string question)
    {
        var topClause = ShouldLimitResults(question) ? "TOP (50) " : string.Empty;
        return $"""
SELECT {topClause}
    @@SERVERNAME AS [ServerName],
    GETDATE() AS [CapturedAt],
    d.name AS [DatabaseName],
    d.state_desc AS [State],
    d.recovery_model_desc AS [RecoveryModel]
FROM sys.databases AS d
ORDER BY d.name;
""";
    }

    private static bool ShouldLimitResults(string question) =>
        !Regex.IsMatch(question ?? string.Empty, @"\ball\b", RegexOptions.IgnoreCase);

    private static int ResolveRequestedDays(string question)
    {
        var match = Regex.Match(question ?? string.Empty, @"\b(?:last|past|within)\s+(\d+)\s+day", RegexOptions.IgnoreCase);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var days))
            return 7;
        return Math.Clamp(days, 1, 365);
    }

    private static bool IsClearlyDangerousWindowsRequest(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        // Explicitly allow common read-only inventory/diagnostics cmdlets.
        if (Regex.IsMatch(text, @"\b(get-volume|get-ciminstance|get-computerinfo|get-service|get-process|get-winevent|get-eventlog)\b", RegexOptions.IgnoreCase)
            && !Regex.IsMatch(text, @"\b(restart|reboot|shutdown|stop|kill|remove|format|disable|enable)\b", RegexOptions.IgnoreCase))
        {
            return false;
        }

        return Regex.IsMatch(text, @"\b(restart|reboot|shutdown)\s+(server|computer|machine|host)\b", RegexOptions.IgnoreCase)
               || Regex.IsMatch(text, @"\b(start|stop|restart)\s+service\b", RegexOptions.IgnoreCase)
               || Regex.IsMatch(text, @"\b(stop|kill)\s+(process|service)\b", RegexOptions.IgnoreCase)
               || Regex.IsMatch(
                   text,
                   @"\b(remove-item|set-itemproperty|new-itemproperty|restart-computer|stop-computer|start-service|stop-service|restart-service|stop-process|taskkill|format-volume|clear-eventlog|disable-netadapter|new-netfirewallrule|set-netfirewallrule|remove-netfirewallrule|install-\w+|uninstall-\w+)\b",
                   RegexOptions.IgnoreCase);
    }

    private static string ResolveWindowsTunedQuestion(string tunedLineLeftText, string rawQuestion, string environment)
    {
        var candidate = (tunedLineLeftText ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(candidate) ||
            candidate.StartsWith("MISMATCH:", StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith("GENERAL_REFUSAL:", StringComparison.OrdinalIgnoreCase))
        {
            candidate = rawQuestion ?? string.Empty;
        }

        // If the LLM accidentally returned PowerShell code instead of a natural-language sentence, fall back to raw question.
        if (LooksLikePowerShell(candidate))
            candidate = (rawQuestion ?? string.Empty).Trim();

        var refined = TunedQuestionRefiner.Refine(candidate, environment);
        if (string.IsNullOrWhiteSpace(refined))
            refined = (rawQuestion ?? string.Empty).Trim();
        return refined;
    }

    private static string BuildFallbackWindowsScript(string tunedQuestion)
    {
        var question = tunedQuestion ?? string.Empty;

        // Disk/drive: keep inline because of ExtractDriveLetter logic.
        if (Regex.IsMatch(question, @"\b(drive|disk|volume)\b", RegexOptions.IgnoreCase))
        {
            var driveLetter = ExtractDriveLetter(question);
            var driveFilter = string.IsNullOrWhiteSpace(driveLetter)
                ? "$true"
                : $"$_.DeviceID -like '{driveLetter}*'";

            return $$"""
param([string]$TargetServer)
$Result = @()
try {
    if ([string]::IsNullOrWhiteSpace($TargetServer)) { $TargetServer = $env:COMPUTERNAME }
    $items = Get-CimInstance Win32_LogicalDisk -ErrorAction Stop | Where-Object { {{driveFilter}} }
    foreach ($item in $items) {
        $Result += [pscustomobject]@{
            ServerName = $TargetServer
            CapturedAt = Get-Date
            Status = 'OK'
            ErrorMessage = $null
            DeviceID = $item.DeviceID
            VolumeName = $item.VolumeName
            Size = $item.Size
            FreeSpace = $item.FreeSpace
        }
    }
}
catch {
    $Result += [pscustomobject]@{
        ServerName = if ([string]::IsNullOrWhiteSpace($TargetServer)) { $env:COMPUTERNAME } else { $TargetServer }
        CapturedAt = Get-Date
        Status = 'ERROR'
        ErrorMessage = $_.Exception.Message
    }
}
$Result
""";
        }

        if (Regex.IsMatch(question, @"\b(service|services)\b", RegexOptions.IgnoreCase) &&
            Regex.IsMatch(question, @"\bstopped\b", RegexOptions.IgnoreCase))
        {
            return """
param([string]$TargetServer)
$Result = @()
try {
    if ([string]::IsNullOrWhiteSpace($TargetServer)) { $TargetServer = $env:COMPUTERNAME }
    $items = Get-Service -ErrorAction Stop | Where-Object { $_.Status -eq 'Stopped' }
    foreach ($item in $items) {
        $Result += [pscustomobject]@{
            ServerName = $TargetServer
            CapturedAt = Get-Date
            Status = 'OK'
            ErrorMessage = $null
            ServiceName = $item.Name
            DisplayName = $item.DisplayName
            ServiceStatus = [string]$item.Status
        }
    }
}
catch {
    $Result += [pscustomobject]@{
        ServerName = if ([string]::IsNullOrWhiteSpace($TargetServer)) { $env:COMPUTERNAME } else { $TargetServer }
        CapturedAt = Get-Date
        Status = 'ERROR'
        ErrorMessage = $_.Exception.Message
    }
}
$Result
""";
        }

        // Delegate all other patterns to MockLlmBehavior's comprehensive library.
        var script = MockLlmBehavior.BuildGenerateScript("Windows_Live", question);
        if (!string.IsNullOrWhiteSpace(script))
            return script;

        return """
param([string]$TargetServer)
$Result = @()
try {
    if ([string]::IsNullOrWhiteSpace($TargetServer)) { $TargetServer = $env:COMPUTERNAME }
    $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
    $Result += [pscustomobject]@{
        ServerName = $TargetServer
        CapturedAt = Get-Date
        Status = 'OK'
        ErrorMessage = $null
        Caption = $os.Caption
        Version = $os.Version
        BuildNumber = $os.BuildNumber
    }
}
catch {
    $Result += [pscustomobject]@{
        ServerName = if ([string]::IsNullOrWhiteSpace($TargetServer)) { $env:COMPUTERNAME } else { $TargetServer }
        CapturedAt = Get-Date
        Status = 'ERROR'
        ErrorMessage = $_.Exception.Message
    }
}
$Result
""";
    }

    private static string? ExtractDriveLetter(string question)
    {
        if (string.IsNullOrWhiteSpace(question))
            return null;

        var likeMatch = Regex.Match(question, @"\blike\s+([A-Z]):?\b", RegexOptions.IgnoreCase);
        if (likeMatch.Success)
            return $"{likeMatch.Groups[1].Value.ToUpperInvariant()}:";

        var namedMatch = Regex.Match(question, @"\b(?:drive|volume|disk)\s+(?:name|letter)?\s*([A-Z]):?\b", RegexOptions.IgnoreCase);
        if (namedMatch.Success)
            return $"{namedMatch.Groups[1].Value.ToUpperInvariant()}:";

        return null;
    }

    private static bool TryFindDangerousCommand(string environment, string script, out string blockedToken)
    {
        blockedToken = string.Empty;
        if (string.IsNullOrWhiteSpace(script))
            return false;

        // History environments always generate T-SQL, so use SQL safety checks even for Windows_History.
        if (EnvironmentRules.IsSqlServer(environment) || EnvironmentRules.IsHistory(environment))
        {
            // Strip SQL comments so tokens inside comments don't cause false blocks
            var commentFree = ScriptSafetyScanner.StripSqlComments(script);

            // 1. Blanket-blocked statements (no safe variant)
            var blockedStatement = Regex.Match(
                commentFree,
                @"(?im)^\s*(UPDATE|DELETE|MERGE|TRUNCATE|GRANT|REVOKE|DENY|KILL|RECONFIGURE|BACKUP|RESTORE)\b");
            if (blockedStatement.Success)
            {
                blockedToken = blockedStatement.Groups[1].Value.ToUpperInvariant();
                return true;
            }

            // 2. INSERT — allow @tableVar / #temp, block real tables
            if (Regex.IsMatch(commentFree, @"(?im)^\s*INSERT\s+INTO\s+(?![@#])\w") ||
                Regex.IsMatch(commentFree, @"(?im)^\s*INSERT\s+(?!INTO\b)(?![@#])\w"))
            {
                blockedToken = "INSERT_INTO_TABLE";
                return true;
            }

            // 3. DROP — allow DROP TABLE #temp, block everything else
            if (Regex.IsMatch(commentFree, @"(?im)\bDROP\b") &&
                !AllInlineOccurrencesAllowed(commentFree,
                    @"(?im)\bDROP\b",
                    @"(?im)\bDROP\s+TABLE\s+(IF\s+EXISTS\s+)?#\w"))
            {
                blockedToken = "DROP";
                return true;
            }

            // 4. CREATE — allow CREATE TABLE #temp and CREATE INDEX ON #temp
            if (Regex.IsMatch(commentFree, @"(?im)\bCREATE\b") &&
                !AllInlineOccurrencesAllowed(commentFree,
                    @"(?im)\bCREATE\b",
                    @"(?im)\bCREATE\s+TABLE\s+#\w",
                    @"(?im)\bCREATE\s+(UNIQUE\s+)?(NONCLUSTERED\s+|CLUSTERED\s+)?INDEX\b[^;]*\bON\s+#\w"))
            {
                blockedToken = "CREATE";
                return true;
            }

            // 5. ALTER — allow ALTER INDEX ON #temp
            if (Regex.IsMatch(commentFree, @"(?im)\bALTER\b") &&
                !AllInlineOccurrencesAllowed(commentFree,
                    @"(?im)\bALTER\b",
                    @"(?im)\bALTER\s+INDEX\b[^;]*\bON\s+#\w"))
            {
                blockedToken = "ALTER";
                return true;
            }

            // 6. Blocked procedure tokens
            foreach (var token in SqlBlockedProcedureTokens)
            {
                if (!Regex.IsMatch(commentFree, $@"\b{Regex.Escape(token)}\b", RegexOptions.IgnoreCase))
                    continue;

                blockedToken = token;
                return true;
            }

            return false;
        }

        if (!EnvironmentRules.IsWindows(environment))
            return false;

        foreach (var command in WindowsBlockedCommands)
        {
            if (!Regex.IsMatch(script, $@"(?im)^\s*{Regex.Escape(command)}\b", RegexOptions.IgnoreCase))
                continue;

            blockedToken = command;
            return true;
        }

        if (Regex.IsMatch(script, @"(?im)^\s*(Install-[A-Za-z0-9_-]+|Uninstall-[A-Za-z0-9_-]+)\b", RegexOptions.IgnoreCase))
        {
            blockedToken = "Install/Uninstall";
            return true;
        }

        if (Regex.IsMatch(script, @"(?im)^\s*(New-LocalUser|Remove-LocalUser|Set-LocalUser|Add-LocalGroupMember|Remove-LocalGroupMember|net\s+user)\b", RegexOptions.IgnoreCase))
        {
            blockedToken = "LocalUserMutation";
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns true when every occurrence of <paramref name="anyPattern"/> in <paramref name="script"/>
    /// is covered by at least one of the <paramref name="allowedPatterns"/>.
    /// </summary>
    private static bool AllInlineOccurrencesAllowed(string script, string anyPattern, params string[] allowedPatterns)
    {
        foreach (Match hit in Regex.Matches(script, anyPattern))
        {
            var remaining = script[hit.Index..];
            var covered = false;
            foreach (var allowed in allowedPatterns)
            {
                var m = Regex.Match(remaining, allowed);
                if (m.Success && m.Index == 0)
                {
                    covered = true;
                    break;
                }
            }
            if (!covered)
                return false;
        }
        return true;
    }

    private static bool ContainsAny(string value, params string[] candidates)
    {
        return candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MentionsFailedJobs(string text) =>
        ContainsAny(text, "failed jobs", "failed job", "job failures", "jobs failed");

    /// <summary>
    /// Resolves SQL Server tokens to port-aware connection tokens.
    /// "CTS02#ADMIN" → "CTS02,1432#ADMIN" using port from Get_UserSQLServer.
    /// </summary>
    private async Task<string[]> ResolveSqlPortsAsync(string[] targets, string? bearerToken, CancellationToken ct)
    {
        if (targets.Length == 0 || string.IsNullOrWhiteSpace(bearerToken))
            return targets;

        try
        {
            var allSql = await userServerRepository.GetSqlServersAsync(bearerToken, ct);
            var lookup = allSql.ToDictionary(s => s.Token, s => s, StringComparer.OrdinalIgnoreCase);
            return targets
                .Select(t =>
                {
                    if (!lookup.TryGetValue(t, out var entry)) return t;
                    // Build connection token with port embedded
                    var server = entry.Port > 0 && entry.Port != 1433
                        ? $"{entry.ServerName},{entry.Port}"
                        : entry.ServerName;
                    var isDefault = string.IsNullOrWhiteSpace(entry.InstanceName)
                                 || entry.InstanceName.Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase);
                    return !isDefault ? $"{server}#{entry.InstanceName}" : server;
                })
                .ToArray();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to resolve SQL ports for Ask pipeline — using raw tokens.");
            return targets;
        }
    }

    private ILLMClient ResolveClient(string provider)
    {
        if (_clientsByProvider.TryGetValue(provider, out var client))
            return client;

        throw new InvalidOperationException($"No ILLMClient implementation registered for provider '{provider}'.");
    }

    private static (string LeftText, string? QueryCodeEcho) ParseTunedLine(string tunedLine)
    {
        var raw = (tunedLine ?? string.Empty).Trim();
        if (raw.Length == 0)
            return (string.Empty, null);

        var separatorIndex = raw.IndexOf("||", StringComparison.Ordinal);
        if (separatorIndex < 0)
            return (raw, null);

        var leftText = raw[..separatorIndex].Trim();
        var queryCodeEcho = raw[(separatorIndex + 2)..].Trim();
        return (leftText, queryCodeEcho.Length == 0 ? null : queryCodeEcho);
    }

    private static bool IsStopped(string leftText)
    {
        return leftText.StartsWith("MISMATCH:", StringComparison.OrdinalIgnoreCase)
               || leftText.StartsWith("GENERAL_REFUSAL:", StringComparison.OrdinalIgnoreCase)
               || leftText.StartsWith("BLOCKED:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryRecoverFalseMismatch(
        string leftText,
        string rawQuestion,
        string environment,
        out string recoveredLeftText)
    {
        recoveredLeftText = leftText;
        if (string.IsNullOrWhiteSpace(rawQuestion))
            return false;

        // Recover MISMATCH, GENERAL_REFUSAL, or BLOCKED when the user is already in
        // a matching environment and the question contains domain-relevant keywords.
        // The LLM tuner is often too aggressive for valid DBA queries like
        // "List all configuration, where maxdoc" — perfectly valid read-only SQL.
        // BLOCKED recovery: the LLM sees "configuration" and thinks "sp_configure" → blocks it,
        // but querying sys.configurations is read-only and safe.
        var isStopped = leftText.StartsWith("MISMATCH:", StringComparison.OrdinalIgnoreCase)
                        || leftText.StartsWith("GENERAL_REFUSAL:", StringComparison.OrdinalIgnoreCase)
                        || leftText.StartsWith("BLOCKED:", StringComparison.OrdinalIgnoreCase);

        if (isStopped
            && EnvironmentRules.IsSqlServer(environment)
            && ContainsAny(rawQuestion, SqlRescueKeywords))
        {
            var fallbackLine = MockLlmBehavior.BuildTuneLine(rawQuestion, environment, string.Empty);
            var (fallbackLeft, _) = ParseTunedLine(fallbackLine);
            if (!IsStopped(fallbackLeft))
            {
                recoveredLeftText = fallbackLeft;
                return true;
            }
        }

        if (isStopped
            && EnvironmentRules.IsWindows(environment)
            && ContainsAny(rawQuestion, WindowsRescueKeywords))
        {
            var fallbackLine = MockLlmBehavior.BuildTuneLine(rawQuestion, environment, string.Empty);
            var (fallbackLeft, _) = ParseTunedLine(fallbackLine);
            if (!IsStopped(fallbackLeft))
            {
                recoveredLeftText = fallbackLeft;
                return true;
            }
        }

        return false;
    }

    // ── SAMPLE EXECUTION ──────────────────────────────────────────────────

    // ── History sample helpers ──────────────────────────────────────────

    /// <summary>
    /// Normalizes selectedServers for History mode: strips port suffix, trims, deduplicates.
    /// "CTS02\ADMIN,1432" → "CTS02\ADMIN"
    /// "CTS02\FINANCE,1431" → "CTS02\FINANCE"
    /// </summary>
    internal static List<string> NormalizeHistorySelectedServers(IEnumerable<string>? servers)
    {
        if (servers is null) return [];

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in servers)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var normalized = raw.Trim();
            var comma = normalized.IndexOf(',');
            if (comma > 0) normalized = normalized[..comma].TrimEnd();

            if (!string.IsNullOrWhiteSpace(normalized) && seen.Add(normalized))
                result.Add(normalized);
        }

        return result;
    }

    /// <summary>Escapes a value for safe inclusion in a SQL N'...' literal by doubling single quotes.</summary>
    internal static string EscapeSqlLiteral(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    /// <summary>Builds a SQL IN-list literal: N'CTS02\ADMIN', N'CTS02\FINANCE'</summary>
    internal static string BuildSqlInList(IReadOnlyList<string> values)
    {
        if (values.Count == 0) return "NULL";
        return string.Join(", ", values.Select(v => $"N'{EscapeSqlLiteral(v)}'"));
    }

    /// <summary>
    /// Full transformation pipeline for History sample scripts.
    /// 1. Removes GO batch separators
    /// 2. Replaces token comments (/*__SQLSERVER_FILTER__*/ etc.) with IN-list filters
    /// 3. Replaces legacy @Server WHERE predicates with IN-list filters
    /// 4. Removes DECLARE @Server
    /// 5. Normalizes/injects @FromUtc, @ToUtc, @Top declarations
    /// 6. Ensures missing declarations are injected
    /// </summary>
    internal static string TransformHistorySampleSql(
        string rawSql,
        string environment,
        IReadOnlyList<string> normalizedServers,
        string? fromUtcIso,
        string? toUtcIso,
        int top = 1000,
        MetricWhitelistEntry? metric = null)
    {
        var result = rawSql;

        // ── Step 0: Replace metric tokens from whitelist entry ───────────
        if (metric is not null)
        {
            var safeMetricName = EscapeSqlLiteral(metric.MetricLabel);
            var safeGroupName = EscapeSqlLiteral(metric.MetricGroup);

            result = result.Replace("/*__METRIC_SELECT__*/", metric.MetricSelectSql, StringComparison.Ordinal);
            result = result.Replace("/*__DETAIL_SELECT__*/", metric.DetailSelectSql, StringComparison.Ordinal);
            result = result.Replace("/*__METRIC_NAME__*/", $"N'{safeMetricName}'", StringComparison.Ordinal);
            result = result.Replace("/*__METRIC_GROUP__*/", $"N'{safeGroupName}'", StringComparison.Ordinal);
        }

        // ── Step 1: Remove GO batch separators ────────────────────────────
        result = GoBatchPattern.Replace(result, string.Empty);

        // ── Step 2: Replace token comments with server IN-filters ─────────
        var inList = normalizedServers.Count > 0 ? BuildSqlInList(normalizedServers) : null;

        if (EnvironmentRules.IsSqlServer(environment))
        {
            var sqlFilter = inList is not null ? $"AND h.SQLServer IN ({inList})" : string.Empty;
            result = result.Replace("/*__SQLSERVER_FILTER__*/", sqlFilter, StringComparison.Ordinal);

            var sqlFilterStatic = inList is not null ? $"AND s.SQLServer IN ({inList})" : string.Empty;
            result = result.Replace("/*__SQLSERVER_FILTER_STATIC__*/", sqlFilterStatic, StringComparison.Ordinal);

            var sqlFilterWaits = inList is not null ? $"AND w.SQLServer IN ({inList})" : string.Empty;
            result = result.Replace("/*__SQLSERVER_FILTER_WAITS__*/", sqlFilterWaits, StringComparison.Ordinal);

            var sqlFilterWho = inList is not null ? $"AND wh.SQLServer IN ({inList})" : string.Empty;
            result = result.Replace("/*__SQLSERVER_FILTER_WHO__*/", sqlFilterWho, StringComparison.Ordinal);

            var alertFilter = inList is not null ? $"AND a.Server IN ({inList})" : string.Empty;
            result = result.Replace("/*__ALERT_SERVER_FILTER__*/", alertFilter, StringComparison.Ordinal);
        }
        else if (EnvironmentRules.IsWindows(environment))
        {
            var winFilter = inList is not null ? $"AND h.WinServer IN ({inList})" : string.Empty;
            result = result.Replace("/*__WINSERVER_FILTER__*/", winFilter, StringComparison.Ordinal);

            var winAlertFilter = inList is not null ? $"AND a.Server IN ({inList})" : string.Empty;
            result = result.Replace("/*__ALERT_SERVER_FILTER__*/", winAlertFilter, StringComparison.Ordinal);
        }

        // ── Step 3: Handle legacy @Server patterns ────────────────────────
        //    Some older samples may use @Server WHERE predicates instead of tokens.
        if (normalizedServers.Count > 0)
        {
            result = HistoryServerDeclarePattern.Replace(result, string.Empty);
            var legacyInList = BuildSqlInList(normalizedServers);
            result = HistoryServerWherePattern.Replace(result, m =>
            {
                var column = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                return $"{column} IN ({legacyInList})";
            });
        }

        // ── Step 4: Bind @FromUtc ─────────────────────────────────────────
        var fromSql = ParseIsoToSqlLiteral(fromUtcIso);
        if (fromSql is not null)
        {
            if (HistoryFromUtcDeclarePattern.IsMatch(result))
            {
                result = HistoryFromUtcDeclarePattern.Replace(result,
                    $"DECLARE @FromUtc datetime2(0) = '{fromSql}';");
            }
            else if (result.Contains("@FromUtc", StringComparison.OrdinalIgnoreCase))
            {
                // Script references @FromUtc but has no DECLARE — inject one
                result = $"DECLARE @FromUtc datetime2(0) = '{fromSql}';\n" + result;
            }
        }

        // ── Step 5: Bind @ToUtc ───────────────────────────────────────────
        var toSql = ParseIsoToSqlLiteral(toUtcIso);
        if (toSql is not null)
        {
            if (HistoryToUtcDeclarePattern.IsMatch(result))
            {
                result = HistoryToUtcDeclarePattern.Replace(result,
                    $"DECLARE @ToUtc datetime2(0) = '{toSql}';");
            }
            else if (result.Contains("@ToUtc", StringComparison.OrdinalIgnoreCase))
            {
                result = $"DECLARE @ToUtc datetime2(0) = '{toSql}';\n" + result;
            }
        }

        // ── Step 6: Bind @Top ─────────────────────────────────────────────
        if (HistoryTopDeclarePattern.IsMatch(result))
        {
            result = HistoryTopDeclarePattern.Replace(result,
                $"DECLARE @Top int = {top};");
        }
        else if (result.Contains("@Top", StringComparison.OrdinalIgnoreCase))
        {
            result = $"DECLARE @Top int = {top};\n" + result;
        }

        // ── Step 7: Clean up ──────────────────────────────────────────────
        // Collapse excessive blank lines left by removed GO/DECLARE lines
        result = Regex.Replace(result, @"(\r?\n){3,}", "\n\n");
        result = result.Trim();

        return result;
    }

    /// <summary>Parses an ISO 8601 string to a SQL-safe datetime literal (yyyy-MM-ddTHH:mm:ss).</summary>
    private static string? ParseIsoToSqlLiteral(string? isoValue)
    {
        if (string.IsNullOrWhiteSpace(isoValue))
            return null;

        if (DateTime.TryParse(isoValue, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var dt))
        {
            return dt.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        }

        return null;
    }

    private async Task<AskApiResponse> BuildSampleExecutionAsync(
        AskApiRequest request,
        ExecutionRoute route,
        LlmModelDefinition tuneModel,
        LlmModelDefinition planModel,
        LlmModelDefinition generateModel,
        CancellationToken cancellationToken,
        IProgressStream? progress = null)
    {
        var prog = progress ?? NullProgressStream.Instance;

        // 1. SCRIPT_FETCH_SAMPLE
        await prog.EmitAsync(ProgressEvent.PhaseStart("SCRIPT_FETCH_SAMPLE"), cancellationToken);

        QuestionSampleRow? sample;
        if (route.SampleId.HasValue)
        {
            sample = await _questionSamplesRepository.GetByIdAsync(
                route.SampleId.Value, request.Environment, cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(route.GroupKey))
        {
            // Metrics-tab flow: resolve generic sample by GroupKey
            sample = await _questionSamplesRepository.GetByGroupKeyAsync(
                route.GroupKey, request.Environment, cancellationToken);
        }
        else
        {
            sample = null;
        }

        if (sample is null || string.IsNullOrWhiteSpace(sample.Script))
        {
            await prog.EmitAsync(
                ProgressEvent.PhaseDone("SCRIPT_FETCH_SAMPLE", "not-found"), cancellationToken);
            return CreateSampleNotFoundResponse(request, route, tuneModel, planModel, generateModel);
        }

        await prog.EmitAsync(ProgressEvent.PhaseDone("SCRIPT_FETCH_SAMPLE"), cancellationToken);

        // 2. Sample scripts are curated and stored centrally — skip safety validation.
        //    The validator's target-aware rules are designed for LLM-generated scripts;
        //    sample scripts may legitimately use ALTER, DROP, CREATE on real DMVs/indexes.
        var script = sample.Script!;
        var tunedQuestion = sample.QuestionText;
        var isHistory = EnvironmentRules.IsHistory(request.Environment);

        // 3. Determine script language from environment
        //    History environments always use SQL (even Windows_History).
        var scriptLanguage = isHistory ? "SQL"
            : EnvironmentRules.IsSqlServer(request.Environment) ? "SQL" : "PS";

        // ── HISTORY BRANCH: bind parameters + centralized execution ───────
        if (isHistory)
        {
            // Resolve whitelist entry if metricKey is present (metrics-tab flow)
            MetricWhitelistEntry? metricEntry = null;
            if (!string.IsNullOrWhiteSpace(request.MetricKey))
            {
                var metricError = ValidateMetricKey(request.MetricKey, request.Environment);
                if (metricError is not null)
                {
                    _logger.LogWarning("Metric validation failed: {Error}, metricKey={MetricKey}", metricError, request.MetricKey);
                    await prog.EmitAsync(ProgressEvent.PhaseDone("SCRIPT_FETCH_SAMPLE", "invalid-metric"), cancellationToken);
                    return CreateStoppedResponse(request, tunedQuestion,
                        $"STOPPED: {metricError}",
                        tuneModel, planModel, generateModel);
                }

                var metricMap = GetMetricMapForEnvironment(request.Environment)!;
                metricEntry = metricMap[request.MetricKey];
                _logger.LogInformation(
                    "Metric whitelist matched: metricKey={MetricKey}, label={Label}, group={Group}",
                    metricEntry.MetricKey, metricEntry.MetricLabel, metricEntry.MetricGroup);
            }
            else if (string.Equals(sample.GroupKey, "Metrics", StringComparison.OrdinalIgnoreCase))
            {
                // Metrics sample template contains token placeholders that require a metricKey.
                // Without it the SQL will have unreplaced /*__METRIC_SELECT__*/ etc. and fail.
                _logger.LogWarning("Metrics sample requested without metricKey, environment={Env}", request.Environment);
                await prog.EmitAsync(ProgressEvent.PhaseDone("SCRIPT_FETCH_SAMPLE", "missing-metric-key"), cancellationToken);
                return CreateStoppedResponse(request, tunedQuestion,
                    "STOPPED: Please select a metric to display. The Metrics tab requires a metricKey, metricLabel, and metricGroup in the request.",
                    tuneModel, planModel, generateModel);
            }

            // Normalize selectedServers: strip port, keep server\instance, deduplicate
            var normalizedServers = NormalizeHistorySelectedServers(
                request.SelectedServers ?? request.SelectedTargets);

            _logger.LogInformation("History server filter: [{Servers}]",
                string.Join(", ", normalizedServers));

            script = TransformHistorySampleSql(
                script, request.Environment, normalizedServers,
                request.FromUtc, request.ToUtc, 5000,
                metricEntry);

            _logger.LogDebug("Final replaced script preview: {Preview}",
                script.Length > 500 ? script[..500] + "..." : script);

            var response = CreateBaseResponse(request, tunedQuestion, tuneModel, planModel, generateModel, _modelSelector);
            response.Plan.Mode = "SAMPLE_ONLY";
            response.Plan.GeneratorMode = "SAMPLE_ONLY";
            response.Plan.SampleId = route.SampleId ?? sample.Id;
            response.Plan.GroupKey = sample.GroupKey;
            response.Plan.ScriptLanguage = "SQL";
            response.Tuning.TunedQuestion = tunedQuestion;
            response.Tuning.Status = "SKIPPED";

            var scriptParams = new Dictionary<string, object?>
            {
                ["selectedServers"] = normalizedServers.Count > 0 ? normalizedServers.ToArray() : null,
                ["fromUtc"] = request.FromUtc,
                ["toUtc"] = request.ToUtc,
                ["top"] = 5000
            };
            if (metricEntry is not null)
            {
                scriptParams["metricKey"] = metricEntry.MetricKey;
                scriptParams["metricLabel"] = metricEntry.MetricLabel;
                scriptParams["metricGroup"] = metricEntry.MetricGroup;
            }

            response.Script = new AskResponseScript
            {
                Final = script,
                Source = "SAMPLE",
                Validation = new AskScriptValidation { IsSafeReadOnly = true },
                Parameters = scriptParams
            };
            response.Result.Kind = "EXECUTION";
            response.Result.Status = "FAILED"; // updated after execution

            // Override response.Request to show centralized target and history context
            response.Request.SelectedTargets = [HistoryTargetLabel];
            response.Request.TargetType = "CentralizedHistory";
            response.Request.SelectedServers = normalizedServers.Count > 0 ? normalizedServers.ToArray() : null;
            response.Request.FromUtc = request.FromUtc;
            response.Request.ToUtc = request.ToUtc;
            response.Request.MetricKey = request.MetricKey;
            response.Request.MetricLabel = request.MetricLabel;
            response.Request.MetricGroup = request.MetricGroup;

            // Execute ONCE on CTS03 — centralized, not per-target fan-out
            var historyExecution = await _scriptAutoFixOrchestrator.ExecuteWithAutoFixAsync(
                new ScriptExecutionRequest
                {
                    Environment = request.Environment,
                    SelectedServers = [EnvironmentRules.HistoryExecutionTarget],
                    TunedQuestion = tunedQuestion,
                    ScriptLanguage = "SQL",
                    GeneratedScript = script,
                    AllowRepair = false,
                    SkipSafetyScanning = true
                },
                prog,
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(historyExecution.FinalScript))
                response.Script.Final = historyExecution.FinalScript;

            PopulateExecutionResult(response.Result, historyExecution);

            // Relabel target from bare "CTS03" to "CTS03::SQLGig"
            if (response.Result.Items is not null)
            {
                foreach (var item in response.Result.Items)
                {
                    if (string.Equals(item.Target, EnvironmentRules.HistoryExecutionTarget, StringComparison.OrdinalIgnoreCase))
                        item.Target = HistoryTargetLabel;
                }
            }

            var historyRetry = MapRetryAttempts(historyExecution.Attempts);
            if (historyRetry is not null)
                response.RetryAttempts = historyRetry;

            // Explain answer
            await prog.EmitAsync(ProgressEvent.PhaseStart("ANSWER"), cancellationToken);
            var (histAnswer, histDrift, histDiag) = await BuildExplainAnswerAsync(
                request, tunedQuestion, response.Result, cancellationToken);
            response.Answer = histAnswer;
            response.DriftReport = histDrift;
            response.DiagnosticReport = histDiag;
            await prog.EmitAsync(ProgressEvent.PhaseDone("ANSWER"), cancellationToken);

            // Chart plan for History sample execution
            var (sampleChartDetails, sampleDataProfile, sampleChartSummary) = await GenerateChartPlanAsync(
                request, tunedQuestion, response.Result, cancellationToken);
            response.ChartDetails = sampleChartDetails;
            response.DataProfile = sampleDataProfile;
            response.ChartSummary = sampleChartSummary;

            // Build pipeline process flow for the "Process" tab
            response.Process = BuildPipelineProcess(response);

            return response;
        }

        // ── LIVE BRANCH: standard per-target sample execution ─────────────

        // 4. Build response shell
        var response2 = CreateBaseResponse(request, tunedQuestion, tuneModel, planModel, generateModel, _modelSelector);
        response2.Plan.Mode = "SAMPLE_ONLY";
        response2.Plan.GeneratorMode = "SAMPLE_ONLY";
        response2.Plan.SampleId = route.SampleId;
        response2.Plan.GroupKey = sample.GroupKey;
        response2.Plan.ScriptLanguage = scriptLanguage;
        response2.Tuning.TunedQuestion = tunedQuestion;
        response2.Tuning.Status = "SKIPPED";
        response2.Script = new AskResponseScript
        {
            Final = script,
            Source = "SAMPLE",
            Validation = new AskScriptValidation { IsSafeReadOnly = true }
        };
        response2.Result.Kind = "EXECUTION";
        response2.Result.Status = "FAILED"; // updated after execution

        // 5. Resolve SQL ports before execution (CTS02#ADMIN → CTS02,1432#ADMIN)
        var resolvedTargets = EnvironmentRules.IsSqlServer(request.Environment)
            ? await ResolveSqlPortsAsync(request.SelectedTargets ?? [], request.BearerToken, cancellationToken)
            : request.SelectedTargets ?? [];

        // 6. Execute via orchestrator (repair disabled for curated scripts)
        var executionResponse = await _scriptAutoFixOrchestrator.ExecuteWithAutoFixAsync(
            new ScriptExecutionRequest
            {
                Environment = request.Environment,
                SelectedServers = resolvedTargets,
                TunedQuestion = tunedQuestion,
                ScriptLanguage = scriptLanguage,
                GeneratedScript = script,
                AllowRepair = false,
                SkipSafetyScanning = true
            },
            prog,
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(executionResponse.FinalScript))
            response2.Script.Final = executionResponse.FinalScript;

        PopulateExecutionResult(response2.Result, executionResponse);

        var retryAttempts = MapRetryAttempts(executionResponse.Attempts);
        if (retryAttempts is not null)
            response2.RetryAttempts = retryAttempts;

        // 6. Explain answer
        await prog.EmitAsync(ProgressEvent.PhaseStart("ANSWER"), cancellationToken);

        var (liveAnswer, liveDrift, liveDiag) = await BuildExplainAnswerAsync(
            request, tunedQuestion, response2.Result, cancellationToken);
        response2.Answer = liveAnswer;
        response2.DriftReport = liveDrift;
        response2.DiagnosticReport = liveDiag;

        // Live visuals for multi-server results (non-drift, non-diagnostic)
        if (liveDrift is null && liveDiag is null)
            response2.LiveVisuals = BuildLiveVisuals(response2.Result);

        await prog.EmitAsync(ProgressEvent.PhaseDone("ANSWER"), cancellationToken);

        // Build pipeline process flow for the "Process" tab
        response2.Process = BuildPipelineProcess(response2);

        // Data quality flags — tells UI if data is real, mock, or degraded
        response2.DataQuality = BuildDataQuality(response2.Result);

        return response2;
    }

    private static AskApiResponse CreateSampleNotFoundResponse(
        AskApiRequest request,
        ExecutionRoute route,
        LlmModelDefinition tuneModel,
        LlmModelDefinition planModel,
        LlmModelDefinition generateModel)
    {
        var response = CreateBaseResponse(request, request.Question, tuneModel, planModel, generateModel);
        response.Tuning.Status = "SKIPPED";
        response.Plan.Mode = "STOPPED";
        response.Plan.GeneratorMode = "SAMPLE_ONLY";
        response.Plan.SampleId = route.SampleId;
        response.Plan.GroupKey = route.GroupKey;
        response.Result.Kind = "EXECUTION";
        response.Result.Status = "STOPPED";
        response.Result.AnswerText =
            $"Sample question not found: SampleId={route.SampleId}, GroupKey={route.GroupKey}, Environment={request.Environment}.";
        return response;
    }

    private static AskApiResponse CreateSampleBlockedResponse(
        AskApiRequest request,
        ExecutionRoute route,
        string blockedToken,
        LlmModelDefinition tuneModel,
        LlmModelDefinition planModel,
        LlmModelDefinition generateModel)
    {
        var response = CreateBaseResponse(request, request.Question, tuneModel, planModel, generateModel);
        response.Tuning.Status = "SKIPPED";
        response.Plan.Mode = "STOPPED";
        response.Plan.GeneratorMode = "SAMPLE_ONLY";
        response.Plan.SampleId = route.SampleId;
        response.Plan.GroupKey = route.GroupKey;
        response.Script = new AskResponseScript
        {
            Final = string.Empty,
            Source = "SAMPLE",
            Validation = new AskScriptValidation
            {
                IsSafeReadOnly = false,
                BlockedTokenFound = blockedToken
            }
        };
        response.Result.Kind = "EXECUTION";
        response.Result.Status = "STOPPED";
        response.Result.AnswerText =
            $"BLOCKED: Sample script rejected — {blockedToken} targets a real table or executes a forbidden command. Please update Script column for SampleId={route.SampleId}.";
        return response;
    }

    private async Task<AskApiResponse> BuildExecutionResponseAsync(
        AskApiRequest request,
        string tunedQuestion,
        string script,
        string scriptLanguage,
        string resolution,
        string dispatchMethod,
        LlmModelDefinition tuneModel,
        LlmModelDefinition planModel,
        LlmModelDefinition generateModel,
        CancellationToken cancellationToken,
        IProgressStream? progress = null)
    {
        _ = resolution;     // kept in signature for call-site clarity
        _ = dispatchMethod; // kept in signature for call-site clarity

        var prog = progress ?? NullProgressStream.Instance;

        var response = CreateBaseResponse(request, tunedQuestion, tuneModel, planModel, generateModel, _modelSelector);
        response.Plan.Mode = "LLM_ONLY";
        response.Plan.GeneratorMode = "LLM_ONLY";
        response.Plan.ScriptLanguage = scriptLanguage;
        response.Script = new AskResponseScript
        {
            Final = script,
            Source = "LLM",
            Validation = new AskScriptValidation { IsSafeReadOnly = true }
        };
        response.Result.Kind = "EXECUTION";
        response.Result.Status = "FAILED"; // updated below after execution

        // Resolve SQL ports before execution (CTS02#ADMIN → CTS02,1432#ADMIN)
        var resolvedLlmTargets = EnvironmentRules.IsSqlServer(request.Environment)
            ? await ResolveSqlPortsAsync(request.SelectedTargets ?? [], request.BearerToken, cancellationToken)
            : request.SelectedTargets ?? [];

        // The orchestrator emits VALIDATE, REPAIR, and EXECUTE phase events itself.
        var executionResponse = await _scriptAutoFixOrchestrator.ExecuteWithAutoFixAsync(
            new ScriptExecutionRequest
            {
                Environment = request.Environment,
                SelectedServers = resolvedLlmTargets,
                TunedQuestion = tunedQuestion,
                ScriptLanguage = scriptLanguage,
                GeneratedScript = script
            },
            prog,
            cancellationToken);

        // Update final script to the post-fix version (may differ from original)
        if (!string.IsNullOrWhiteSpace(executionResponse.FinalScript))
            response.Script.Final = executionResponse.FinalScript;

        PopulateExecutionResult(response.Result, executionResponse);

        // Map retry attempts to top-level retryAttempts node (only when repairs occurred)
        var retryAttempts = MapRetryAttempts(executionResponse.Attempts);
        if (retryAttempts is not null)
            response.RetryAttempts = retryAttempts;

        // Explain step: generate answer node based on execution result.
        await prog.EmitAsync(ProgressEvent.PhaseStart("ANSWER"), cancellationToken);

        var (genAnswer, genDrift, genDiag) = await BuildExplainAnswerAsync(
            request,
            tunedQuestion,
            response.Result,
            cancellationToken);
        response.Answer = genAnswer;
        response.DriftReport = genDrift;
        response.DiagnosticReport = genDiag;

        // Live visuals for multi-server results (non-drift, non-diagnostic)
        if (genDrift is null && genDiag is null)
            response.LiveVisuals = BuildLiveVisuals(response.Result);

        await prog.EmitAsync(ProgressEvent.PhaseDone("ANSWER"), cancellationToken);

        // Data quality flags
        response.DataQuality = BuildDataQuality(response.Result);

        return response;
    }

    private static List<AskRetryAttempt>? MapRetryAttempts(List<ScriptExecutionAttempt> attempts)
    {
        // Only include when at least one repair attempt occurred
        if (attempts.Count <= 1 && attempts.All(a => a.Phase == "EXECUTE"))
            return null;

        return attempts.Select(a => new AskRetryAttempt
        {
            Attempt = a.Attempt,
            Target = a.Target,
            Phase = a.Phase,
            ScriptHash = a.ScriptHash,
            ScriptPreview = a.ScriptPreview,
            Status = a.Status,
            ErrorType = a.ErrorType,
            ErrorMessage = a.Error,
            RepairedByLlm = a.RepairedByLlm,
            TsUtc = a.TsUtc
        }).ToList();
    }

    private static void PopulateExecutionResult(AskResponseResult result, ScriptExecutionResponse executionResponse)
    {
        var items = new List<AskExecutionItem>();

        foreach (var r in executionResponse.ResultsByServer)
        {
            if (string.Equals(r.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase))
            {
                var rows = r.Rows?.Select(StripNullValues).ToList() ?? [];
                items.Add(new AskExecutionItem
                {
                    Target = r.Server,
                    Status = "SUCCESS",
                    RowCount = rows.Count,
                    Rows = rows.Count > 0 ? rows : null
                });
            }
            else
            {
                // Execution-level failure (connection/timeout) — single error row.
                items.Add(new AskExecutionItem
                {
                    Target = r.Server,
                    Status = "FAILED",
                    RowCount = 0,
                    Rows =
                    [
                        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["ErrorMessage"] = r.Error ?? "Execution failed."
                        }
                    ]
                });
            }
        }

        // Edge case: orchestrator returned no server results but logged an attempt.
        if (items.Count == 0)
        {
            var lastAttempt = executionResponse.Attempts.LastOrDefault();
            if (lastAttempt is not null)
            {
                var isSuccess = string.Equals(lastAttempt.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase);
                items.Add(new AskExecutionItem
                {
                    Target = lastAttempt.Target ?? string.Empty,
                    Status = isSuccess ? "SUCCESS" : "FAILED",
                    RowCount = 0,
                    Rows = isSuccess ? null :
                    [
                        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["ErrorMessage"] = lastAttempt.Error ?? "Execution failed."
                        }
                    ]
                });
            }
        }

        var serverList = executionResponse.ResultsByServer;
        var allSuccess = serverList.Count > 0
            && serverList.All(r => string.Equals(r.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase));
        var anySuccess = serverList.Count > 0
            && serverList.Any(r => string.Equals(r.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase));

        result.Status = allSuccess ? "SUCCESS" : anySuccess ? "PARTIAL_SUCCESS" : "FAILED";
        result.Items = items.Count > 0 ? items : null;
        result.Summary = new AskExecutionSummary
        {
            SuccessCount = executionResponse.Summary.SuccessCount,
            FailCount = executionResponse.Summary.FailCount,
            TotalRowCount = executionResponse.Summary.TotalRowCount,
            DurationMs = executionResponse.ResultsByServer.Sum(r => r.DurationMs)
        };
    }

    /// <summary>Returns a copy of <paramref name="row"/> with all null-valued entries removed.</summary>
    private static Dictionary<string, object?> StripNullValues(Dictionary<string, object?> row) =>
        new(row.Where(kv => kv.Value != null), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Inspects execution results and builds data quality flags.
    /// Detects: execution failures, collector failures, incomplete data.
    /// </summary>
    internal static AskDataQuality BuildDataQuality(AskResponseResult result, bool usedMockLlm = false)
    {
        var quality = new AskDataQuality { UsedMockLlm = usedMockLlm };
        var warnings = new List<string>();

        // Check for execution failures
        if (result.Items is { Count: > 0 })
        {
            var failed = result.Items.Where(i => i.Status == "FAILED").ToList();
            if (failed.Count > 0)
            {
                quality.HasExecutionFailures = true;
                foreach (var f in failed)
                    warnings.Add($"Execution failed on {f.Target}: {f.Rows?.FirstOrDefault()?.GetValueOrDefault("ErrorMessage")}");
            }

            // Check for collector failures in diagnostic data (rows with ErrorMessage or empty MetricValue)
            foreach (var item in result.Items.Where(i => i.Status == "SUCCESS" && i.Rows is { Count: > 0 }))
            {
                foreach (var row in item.Rows!)
                {
                    // Detect Get-Counter failures, permission errors, collector timeouts
                    var detail = row.GetValueOrDefault("Detail")?.ToString() ?? "";
                    var metricValue = row.GetValueOrDefault("MetricValue")?.ToString() ?? "";
                    var severity = row.GetValueOrDefault("SeverityHint")?.ToString() ?? "";

                    if (detail.Contains("collector failure", StringComparison.OrdinalIgnoreCase)
                        || detail.Contains("access denied", StringComparison.OrdinalIgnoreCase)
                        || detail.Contains("permission", StringComparison.OrdinalIgnoreCase)
                        || metricValue.Equals("<null>", StringComparison.OrdinalIgnoreCase))
                    {
                        quality.HasCollectorFailures = true;
                        var metricName = row.GetValueOrDefault("MetricName")?.ToString() ?? "Unknown";
                        warnings.Add($"Collector failure: {metricName} on {item.Target} — {detail}");
                    }
                }
            }
        }

        // Mock LLM warning
        if (usedMockLlm)
            warnings.Add("LLM unavailable — answer generated from deterministic fallback. Configure API key for real analysis.");

        // Determine overall source
        quality.Source = usedMockLlm ? "mock"
            : quality.HasExecutionFailures && result.Items?.All(i => i.Status == "FAILED") == true ? "degraded"
            : quality.HasExecutionFailures ? "partial"
            : "real";

        quality.Warnings = warnings.Count > 0 ? warnings : null;
        return quality;
    }

    private async Task<(AskAnswerNode Answer, AskDriftReport? DriftReport, AskDiagnosticReport? DiagnosticReport)> BuildExplainAnswerAsync(
        AskApiRequest request,
        string tunedQuestion,
        AskResponseResult result,
        CancellationToken cancellationToken)
    {
        var highlightsArray = BuildHighlightsArray(result);
        var severity = DeriveSeverity(result, highlightsArray);

        // No useful data when all targets failed — concise 2-sentence message, no verbose dump.
        if (string.Equals(result.Status, "FAILED", StringComparison.OrdinalIgnoreCase))
        {
            var failReason = ExtractConciseFailReason(result);
            var failedNode = new AskAnswerNode
            {
                Status = "FAILED",
                Severity = severity,
                Highlights = highlightsArray.Length > 0 ? highlightsArray : null,
                Explanation = failReason,
            };
            return (failedNode, null, null);
        }

        // ── Diagnostic detection: use Root Cause Diagnostic Engine ──
        if (IsDiagnosticData(result))
        {
            _logger.LogInformation("Diagnostic data detected — using Root Cause Diagnostic Engine.");
            var diagnosticReport = BuildDiagnosticReport(result);
            var diagNode = BuildDiagnosticAnswerNode(diagnosticReport, severity, highlightsArray);
            return (diagNode, null, diagnosticReport);
        }

        // ── Drift/Compare detection: use Fleet Drift Engine for rich structured output ──
        if (IsDriftData(result))
        {
            _logger.LogInformation("Drift data detected — using Fleet Drift Engine.");
            var (driftReport, driftNode) = BuildFleetDriftReport(result);
            return (driftNode, driftReport, null);
        }

        var explainModel = _modelSelector.SelectExplainModel();

        try
        {
            var highlightsText = GenerateHighlights(result);
            var dataSample = BuildDataSample(result);
            var summary = result.Summary is null
                ? string.Empty
                : $"success={result.Summary.SuccessCount}, fail={result.Summary.FailCount}, rows={result.Summary.TotalRowCount}";

            var explainPrompt = PromptTemplates.ExplainAnswer
                .Replace("{{$environment}}", request.Environment ?? string.Empty, StringComparison.Ordinal)
                .Replace("{{$rawQuestion}}", request.Question ?? string.Empty, StringComparison.Ordinal)
                .Replace("{{$tunedQuestion}}", tunedQuestion ?? string.Empty, StringComparison.Ordinal)
                .Replace("{{$resultStatus}}", result.Status ?? string.Empty, StringComparison.Ordinal)
                .Replace("{{$resultSummary}}", summary, StringComparison.Ordinal)
                .Replace("{{$highlights}}", highlightsText, StringComparison.Ordinal)
                .Replace("{{$dataSample}}", dataSample, StringComparison.Ordinal);

            var explainClient = ResolveClient(explainModel.Provider);
            var raw = await explainClient.GenerateAsync(
                explainPrompt,
                tunedQuestion ?? string.Empty,
                request.Environment ?? string.Empty,
                explainModel.ModelKey,
                cancellationToken,
                apiKey: explainModel.ApiKeyEncrypted);

            var answerNode = ParseExplainResponse(raw, explainModel, highlightsArray, severity, result.Status);

            // Post-processing: ensure structured sections exist
            EnsureExplainSections(answerNode, result, highlightsArray);
            DeduplicateExplanation(answerNode);

            return (answerNode, null, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Explain step failed; returning FAILED answer node.");
            var failedNode = new AskAnswerNode
            {
                Status = "FAILED",
                Severity = severity,
                Highlights = highlightsArray.Length > 0 ? highlightsArray : null,
                Explanation = $"Explanation could not be generated: {ex.Message}"
            };
            EnsureExplainSections(failedNode, result, highlightsArray);
            return (failedNode, null, null);
        }
    }

    /// <summary>
    /// Ensures explain answers have structured sections. If the LLM didn't produce sections,
    /// builds deterministic sections from the existing flat fields (explanation, anomaly, analysis, suggestion).
    /// </summary>
    private static void EnsureExplainSections(AskAnswerNode node, AskResponseResult result, string[] highlights)
    {
        if (node.Sections is { Count: >= 4 })
            return;

        // Build sections from flat fields when LLM didn't produce structured output
        var sections = new List<AnswerSection>();

        // Findings section from explanation
        if (!string.IsNullOrWhiteSpace(node.Explanation))
        {
            sections.Add(new AnswerSection
            {
                Key = "findings",
                Title = "Key Findings",
                Icon = "Search",
                Tone = node.Severity switch
                {
                    "CRITICAL" => "critical",
                    "WARNING" => "warning",
                    _ => "info"
                },
                Bullets = SplitIntoBullets(node.Explanation)
            });
        }

        // Anomaly/risk section
        if (!string.IsNullOrWhiteSpace(node.Anomaly))
        {
            sections.Add(new AnswerSection
            {
                Key = "risk_assessment",
                Title = "Risk Assessment",
                Icon = "AlertTriangle",
                Tone = "warning",
                Bullets = SplitIntoBullets(node.Anomaly)
            });
        }

        // Analysis section
        if (!string.IsNullOrWhiteSpace(node.Analysis))
        {
            sections.Add(new AnswerSection
            {
                Key = "analysis",
                Title = "Analysis",
                Icon = "Compass",
                Tone = "info",
                Bullets = SplitIntoBullets(node.Analysis)
            });
        }

        // Per-server breakdown from highlights
        if (highlights.Length > 0)
        {
            sections.Add(new AnswerSection
            {
                Key = "server_breakdown",
                Title = "Per-Server Breakdown",
                Icon = "Database",
                Tone = "info",
                Bullets = highlights.Take(5).ToList()
            });
        }

        // Action items from suggestion
        if (!string.IsNullOrWhiteSpace(node.Suggestion))
        {
            sections.Add(new AnswerSection
            {
                Key = "action_items",
                Title = "Recommended Actions",
                Icon = "ListChecks",
                Tone = "ok",
                Steps = SplitIntoBullets(node.Suggestion)
            });
        }

        // Context section with execution summary
        var contextBullets = new List<string>();
        if (result.Summary is not null)
        {
            contextBullets.Add($"Executed across {result.Summary.SuccessCount + result.Summary.FailCount} target(s): {result.Summary.SuccessCount} succeeded, {result.Summary.FailCount} failed.");
            if (result.Summary.TotalRowCount > 0)
                contextBullets.Add($"Total rows returned: {result.Summary.TotalRowCount}.");
        }
        if (contextBullets.Count > 0)
        {
            sections.Add(new AnswerSection
            {
                Key = "context",
                Title = "Execution Context",
                Icon = "Info",
                Tone = "info",
                Bullets = contextBullets
            });
        }

        // Ensure minimum 4 sections
        if (sections.Count < 4)
        {
            if (!sections.Any(s => s.Key == "findings"))
                sections.Insert(0, new AnswerSection
                {
                    Key = "findings", Title = "Key Findings", Icon = "Search", Tone = "info",
                    Bullets = [node.Explanation ?? "Execution completed. Review the data for details."]
                });
            if (!sections.Any(s => s.Key == "context"))
                sections.Add(new AnswerSection
                {
                    Key = "context", Title = "Additional Context", Icon = "BookOpen", Tone = "info",
                    Bullets = ["Review result.items for the complete dataset."]
                });
            if (!sections.Any(s => s.Key == "action_items"))
                sections.Add(new AnswerSection
                {
                    Key = "action_items", Title = "Next Steps", Icon = "Lightbulb", Tone = "ok",
                    Steps = ["Review the data in the results panel.", "Investigate any flagged anomalies.", "Re-run with different parameters if needed."]
                });
            if (sections.Count < 4 && !sections.Any(s => s.Key == "health_check"))
                sections.Add(new AnswerSection
                {
                    Key = "health_check", Title = "Health Status", Icon = "ShieldCheck",
                    Tone = node.Severity switch { "CRITICAL" => "critical", "WARNING" => "warning", _ => "ok" },
                    Bullets = [node.Severity switch
                    {
                        "CRITICAL" => "One or more critical issues detected — immediate attention recommended.",
                        "WARNING" => "Some items need attention — review the flagged items above.",
                        _ => "All monitored values are within normal operating ranges."
                    }]
                });
        }

        node.Sections = sections;

        // Build title if missing
        if (string.IsNullOrWhiteSpace(node.Title))
            node.Title = node.Explanation?.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim()
                         ?? "Execution Results Analysis";

        // Build summary if missing
        if (node.Summary is null or { Length: 0 })
        {
            var summaryBullets = new List<string>();
            if (!string.IsNullOrWhiteSpace(node.Explanation))
                summaryBullets.AddRange(node.Explanation.Split('.', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 10)
                    .Take(3)
                    .Select(s => s + "."));
            if (!string.IsNullOrWhiteSpace(node.Anomaly))
                summaryBullets.Add(node.Anomaly.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() + ".");
            if (summaryBullets.Count == 0)
                summaryBullets.Add("Execution completed successfully.");
            node.Summary = summaryBullets.Take(7).ToArray();
        }

        // Build keyMetrics deterministically from result data if not already set
        if (node.KeyMetrics is null or { Count: 0 })
            node.KeyMetrics = BuildKeyMetricsFromResult(result);

        // Build comparison deterministically from multi-target results if not already set
        if (node.Comparison is null or { Count: 0 })
            node.Comparison = BuildComparisonFromResult(result);

        // Build recommendations deterministically if not already set
        if (node.Recommendations is null or { Count: 0 })
            node.Recommendations = BuildRecommendationsFromResult(result, node.Severity, highlights);
    }

    /// <summary>Extracts key metrics from result rows for KPI card display.</summary>
    private static List<AskKeyMetric>? BuildKeyMetricsFromResult(AskResponseResult result)
    {
        if (result.Items is null or { Count: 0 })
            return null;

        var metrics = new List<AskKeyMetric>();

        // Execution summary metrics
        if (result.Summary is not null)
        {
            var total = result.Summary.SuccessCount + result.Summary.FailCount;
            metrics.Add(new AskKeyMetric
            {
                Label = "Targets",
                Value = $"{result.Summary.SuccessCount}/{total}",
                Unit = null,
                Status = result.Summary.FailCount > 0 ? "warning" : "ok"
            });
            if (result.Summary.TotalRowCount > 0)
                metrics.Add(new AskKeyMetric
                {
                    Label = "Total Rows",
                    Value = result.Summary.TotalRowCount.ToString(),
                    Unit = "count",
                    Status = "info"
                });
        }

        // Extract numeric column metrics from first successful item
        var successItems = result.Items
            .Where(i => string.Equals(i.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase) && i.Rows is { Count: > 0 })
            .ToList();
        if (successItems.Count == 0) return metrics.Count > 0 ? metrics : null;

        var firstItem = successItems[0];
        var allRows = firstItem.Rows!;
        var firstRow = allRows[0];

        // ── History time-series pattern: ServerName + MetricValue + MetricName ──
        // Build per-server aggregated key metrics with context-aware severity.
        if (firstRow.ContainsKey("ServerName") && firstRow.ContainsKey("MetricValue") && firstRow.ContainsKey("MetricName"))
        {
            var metricName = firstRow.TryGetValue("MetricName", out var mn) ? mn?.ToString() : null;
            var metricCol = metricName ?? "MetricValue";

            var byServer = allRows
                .Where(r => r.TryGetValue("ServerName", out var s) && s is not null)
                .GroupBy(r => r["ServerName"]!.ToString()!, StringComparer.OrdinalIgnoreCase);

            foreach (var grp in byServer.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (metrics.Count >= 6) break;
                var vals = grp
                    .Select(r => r.TryGetValue("MetricValue", out var mv) && mv is not null
                        && double.TryParse(mv.ToString(), System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : (double?)null)
                    .Where(v => v.HasValue)
                    .Select(v => v!.Value)
                    .ToList();

                if (vals.Count == 0) continue;

                var avg = vals.Average();
                var max = vals.Max();
                var status = ClassifyHistoryMetricValue(metricName, avg);
                var maxStatus = ClassifyHistoryMetricValue(metricName, max);
                if (string.Equals(maxStatus, "critical", StringComparison.Ordinal) && !string.Equals(status, "critical", StringComparison.Ordinal))
                    status = "warning";

                var label = metricName is not null ? $"{grp.Key} Avg {HumanizeColumnName(metricName)}" : $"{grp.Key} Avg";
                metrics.Add(new AskKeyMetric
                {
                    Label = label,
                    Value = FormatMetricValue(avg, metricCol),
                    Unit = InferMetricUnit(metricCol),
                    Status = status
                });

                // Add peak metric if significantly different from avg
                if (max > avg * 1.3 && metrics.Count < 6)
                {
                    var peakLabel = $"{grp.Key} Peak";
                    metrics.Add(new AskKeyMetric
                    {
                        Label = peakLabel,
                        Value = FormatMetricValue(max, metricCol),
                        Unit = InferMetricUnit(metricCol),
                        Status = maxStatus
                    });
                }
            }

            return metrics.Count > 0 ? metrics : null;
        }

        // ── Standard (non-History) path: extract from first row ──
        foreach (var kv in firstRow)
        {
            if (metrics.Count >= 6) break;
            if (IsSkippableColumn(kv.Key)) continue;
            if (kv.Value is null) continue;

            if (double.TryParse(kv.Value.ToString(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var numVal))
            {
                var status = ClassifyMetricStatus(kv.Key, numVal);
                metrics.Add(new AskKeyMetric
                {
                    Label = HumanizeColumnName(kv.Key),
                    Value = FormatMetricValue(numVal, kv.Key),
                    Unit = InferMetricUnit(kv.Key),
                    Status = status
                });
            }
        }

        return metrics.Count > 0 ? metrics : null;
    }

    /// <summary>
    /// Classifies a History MetricValue using the MetricThresholdRegistry.
    /// Returns "critical", "warning", or "ok" based on operational thresholds.
    /// Works for ANY metric that has an entry in the registry — CPU, memory, disk, PLE, blocking, etc.
    /// </summary>
    private static string ClassifyHistoryMetricValue(string? metricName, double value)
    {
        if (metricName is null) return "info";

        // Direct lookup in the operational threshold registry
        if (MetricThresholdRegistry.TryGetValue(metricName, out var th))
            return ClassifyByThreshold(th, value);

        // Fuzzy match: try common suffixes/prefixes that map to known keys
        foreach (var (key, threshold) in MetricThresholdRegistry)
        {
            if (string.Equals(key, metricName, StringComparison.OrdinalIgnoreCase))
                return ClassifyByThreshold(threshold, value);
        }

        // Fallback: use the generic ClassifyMetricStatus which pattern-matches column names
        return ClassifyMetricStatus(metricName, value);
    }

    private static string ClassifyByThreshold(MetricThreshold th, double value)
    {
        // Check high thresholds (e.g., CPU > 95 = critical, > 80 = warning)
        if (th.CriticalHigh > 0 && value >= th.CriticalHigh) return "critical";
        if (th.WarningHigh > 0 && value >= th.WarningHigh) return "warning";
        // Check low thresholds (e.g., PLE < 60 = critical, < 300 = warning)
        if (th.CriticalLow > 0 && value <= th.CriticalLow) return "critical";
        if (th.WarningLow > 0 && value <= th.WarningLow) return "warning";
        return "ok";
    }

    /// <summary>Builds per-target comparison entries from multi-target results.</summary>
    private static List<AskComparisonEntry>? BuildComparisonFromResult(AskResponseResult result)
    {
        if (result.Items is null or { Count: 0 })
            return null;

        // ── History pattern: single item with ServerName column containing multiple servers ──
        if (result.Items.Count == 1)
        {
            var singleItem = result.Items[0];
            if (singleItem.Rows is { Count: > 0 } && singleItem.Rows[0].ContainsKey("ServerName") && singleItem.Rows[0].ContainsKey("MetricValue"))
            {
                var metricName = singleItem.Rows[0].TryGetValue("MetricName", out var mn) ? mn?.ToString() : null;

                var byServer = singleItem.Rows
                    .Where(r => r.TryGetValue("ServerName", out var s) && s is not null)
                    .GroupBy(r => r["ServerName"]!.ToString()!, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (byServer.Count < 2) return null; // Need at least 2 servers for comparison

                var entries = new List<AskComparisonEntry>();
                foreach (var grp in byServer.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var vals = grp
                        .Select(r => r.TryGetValue("MetricValue", out var mv) && mv is not null
                            && double.TryParse(mv.ToString(), System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : (double?)null)
                        .Where(v => v.HasValue)
                        .Select(v => v!.Value)
                        .ToList();

                    if (vals.Count == 0) continue;

                    var avg = Math.Round(vals.Average(), 2);
                    var max = Math.Round(vals.Max(), 2);
                    var min = Math.Round(vals.Min(), 2);
                    var status = ClassifyHistoryMetricValue(metricName, avg);
                    var maxStatus = ClassifyHistoryMetricValue(metricName, max);
                    if (maxStatus is "critical" && status is not "critical") status = "warning";

                    var serverMetrics = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["avg"] = avg,
                        ["min"] = min,
                        ["max"] = max,
                        ["readings"] = vals.Count
                    };

                    string? note = null;
                    if (string.Equals(maxStatus, "critical", StringComparison.Ordinal))
                        note = $"Sustained {(metricName ?? "metric")} at critical levels (max {max})";
                    else if (string.Equals(status, "warning", StringComparison.Ordinal))
                        note = $"{(metricName ?? "Metric")} averaging in warning zone ({avg})";

                    entries.Add(new AskComparisonEntry
                    {
                        Target = grp.Key,
                        Status = status,
                        Metrics = serverMetrics,
                        Note = note
                    });
                }

                return entries.Count > 0 ? entries : null;
            }

            return null; // Single item without History shape — no comparison
        }

        // ── Standard multi-item comparison ──
        var standardEntries = new List<AskComparisonEntry>();
        foreach (var item in result.Items)
        {
            var itemStatus = string.Equals(item.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase) ? "ok" : "critical";
            string? note = null;
            Dictionary<string, object?>? itemMetrics = null;

            if (item.Rows is { Count: > 0 })
            {
                itemMetrics = [];
                var row = item.Rows[0];
                foreach (var kv in row)
                {
                    if (IsSkippableColumn(kv.Key) || kv.Value is null) continue;
                    if (double.TryParse(kv.Value.ToString(), System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var numVal))
                    {
                        itemMetrics[kv.Key] = Math.Round(numVal, 2);
                        var metricStatus = ClassifyMetricStatus(kv.Key, numVal);
                        if (metricStatus is "warning" or "critical")
                            itemStatus = metricStatus;
                    }
                }
                if (itemMetrics.Count == 0) itemMetrics = null;
            }
            else if (string.Equals(item.Status, "FAILED", StringComparison.OrdinalIgnoreCase))
            {
                note = item.Rows?.FirstOrDefault()?.TryGetValue("ErrorMessage", out var e) == true
                    ? e?.ToString() : "Execution failed";
            }

            standardEntries.Add(new AskComparisonEntry
            {
                Target = item.Target ?? "unknown",
                Status = itemStatus,
                Metrics = itemMetrics,
                Note = note
            });
        }

        return standardEntries.Count > 0 ? standardEntries : null;
    }

    /// <summary>Builds actionable recommendations from result state.</summary>
    private static List<AskRecommendation>? BuildRecommendationsFromResult(
        AskResponseResult result, string? severity, string[] highlights)
    {
        var recs = new List<AskRecommendation>();

        // Failed targets
        var failedTargets = result.Items?
            .Where(i => string.Equals(i.Status, "FAILED", StringComparison.OrdinalIgnoreCase))
            .Select(i => i.Target)
            .ToList();
        if (failedTargets is { Count: > 0 })
            recs.Add(new AskRecommendation
            {
                Text = $"Investigate connectivity issues on {string.Join(", ", failedTargets)} — execution failed on these targets.",
                Priority = "high"
            });

        // ── History time-series: per-server metric analysis ──
        var successItem = result.Items?.FirstOrDefault(
            i => string.Equals(i.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase) && i.Rows is { Count: > 0 });
        if (successItem?.Rows is { Count: > 0 }
            && successItem.Rows[0].ContainsKey("ServerName")
            && successItem.Rows[0].ContainsKey("MetricValue")
            && successItem.Rows[0].ContainsKey("MetricName"))
        {
            var metricName = successItem.Rows[0].TryGetValue("MetricName", out var mn) ? mn?.ToString() : null;

            var byServer = successItem.Rows
                .Where(r => r.TryGetValue("ServerName", out var s) && s is not null)
                .GroupBy(r => r["ServerName"]!.ToString()!, StringComparer.OrdinalIgnoreCase);

            foreach (var grp in byServer)
            {
                var vals = grp
                    .Select(r => r.TryGetValue("MetricValue", out var mv) && mv is not null
                        && double.TryParse(mv.ToString(), System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : (double?)null)
                    .Where(v => v.HasValue)
                    .Select(v => v!.Value)
                    .ToList();
                if (vals.Count == 0) continue;

                var avg = vals.Average();
                var max = vals.Max();
                var min = vals.Min();
                // Build threshold-aware recommendation text
                var status = ClassifyHistoryMetricValue(metricName, avg);
                var maxStatus = ClassifyHistoryMetricValue(metricName, max);
                if (maxStatus is "critical" && status is not "critical") status = "warning";
                var metricDisplay = metricName is not null ? HumanizeColumnName(metricName) : "metric";

                if (string.Equals(status, "critical", StringComparison.Ordinal))
                {
                    recs.Add(new AskRecommendation
                    {
                        Text = $"CRITICAL: {grp.Key} — {metricDisplay} averaging {avg:F1} (min {min:F1}, max {max:F0}). "
                            + "Investigate immediately — check top processes, runaway queries, or resource contention.",
                        Priority = "critical"
                    });
                }
                else if (string.Equals(status, "warning", StringComparison.Ordinal))
                {
                    recs.Add(new AskRecommendation
                    {
                        Text = $"WARNING: {grp.Key} — {metricDisplay} averaging {avg:F1} (peak {max:F0}). "
                            + "Monitor closely and investigate if values continue rising.",
                        Priority = "high"
                    });
                }
            }
        }

        // Warning highlights → recommendations
        foreach (var h in highlights.Take(3))
        {
            if (h.Contains("HIGH", StringComparison.OrdinalIgnoreCase) || h.Contains("FAILED", StringComparison.OrdinalIgnoreCase))
                recs.Add(new AskRecommendation { Text = $"Review: {h}", Priority = "high" });
            else if (h.Contains("Stopped", StringComparison.OrdinalIgnoreCase) || h.Contains("OFFLINE", StringComparison.OrdinalIgnoreCase))
                recs.Add(new AskRecommendation { Text = $"Take action: {h}", Priority = "critical" });
        }

        // Severity-based default
        if (recs.Count == 0)
        {
            recs.Add(severity switch
            {
                "CRITICAL" => new AskRecommendation { Text = "Immediate investigation recommended — critical issues detected.", Priority = "critical" },
                "WARNING" => new AskRecommendation { Text = "Review flagged items and monitor for further degradation.", Priority = "medium" },
                _ => new AskRecommendation { Text = "Continue routine monitoring. All values appear within normal ranges.", Priority = "low" }
            });
        }

        return recs;
    }

    private static bool IsSkippableColumn(string colName) =>
        colName.Equals("ServerName", StringComparison.OrdinalIgnoreCase)
        || colName.Equals("CapturedAt", StringComparison.OrdinalIgnoreCase)
        || colName.Equals("CapturedAtUtc", StringComparison.OrdinalIgnoreCase)
        || colName.Equals("MetricGroup", StringComparison.OrdinalIgnoreCase)
        || colName.Equals("MetricName", StringComparison.OrdinalIgnoreCase)
        || colName.Equals("Detail", StringComparison.OrdinalIgnoreCase)
        || colName.Equals("ErrorMessage", StringComparison.OrdinalIgnoreCase)
        || colName.Equals("Status", StringComparison.OrdinalIgnoreCase)
        || colName.Equals("DatabaseName", StringComparison.OrdinalIgnoreCase)
        || colName.Equals("JobName", StringComparison.OrdinalIgnoreCase);

    private static string ClassifyMetricStatus(string colName, double value)
    {
        var col = colName.ToLowerInvariant();
        if (col.Contains("percent") || col.Contains("_pct") || col.Contains("cpu") || col.Contains("usage"))
            return value >= 90 ? "critical" : value >= 80 ? "warning" : "ok";
        if (col.Contains("pagelifeexpectancy") || col.Contains("ple"))
            return value < 300 ? "critical" : value < 1000 ? "warning" : "ok";
        if (col.Contains("freespace") || col.Contains("free_gb") || col.Contains("availablemb"))
            return value < 1 ? "critical" : value < 5 ? "warning" : "ok";
        if (col.Contains("error") || col.Contains("fail"))
            return value > 0 ? "warning" : "ok";
        return "info";
    }

    private static string HumanizeColumnName(string colName)
    {
        // Insert spaces before capitals: "PageLifeExpectancy" → "Page Life Expectancy"
        var spaced = Regex.Replace(colName, @"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");
        return spaced.Replace('_', ' ').Trim();
    }

    private static string FormatMetricValue(double value, string colName)
    {
        if (value == Math.Floor(value) && Math.Abs(value) < 1_000_000)
            return ((long)value).ToString();
        return value.ToString("N2", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? InferMetricUnit(string colName)
    {
        var col = colName.ToLowerInvariant();
        if (col.Contains("percent") || col.Contains("_pct") || col.Contains("usage")) return "percent";
        if (col.Contains("_gb") || col.Contains("sizegb")) return "gb";
        if (col.Contains("_mb") || col.Contains("sizemb") || col.Contains("availablemb")) return "mb";
        if (col.Contains("ms") || col.Contains("latency")) return "ms";
        if (col.Contains("seconds") || col.Contains("ple") || col.Contains("pagelife")) return "seconds";
        if (col.Contains("count") || col.Contains("total") || col.Contains("rows")) return "count";
        return null;
    }

    /// <summary>Splits a paragraph into individual bullet sentences.</summary>
    private static List<string> SplitIntoBullets(string text)
    {
        var sentences = text.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 5)
            .Select(s => s + ".")
            .Take(5)
            .ToList();
        return sentences.Count > 0 ? sentences : [text];
    }

    // ── Chart plan generation (History environments only) ──────────────────

    private async Task<(AskChartDetails? ChartDetails, AskDataProfile? DataProfile, AskChartSummary? ChartSummary)>
        GenerateChartPlanAsync(
        AskApiRequest request,
        string tunedQuestion,
        AskResponseResult result,
        CancellationToken cancellationToken)
    {
        if (!EnvironmentRules.IsHistory(request.Environment))
            return (null, null, null);

        if (result.Items is null or { Count: 0 })
            return (new AskChartDetails { Enabled = false, Reason = "No result rows available for charting." }, null, null);

        // Collect available field names from the first successful item
        var firstItem = result.Items.FirstOrDefault(i =>
            string.Equals(i.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase)
            && i.Rows is { Count: > 0 });

        if (firstItem?.Rows is null or { Count: 0 })
            return (new AskChartDetails { Enabled = false, Reason = "No successful rows available for charting." }, null, null);

        var availableFields = firstItem.Rows[0].Keys.ToList();

        // Build data profile and chart summary
        var profile = BuildDataProfile(result);
        var chartSummary = BuildChartSummary(request, result, profile);

        // Try deterministic chart builder first (rule-based, no LLM needed)
        var deterministicPlan = BuildDeterministicChartPlan(
            profile, availableFields, result,
            request.MetricLabel, request.MetricKey, request.MetricGroup);

        _logger.LogDebug("Deterministic chart plan: enabled={Enabled}, chartable={Chartable}, charts={Count}, reason={Reason}",
            deterministicPlan.Enabled, deterministicPlan.Chartable, deterministicPlan.Charts.Count, deterministicPlan.Reason);

        if (deterministicPlan.Enabled && deterministicPlan.Charts.Count > 0)
        {
            var hasForecast = deterministicPlan.Charts.Any(c => c.ChartType == "forecastLine");
            _logger.LogDebug("Using deterministic chart plan: {Count} charts [{ChartIds}], default={DefaultChartId}, hasForecast={HasForecast}",
                deterministicPlan.Charts.Count,
                string.Join(", ", deterministicPlan.Charts.Select(c => $"{c.ChartId}({c.ChartType})")),
                deterministicPlan.DefaultChartId,
                hasForecast);

            if (!hasForecast && deterministicPlan.ForecastSkipReason is not null)
                _logger.LogDebug("Forecast skipped: {Reason}", deterministicPlan.ForecastSkipReason);

            // Log anomaly diagnostic
            var anomalyChart = deterministicPlan.Charts.FirstOrDefault(c => c.ChartType == "anomalyLine");
            if (anomalyChart?.Anomaly is not null && !anomalyChart.Anomaly.HasAnomalies)
                _logger.LogDebug("No anomalies detected: {Reason}", anomalyChart.Anomaly.NoAnomalyReason);

            return (deterministicPlan, profile, chartSummary);
        }

        // If deterministic builder says not chartable, trust it
        if (!deterministicPlan.Chartable)
        {
            _logger.LogDebug("Data not chartable: {Reason}", deterministicPlan.Reason);
            return (deterministicPlan, profile, chartSummary);
        }

        // Fallback: try LLM chart plan
        var dataSample = BuildDataSample(result);
        var totalRowCount = result.Summary?.TotalRowCount ?? firstItem.RowCount;

        var answerSummary = result.Summary is not null
            ? $"success={result.Summary.SuccessCount}, fail={result.Summary.FailCount}, rows={result.Summary.TotalRowCount}"
            : string.Empty;

        var explainModel = _modelSelector.SelectExplainModel();

        try
        {
            var metricContext = string.Empty;
            if (!string.IsNullOrWhiteSpace(request.MetricKey))
            {
                metricContext = $"""
                METRIC_KEY: {request.MetricKey}
                METRIC_LABEL: {request.MetricLabel ?? request.MetricKey}
                METRIC_GROUP: {request.MetricGroup ?? ""}
                """;
            }

            var chartPrompt = PromptTemplates.ChartPlan
                .Replace("{{$environment}}", request.Environment ?? string.Empty, StringComparison.Ordinal)
                .Replace("{{$question}}", tunedQuestion ?? request.Question ?? string.Empty, StringComparison.Ordinal)
                .Replace("{{$answerSummary}}", answerSummary, StringComparison.Ordinal)
                .Replace("{{$availableFields}}", string.Join(", ", availableFields), StringComparison.Ordinal)
                .Replace("{{$rowCount}}", totalRowCount.ToString(), StringComparison.Ordinal)
                .Replace("{{$dataSample}}", dataSample, StringComparison.Ordinal)
                .Replace("{{$metricContext}}", metricContext, StringComparison.Ordinal);

            var chartClient = ResolveClient(explainModel.Provider);
            var raw = await chartClient.GenerateAsync(
                chartPrompt,
                tunedQuestion ?? string.Empty,
                request.Environment ?? string.Empty,
                explainModel.ModelKey,
                cancellationToken,
                apiKey: explainModel.ApiKeyEncrypted);

            var parsed = TryParseChartPlanJson(raw, availableFields);
            if (parsed is not null)
            {
                _logger.LogDebug("LLM chart plan parsed: {Count} charts", parsed.Charts.Count);
                return (parsed, profile, chartSummary);
            }

            _logger.LogWarning("Chart plan LLM response could not be parsed; using deterministic fallback.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chart plan generation failed; using deterministic fallback.");
        }

        // Mock fallback
        var mockRaw = MockLlmBehavior.BuildChartPlanJson(
            request.Environment ?? string.Empty, tunedQuestion ?? request.Question ?? string.Empty, availableFields,
            request.MetricLabel, request.MetricKey, request.MetricGroup);
        var mockParsed = TryParseChartPlanJson(mockRaw, availableFields)
            ?? new AskChartDetails { Enabled = false, Reason = "Chart plan generation failed." };
        _logger.LogDebug("Mock chart plan fallback: enabled={Enabled}, charts={Count}",
            mockParsed.Enabled, mockParsed.Charts.Count);
        return (mockParsed, profile, chartSummary);
    }

    internal static AskChartDetails? TryParseChartPlanJson(string? raw, IReadOnlyList<string> availableFields)
    {
        var text = (raw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
            return null;

        // Strip markdown fences
        var fenced = System.Text.RegularExpressions.Regex.Match(
            text, @"```(?:json)?\s*([\s\S]*?)```", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (fenced.Success)
            text = fenced.Groups[1].Value.Trim();

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            var root = doc.RootElement;

            var enabled = root.TryGetProperty("enabled", out var ep) && ep.ValueKind == System.Text.Json.JsonValueKind.True;
            var reason = root.TryGetProperty("reason", out var rp) && rp.ValueKind == System.Text.Json.JsonValueKind.String
                ? rp.GetString()
                : null;

            if (!enabled)
                return new AskChartDetails { Enabled = false, Reason = reason ?? "Charts not applicable." };

            var charts = new List<AskChartDefinition>();

            if (root.TryGetProperty("charts", out var cp) && cp.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var allowedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "area", "anomalyLine", "heatmap", "bar", "pie", "forecastLine" };
                var fieldSet = new HashSet<string>(availableFields, StringComparer.OrdinalIgnoreCase);

                foreach (var ce in cp.EnumerateArray())
                {
                    if (charts.Count >= 7) break; // max 7 charts

                    var chart = ParseChartDefinition(ce, allowedTypes, fieldSet);
                    if (chart is not null)
                        charts.Add(chart);
                }
            }

            if (charts.Count == 0)
                return new AskChartDetails { Enabled = false, Reason = "No valid charts could be parsed from LLM response." };

            return new AskChartDetails { Enabled = true, Charts = charts };
        }
        catch
        {
            return null;
        }
    }

    private static AskChartDefinition? ParseChartDefinition(
        System.Text.Json.JsonElement el,
        HashSet<string> allowedTypes,
        HashSet<string> validFields)
    {
        string? GetStr(string name) =>
            el.TryGetProperty(name, out var p) && p.ValueKind == System.Text.Json.JsonValueKind.String
                ? p.GetString()
                : null;

        bool GetBool(string name) =>
            el.TryGetProperty(name, out var p) && p.ValueKind == System.Text.Json.JsonValueKind.True;

        int GetInt(string name, int fallback) =>
            el.TryGetProperty(name, out var p) && p.ValueKind == System.Text.Json.JsonValueKind.Number
                ? p.GetInt32()
                : fallback;

        var chartType = GetStr("chartType") ?? string.Empty;
        if (!allowedTypes.Contains(chartType))
            return null;

        // Validate field references against actual result columns
        string? ValidateField(string name)
        {
            var val = GetStr(name);
            return val is not null && validFields.Contains(val) ? val : null;
        }

        var chart = new AskChartDefinition
        {
            ChartId = GetStr("chartId") ?? "chart",
            ChartType = chartType,
            Priority = GetInt("priority", 1),
            Title = GetStr("title") ?? "Chart",
            Subtitle = GetStr("subtitle"),
            XField = ValidateField("xField"),
            YField = ValidateField("yField"),
            SeriesField = ValidateField("seriesField"),
            CategoryField = ValidateField("categoryField"),
            Stacked = GetBool("stacked"),
            ShowLegend = GetBool("showLegend"),
            ShowMarkers = GetBool("showMarkers"),
            XAxisLabel = GetStr("xAxisLabel"),
            YAxisLabel = GetStr("yAxisLabel"),
            Aggregation = GetStr("aggregation") ?? "none",
            TimeGrain = GetStr("timeGrain"),
            MetricGroup = GetStr("metricGroup"),
            MetricName = GetStr("metricName"),
            FormatHint = GetStr("formatHint"),
            Goal = GetStr("goal"),
            AllowSeriesToggle = GetBool("allowSeriesToggle"),
            AllowMaximize = GetBool("allowMaximize"),
            ValueField = ValidateField("valueField"),
            RecommendedHeight = GetStr("recommendedHeight"),
            ColorIntent = GetStr("colorIntent"),
            TimeBucketed = GetBool("timeBucketed"),
            LatestSnapshotOnly = GetBool("latestSnapshotOnly")
        };

        // Parse supportedInteractions array
        if (el.TryGetProperty("supportedInteractions", out var siEl) && siEl.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            chart.SupportedInteractions = siEl.EnumerateArray()
                .Where(e => e.ValueKind == System.Text.Json.JsonValueKind.String)
                .Select(e => e.GetString()!)
                .ToArray();
        }

        // Parse anomaly config
        if (el.TryGetProperty("anomaly", out var anomalyEl) && anomalyEl.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            string? AnomalyStr(string name) =>
                anomalyEl.TryGetProperty(name, out var p3) && p3.ValueKind == System.Text.Json.JsonValueKind.String
                    ? p3.GetString()
                    : null;

            double AnomalyNum(string name, double fallback) =>
                anomalyEl.TryGetProperty(name, out var p3) && p3.ValueKind == System.Text.Json.JsonValueKind.Number
                    ? p3.GetDouble()
                    : fallback;

            bool AnomalyBool(string name) =>
                anomalyEl.TryGetProperty(name, out var p3) && p3.ValueKind == System.Text.Json.JsonValueKind.True;

            chart.Anomaly = new AskAnomalyConfig
            {
                Method = AnomalyStr("method") ?? "zscore",
                Threshold = AnomalyNum("threshold", 2.5),
                ServerWise = AnomalyBool("serverWise"),
                HasAnomalies = AnomalyBool("hasAnomalies"),
                HasPointAnomalies = AnomalyBool("hasPointAnomalies"),
                HasPeerDeviation = AnomalyBool("hasPeerDeviation"),
                Mean = AnomalyNum("mean", 0),
                StdDev = AnomalyNum("stdDev", 0),
                ThresholdUpper = AnomalyNum("thresholdUpper", 0),
                ThresholdLower = AnomalyNum("thresholdLower", 0),
                ValueField = AnomalyStr("valueField"),
                TimeField = AnomalyStr("timeField"),
                SeriesField = AnomalyStr("seriesField"),
                Mode = AnomalyStr("mode"),
                Message = AnomalyStr("message")
            };
        }

        // Parse forecast config
        if (el.TryGetProperty("forecast", out var forecastEl) && forecastEl.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            string? ForecastStr(string name) =>
                forecastEl.TryGetProperty(name, out var p4) && p4.ValueKind == System.Text.Json.JsonValueKind.String
                    ? p4.GetString()
                    : null;

            double ForecastNum(string name, double fallback) =>
                forecastEl.TryGetProperty(name, out var p4) && p4.ValueKind == System.Text.Json.JsonValueKind.Number
                    ? p4.GetDouble()
                    : fallback;

            int ForecastInt(string name, int fallback) =>
                forecastEl.TryGetProperty(name, out var p4) && p4.ValueKind == System.Text.Json.JsonValueKind.Number
                    ? p4.GetInt32()
                    : fallback;

            chart.Forecast = new AskForecastConfig
            {
                Horizon = ForecastStr("horizon") ?? "6months",
                Method = ForecastStr("method") ?? "linear_regression",
                ConfidenceLevel = ForecastNum("confidenceLevel", 0.95),
                ForecastStartUtc = ForecastStr("forecastStartUtc"),
                Slope = ForecastNum("slope", 0),
                Intercept = ForecastNum("intercept", 0),
                RSquared = ForecastNum("rSquared", 0),
                HistoricalPointCount = ForecastInt("historicalPointCount", 0)
            };
        }

        // Parse transform object
        if (el.TryGetProperty("transform", out var transformEl) && transformEl.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            string? TransformStr(string name) =>
                transformEl.TryGetProperty(name, out var p2) && p2.ValueKind == System.Text.Json.JsonValueKind.String
                    ? p2.GetString()
                    : null;

            var transform = new AskChartTransform
            {
                Type = TransformStr("type") ?? string.Empty,
                AggregateField = TransformStr("aggregateField"),
                AggregateFn = TransformStr("aggregateFn"),
                BucketField = TransformStr("bucketField"),
                BucketSize = TransformStr("bucketSize")
            };

            if (transformEl.TryGetProperty("groupBy", out var gbEl) && gbEl.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                transform.GroupBy = gbEl.EnumerateArray()
                    .Where(e => e.ValueKind == System.Text.Json.JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToArray();
            }

            if (!string.IsNullOrWhiteSpace(transform.Type))
                chart.Transform = transform;
        }

        // Parse filters: Dictionary<string, string[]>
        if (el.TryGetProperty("filters", out var filtersEl) && filtersEl.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            var filters = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in filtersEl.EnumerateObject())
            {
                if (prop.Value.ValueKind != System.Text.Json.JsonValueKind.Array)
                    continue;
                var values = prop.Value.EnumerateArray()
                    .Where(e => e.ValueKind == System.Text.Json.JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToArray();
                if (values.Length > 0)
                    filters[prop.Name] = values;
            }

            if (filters.Count > 0)
                chart.Filters = filters;
        }

        return chart;
    }

    // ── Data profile and chart summary builders ──────────────────────────

    internal static AskDataProfile BuildDataProfile(AskResponseResult result)
    {
        var profile = new AskDataProfile();

        if (result.Items is null or { Count: 0 })
            return profile;

        var firstSuccess = result.Items.FirstOrDefault(i =>
            string.Equals(i.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase)
            && i.Rows is { Count: > 0 });

        if (firstSuccess?.Rows is null or { Count: 0 })
            return profile;

        // Collect all rows across all successful items
        var allRows = result.Items
            .Where(i => string.Equals(i.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase) && i.Rows is { Count: > 0 })
            .SelectMany(i => i.Rows!)
            .ToList();

        profile.RowCount = allRows.Count;

        var fields = firstSuccess.Rows[0].Keys.ToList();

        // Detect time field
        var timeFields = new[] { "CapturedAtUtc", "CapturedAt", "CollectedAtUtc", "Timestamp", "EventTime", "StartTime", "EndTime" };
        var timeField = fields.FirstOrDefault(f => timeFields.Any(tf => f.Equals(tf, StringComparison.OrdinalIgnoreCase)));
        if (timeField is not null)
        {
            profile.HasTimeField = true;
            profile.TimeField = timeField;
        }

        // Detect numeric metric field
        var numericFields = new[] { "MetricValue", "Value", "Count", "Total", "Average", "Max", "Min", "Duration", "Size" };
        var numericField = fields.FirstOrDefault(f => numericFields.Any(nf => f.Equals(nf, StringComparison.OrdinalIgnoreCase)));
        if (numericField is not null)
        {
            // Verify at least one row has a non-null numeric value
            var hasNumeric = allRows.Any(r =>
                r.TryGetValue(numericField, out var v) && v is not null && v is not DBNull
                && double.TryParse(v.ToString(), out _));
            if (hasNumeric)
            {
                profile.HasNumericMetric = true;
                profile.NumericField = numericField;
            }
        }

        // Detect series field (ServerName, MetricName, etc.)
        var seriesFields = new[] { "ServerName", "InstanceName", "MachineName", "HostName", "ComputerName" };
        var seriesField = fields.FirstOrDefault(f => seriesFields.Any(sf => f.Equals(sf, StringComparison.OrdinalIgnoreCase)));
        if (seriesField is not null)
        {
            profile.HasSeriesField = true;
            profile.SeriesField = seriesField;

            // Count distinct servers
            profile.DistinctServers = allRows
                .Select(r => r.TryGetValue(seriesField, out var v) ? v?.ToString() : null)
                .Where(v => v is not null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
        }

        // Count distinct metric names
        var metricNameField = fields.FirstOrDefault(f => f.Equals("MetricName", StringComparison.OrdinalIgnoreCase));
        if (metricNameField is not null)
        {
            profile.DistinctMetricNames = allRows
                .Select(r => r.TryGetValue(metricNameField, out var v) ? v?.ToString() : null)
                .Where(v => v is not null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
        }

        // Determine profile type
        profile.ProfileType = (profile.HasTimeField, profile.HasNumericMetric, profile.HasSeriesField, profile.DistinctServers) switch
        {
            (true, true, true, > 1) => "time_series_multi_server",
            (true, true, _, _) => "time_series_single_server",
            (false, true, true, _) => "category",
            (false, false, _, _) => "status",
            _ => "unknown"
        };

        return profile;
    }

    internal static AskChartSummary? BuildChartSummary(
        AskApiRequest request, AskResponseResult result, AskDataProfile profile)
    {
        // Only produce summary when there's chartable data
        if (!profile.HasNumericMetric)
            return null;

        var summary = new AskChartSummary
        {
            PrimaryMetric = request.MetricKey,
            PrimaryMetricLabel = request.MetricLabel,
        };

        // Infer unit from metric key
        if (!string.IsNullOrWhiteSpace(request.MetricKey))
        {
            var key = request.MetricKey;
            if (key.Contains("Percent", StringComparison.OrdinalIgnoreCase) || key.Contains("_Pct", StringComparison.OrdinalIgnoreCase))
                summary.Unit = "percent";
            else if (key.Contains("_GB", StringComparison.OrdinalIgnoreCase) || key.Contains("SizeGB", StringComparison.OrdinalIgnoreCase))
                summary.Unit = "gb";
            else if (key.Contains("_MB", StringComparison.OrdinalIgnoreCase) || key.Contains("SizeMB", StringComparison.OrdinalIgnoreCase))
                summary.Unit = "mb";
            else if (key.Contains("Ms", StringComparison.OrdinalIgnoreCase) || key.Contains("Latency", StringComparison.OrdinalIgnoreCase))
                summary.Unit = "milliseconds";
            else if (key.Contains("Seconds", StringComparison.OrdinalIgnoreCase) || key.Contains("Duration", StringComparison.OrdinalIgnoreCase))
                summary.Unit = "seconds";
            else if (key.Contains("Count", StringComparison.OrdinalIgnoreCase) || key.Contains("Requests", StringComparison.OrdinalIgnoreCase))
                summary.Unit = "count";
            else if (key.Contains("Rate", StringComparison.OrdinalIgnoreCase) || key.Contains("PerSec", StringComparison.OrdinalIgnoreCase))
                summary.Unit = "rate";
            else
                summary.Unit = "number";
        }

        var allRows = result.Items?
            .Where(i => string.Equals(i.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase) && i.Rows is { Count: > 0 })
            .SelectMany(i => i.Rows!)
            .ToList();

        if (allRows is not { Count: > 0 })
            return summary;

        // Helper: compute trend + volatility for a list of values
        static (string Trend, string Volatility) ComputeTrendAndVolatility(List<double> values)
        {
            if (values.Count < 4) return ("unknown", "low");

            var half = values.Count / 2;
            var firstHalfAvg = values.Take(half).Average();
            var secondHalfAvg = values.Skip(half).Average();
            var changePct = firstHalfAvg == 0 ? 0 : (secondHalfAvg - firstHalfAvg) / Math.Abs(firstHalfAvg) * 100;

            // Recovering: was declining in first half but rising in second half (check quarters)
            var trend = changePct switch
            {
                > 15 => "increasing",
                < -15 => "decreasing",
                _ => "stable"
            };

            // Check for recovering pattern: first quarter declining, last quarter rising
            if (values.Count >= 8 && trend == "stable")
            {
                var q1Avg = values.Take(values.Count / 4).Average();
                var q2Avg = values.Skip(values.Count / 4).Take(values.Count / 4).Average();
                var q4Avg = values.Skip(3 * values.Count / 4).Average();
                if (q2Avg < q1Avg * 0.9 && q4Avg > q2Avg * 1.1)
                    trend = "recovering";
            }

            // Volatility: coefficient of variation
            var mean = values.Average();
            var stdDev = Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / values.Count);
            var cv = mean != 0 ? stdDev / Math.Abs(mean) * 100 : 0;
            var volatility = cv switch
            {
                > 30 => "high",
                > 10 => "medium",
                _ => "low"
            };

            return (trend, volatility);
        }

        // Compute highest/lowest series and per-server summaries
        if (profile is { HasSeriesField: true, DistinctServers: > 1 } && profile.NumericField is not null)
        {
            var serverData = allRows
                .Where(r => r.TryGetValue(profile.SeriesField!, out var sv) && sv is not null
                         && r.TryGetValue(profile.NumericField, out var mv) && mv is not null
                         && double.TryParse(mv.ToString(), out _))
                .GroupBy(r => r[profile.SeriesField!]?.ToString() ?? "", StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var vals = g.Select(r => double.Parse(r[profile.NumericField]!.ToString()!)).ToList();
                    var avg = vals.Average();
                    var (trend, vol) = ComputeTrendAndVolatility(vals);
                    return new
                    {
                        Server = g.Key, Avg = avg, Values = vals,
                        Latest = vals[^1], Min = vals.Min(), Max = vals.Max(),
                        Trend = trend, Volatility = vol
                    };
                })
                .OrderByDescending(x => x.Avg)
                .ToList();

            if (serverData.Count > 0)
            {
                summary.HighestSeries = serverData[0].Server;
                summary.LowestSeries = serverData[^1].Server;

                // Per-server series summaries
                summary.Series = serverData.Select(s => new AskSeriesSummary
                {
                    Server = s.Server,
                    TrendDirection = s.Trend,
                    Volatility = s.Volatility,
                    LatestValue = Math.Round(s.Latest, 4),
                    AverageValue = Math.Round(s.Avg, 4),
                    MinValue = Math.Round(s.Min, 4),
                    MaxValue = Math.Round(s.Max, 4)
                }).ToList();

                // Overall trend from all values
                if (profile.HasTimeField)
                {
                    var allVals = allRows
                        .Where(r => r.TryGetValue(profile.NumericField, out var v) && v is not null && double.TryParse(v.ToString(), out _))
                        .Select(r => double.Parse(r[profile.NumericField]!.ToString()!))
                        .ToList();

                    var (overallTrend, _) = ComputeTrendAndVolatility(allVals);
                    summary.TrendDirection = overallTrend;
                }
            }
        }
        else if (profile.HasTimeField && profile.NumericField is not null)
        {
            // Single-server trend
            var numericValues = allRows
                .Where(r => r.TryGetValue(profile.NumericField, out var v) && v is not null && double.TryParse(v.ToString(), out _))
                .Select(r => double.Parse(r[profile.NumericField]!.ToString()!))
                .ToList();

            if (numericValues.Count >= 4)
            {
                var (trend, volatility) = ComputeTrendAndVolatility(numericValues);
                summary.TrendDirection = trend;

                // Single server — still provide a series entry
                summary.Series =
                [
                    new AskSeriesSummary
                    {
                        Server = profile.SeriesField is not null ? "(single)" : "(all)",
                        TrendDirection = trend,
                        Volatility = volatility,
                        LatestValue = Math.Round(numericValues[^1], 4),
                        AverageValue = Math.Round(numericValues.Average(), 4),
                        MinValue = Math.Round(numericValues.Min(), 4),
                        MaxValue = Math.Round(numericValues.Max(), 4)
                    }
                ];
            }
        }

        return summary;
    }

    /// <summary>
    /// Rule-based deterministic chart plan builder. Inspects the DataProfile and produces
    /// charts in priority order: area → anomalyLine → heatmap → bar → pie.
    /// Only includes chart types that are valid for the data shape.
    /// </summary>
    internal static AskChartDetails BuildDeterministicChartPlan(
        AskDataProfile profile, IReadOnlyList<string> availableFields,
        AskResponseResult? result = null,
        string? metricLabel = null, string? metricKey = null, string? metricGroup = null)
    {
        if (!profile.HasNumericMetric || profile.RowCount == 0)
            return new AskChartDetails { Enabled = false, Chartable = false, Reason = "No numeric data available for charting." };

        // Text/status metrics are not chartable
        if (!string.IsNullOrWhiteSpace(metricKey))
        {
            var entry = SqlServerHistoryMetricMap.GetValueOrDefault(metricKey)
                        ?? WindowsHistoryMetricMap.GetValueOrDefault(metricKey);
            if (entry is not null && entry.MetricSelectSql == "NULL AS MetricValue")
                return new AskChartDetails { Enabled = false, Chartable = false, Reason = "Text/status metric is not chartable." };
        }

        var charts = new List<AskChartDefinition>();
        string? forecastSkipReason = null;
        var label = metricLabel ?? DeriveMetricLabelFromResult(result) ?? "Metric";
        var formatHint = InferFormatHint(metricKey);
        var fieldSet = new HashSet<string>(availableFields, StringComparer.OrdinalIgnoreCase);

        string? ValidField(string name) => fieldSet.Contains(name) ? name : null;

        var timeInteractions = new[] { "legendToggle", "maximize", "download", "tooltip", "seriesHideShow" };
        var staticInteractions = new[] { "maximize", "download", "tooltip" };

        // ── Multi-metric split: when data has >1 distinct MetricName, generate per-metric charts ──
        if (profile.HasTimeField && profile.TimeField is not null
            && profile.DistinctMetricNames > 1 && result is not null)
        {
            var metricNames = GetDistinctMetricNames(result);
            if (metricNames.Count > 1)
            {
                var xField = profile.TimeField;
                var yField = profile.NumericField ?? "MetricValue";
                var seriesField = profile.SeriesField;
                var hasMultiServer = profile.DistinctServers > 1;
                int priority = 0;

                foreach (var mn in metricNames)
                {
                    if (charts.Count >= 7) break; // max charts cap

                    var mLabel = HumanizeMetricName(mn);
                    var mFormatHint = InferFormatHint(mn);
                    var filter = new Dictionary<string, string[]> { ["MetricName"] = [mn] };

                    // Area trend per metric
                    priority++;
                    charts.Add(new AskChartDefinition
                    {
                        ChartId = $"area_{mn.ToLowerInvariant()}",
                        ChartType = "area",
                        Priority = priority,
                        Title = $"{mLabel} Trend",
                        Subtitle = hasMultiServer ? "Trend across selected servers" : "Trend over time",
                        XField = ValidField(xField),
                        YField = ValidField(yField),
                        SeriesField = hasMultiServer ? ValidField(seriesField!) : null,
                        ShowLegend = hasMultiServer,
                        ShowMarkers = false,
                        XAxisLabel = "Time",
                        YAxisLabel = mFormatHint == "percent" ? "%" : "Value",
                        Aggregation = "avg",
                        TimeGrain = "auto",
                        MetricGroup = metricGroup,
                        MetricName = mn,
                        FormatHint = mFormatHint,
                        Goal = "trend",
                        Filters = filter,
                        AllowSeriesToggle = hasMultiServer,
                        AllowMaximize = true,
                        SupportedInteractions = timeInteractions,
                        RecommendedHeight = "standard",
                        ColorIntent = hasMultiServer ? "categorical" : "sequential"
                    });

                    // Anomaly per metric
                    if (charts.Count >= 7) break;
                    priority++;
                    var filteredResult = FilterResultByMetricName(result, mn);
                    var anomalyChart = new AskChartDefinition
                    {
                        ChartId = $"anomaly_{mn.ToLowerInvariant()}",
                        ChartType = "anomalyLine",
                        Priority = priority,
                        Title = $"{mLabel} Anomalies",
                        Subtitle = "Spikes and drops highlighted",
                        XField = ValidField(xField),
                        YField = ValidField(yField),
                        SeriesField = hasMultiServer ? ValidField(seriesField!) : null,
                        ShowLegend = hasMultiServer,
                        ShowMarkers = true,
                        XAxisLabel = "Time",
                        YAxisLabel = mFormatHint == "percent" ? "%" : "Value",
                        Aggregation = "avg",
                        TimeGrain = "auto",
                        FormatHint = mFormatHint,
                        Goal = "anomaly",
                        Filters = filter,
                        AllowSeriesToggle = hasMultiServer,
                        AllowMaximize = true,
                        SupportedInteractions = timeInteractions,
                        RecommendedHeight = "standard",
                        ColorIntent = "alert",
                        Anomaly = ComputeAnomalyMetadata(
                            filteredResult, yField, seriesField, xField, hasMultiServer, metricKey: mn)
                    };
                    charts.Add(anomalyChart);
                }

                // One combined bar comparison (latest snapshot per server, grouped by metric)
                if (charts.Count < 7 && hasMultiServer)
                {
                    priority++;
                    charts.Add(new AskChartDefinition
                    {
                        ChartId = "bar_compare",
                        ChartType = "bar",
                        Priority = priority,
                        Title = "Latest Server Comparison",
                        Subtitle = $"Latest values by server",
                        CategoryField = ValidField(seriesField!),
                        ValueField = ValidField(yField),
                        ShowLegend = true,
                        XAxisLabel = "Server",
                        YAxisLabel = "Value",
                        Aggregation = "latest",
                        MetricGroup = metricGroup,
                        FormatHint = formatHint,
                        Goal = "comparison",
                        AllowMaximize = true,
                        LatestSnapshotOnly = true,
                        SupportedInteractions = staticInteractions,
                        RecommendedHeight = "compact",
                        ColorIntent = "categorical",
                        Transform = new AskChartTransform
                        {
                            Type = "group",
                            GroupBy = [seriesField!],
                            AggregateField = yField,
                            AggregateFn = "latest"
                        }
                    });
                }

                var multiDefault = charts.OrderBy(c => c.Priority).First();
                return new AskChartDetails
                {
                    Enabled = true,
                    Chartable = true,
                    DefaultChartId = multiDefault.ChartId,
                    Charts = charts,
                    RecommendedXField = profile.TimeField,
                    RecommendedYField = profile.NumericField,
                    RecommendedSeriesField = profile.SeriesField,
                    ForecastSkipReason = "Multi-metric data: forecast computed per individual metric if needed."
                };
            }
        }

        // ── Time-series profiles: area → anomalyLine → heatmap → bar ──
        if (profile.HasTimeField && profile.TimeField is not null)
        {
            var xField = profile.TimeField;
            var yField = profile.NumericField ?? "MetricValue";
            var seriesField = profile.SeriesField;
            var hasMultiServer = profile.DistinctServers > 1;

            // 1. Area chart (primary trend — always first for time-series)
            charts.Add(new AskChartDefinition
            {
                ChartId = "area_trend",
                ChartType = "area",
                Priority = 1,
                Title = $"{label} Trend",
                Subtitle = "Trend across selected servers",
                XField = ValidField(xField),
                YField = ValidField(yField),
                SeriesField = hasMultiServer ? ValidField(seriesField!) : null,
                ShowLegend = hasMultiServer,
                ShowMarkers = false,
                XAxisLabel = "Time",
                YAxisLabel = formatHint == "percent" ? "%" : "Value",
                Aggregation = "avg",
                TimeGrain = "auto",
                MetricGroup = metricGroup,
                MetricName = metricLabel,
                FormatHint = formatHint,
                Goal = "trend",
                AllowSeriesToggle = hasMultiServer,
                AllowMaximize = true,
                SupportedInteractions = timeInteractions,
                RecommendedHeight = "standard",
                ColorIntent = hasMultiServer ? "categorical" : "sequential"
            });

            // 2. AnomalyLine chart (time-series numeric → always applicable)
            var anomalyChart = new AskChartDefinition
            {
                ChartId = "anomaly_line",
                ChartType = "anomalyLine",
                Priority = 2,
                Title = $"{label} Anomalies",
                Subtitle = "Spikes and drops highlighted",
                XField = ValidField(xField),
                YField = ValidField(yField),
                SeriesField = hasMultiServer ? ValidField(seriesField!) : null,
                ShowLegend = hasMultiServer,
                ShowMarkers = true,
                XAxisLabel = "Time",
                YAxisLabel = formatHint == "percent" ? "%" : "Value",
                Aggregation = "avg",
                TimeGrain = "auto",
                FormatHint = formatHint,
                Goal = "anomaly",
                AllowSeriesToggle = hasMultiServer,
                AllowMaximize = true,
                SupportedInteractions = timeInteractions,
                RecommendedHeight = "standard",
                ColorIntent = "alert"
            };

            // Compute anomaly metadata from actual result rows
            if (result is not null)
            {
                anomalyChart.Anomaly = ComputeAnomalyMetadata(
                    result, yField, seriesField, xField, hasMultiServer, metricKey: metricKey);
            }
            else
            {
                anomalyChart.Anomaly = new AskAnomalyConfig
                {
                    Method = "zscore",
                    Threshold = 2.5,
                    ServerWise = hasMultiServer,
                    ValueField = yField,
                    TimeField = xField,
                    SeriesField = hasMultiServer ? seriesField : null
                };
            }

            charts.Add(anomalyChart);

            // 3. Heatmap (multi-server with enough rows — ≥6 for minimal density)
            if (hasMultiServer && profile.RowCount >= 6)
            {
                charts.Add(new AskChartDefinition
                {
                    ChartId = "heatmap_view",
                    ChartType = "heatmap",
                    Priority = 3,
                    Title = $"{label} Heatmap",
                    Subtitle = "Server vs time intensity",
                    XField = ValidField(xField),
                    YField = ValidField(seriesField!),
                    ValueField = ValidField(yField),
                    XAxisLabel = "Time",
                    YAxisLabel = "Server",
                    Aggregation = "avg",
                    TimeGrain = "15min",
                    FormatHint = formatHint,
                    Goal = "density",
                    AllowMaximize = true,
                    TimeBucketed = true,
                    SupportedInteractions = staticInteractions,
                    RecommendedHeight = "tall",
                    ColorIntent = "sequential",
                    Transform = new AskChartTransform
                    {
                        Type = "group",
                        GroupBy = [seriesField!, xField],
                        AggregateField = yField,
                        AggregateFn = "avg"
                    }
                });
            }

            // 4. Bar chart (server comparison)
            if (hasMultiServer)
            {
                charts.Add(new AskChartDefinition
                {
                    ChartId = "bar_compare",
                    ChartType = "bar",
                    Priority = 4,
                    Title = $"Latest Server Comparison",
                    Subtitle = $"Latest {label.ToLowerInvariant()} by server",
                    CategoryField = ValidField(seriesField!),
                    ValueField = ValidField(yField),
                    ShowLegend = false,
                    XAxisLabel = "Server",
                    YAxisLabel = formatHint == "percent" ? "%" : "Value",
                    Aggregation = "latest",
                    MetricGroup = metricGroup,
                    MetricName = metricLabel,
                    FormatHint = formatHint,
                    Goal = "comparison",
                    AllowMaximize = true,
                    LatestSnapshotOnly = true,
                    SupportedInteractions = staticInteractions,
                    RecommendedHeight = "compact",
                    ColorIntent = "categorical",
                    Transform = new AskChartTransform
                    {
                        Type = "group",
                        GroupBy = [seriesField!],
                        AggregateField = yField,
                        AggregateFn = "latest"
                    }
                });
            }
            else
            {
                // Single server: bar with latest snapshot
                charts.Add(new AskChartDefinition
                {
                    ChartId = "bar_compare",
                    ChartType = "bar",
                    Priority = 4,
                    Title = $"{label} Values",
                    Subtitle = "Value distribution",
                    CategoryField = ValidField(xField),
                    ValueField = ValidField(yField),
                    ShowLegend = false,
                    XAxisLabel = "Time",
                    YAxisLabel = formatHint == "percent" ? "%" : "Value",
                    Aggregation = "none",
                    FormatHint = formatHint,
                    Goal = "comparison",
                    AllowMaximize = true,
                    SupportedInteractions = staticInteractions,
                    RecommendedHeight = "compact",
                    ColorIntent = "sequential"
                });
            }

            // 5. Forecast chart (time-series with ≥10 rows and actual result data)
            if (result is null)
            {
                forecastSkipReason = "No result data available for forecast computation.";
            }
            else if (profile.RowCount < 10)
            {
                forecastSkipReason = $"Insufficient data for forecast: {profile.RowCount} rows (minimum 10 required).";
            }
            else
            {
                var forecastConfig = ComputeLinearForecast(result, yField, xField);
                if (forecastConfig is not null)
                {
                    charts.Add(new AskChartDefinition
                    {
                        ChartId = "forecast_6m",
                        ChartType = "forecastLine",
                        Priority = 5,
                        Title = $"{label} 6-Month Forecast",
                        Subtitle = "Predicted trend based on historical pattern",
                        XField = ValidField(xField),
                        YField = ValidField(yField),
                        SeriesField = hasMultiServer ? ValidField(seriesField!) : null,
                        ShowLegend = hasMultiServer,
                        ShowMarkers = false,
                        XAxisLabel = "Time",
                        YAxisLabel = formatHint == "percent" ? "%" : "Value",
                        Aggregation = "avg",
                        TimeGrain = "auto",
                        FormatHint = formatHint,
                        Goal = "forecast",
                        AllowMaximize = true,
                        Forecast = forecastConfig,
                        SupportedInteractions = timeInteractions,
                        RecommendedHeight = "tall",
                        ColorIntent = "diverging"
                    });
                }
                else
                {
                    forecastSkipReason = "Linear regression could not be computed: timestamps may not parse, time span < 1 day, or < 10 valid numeric+time points.";
                }
            }
        }
        else
        {
            // ── Non-time-series: bar → pie ──
            var yField = profile.NumericField ?? "MetricValue";
            var catField = profile.SeriesField ?? "MetricName";

            // 1. Bar chart for ranking/comparison
            charts.Add(new AskChartDefinition
            {
                ChartId = "bar_compare",
                ChartType = "bar",
                Priority = 1,
                Title = $"{label} Ranking",
                Subtitle = "Top values",
                CategoryField = ValidField(catField),
                ValueField = ValidField(yField),
                ShowLegend = false,
                XAxisLabel = catField,
                YAxisLabel = "Value",
                Aggregation = "max",
                FormatHint = formatHint,
                Goal = "ranking",
                AllowMaximize = true,
                LatestSnapshotOnly = true,
                SupportedInteractions = staticInteractions,
                RecommendedHeight = "standard",
                ColorIntent = "categorical"
            });

            // 2. Pie chart (only when categorical composition makes sense)
            if (profile.HasSeriesField && profile.DistinctServers > 1 && profile.DistinctServers <= 12)
            {
                charts.Add(new AskChartDefinition
                {
                    ChartId = "pie_share",
                    ChartType = "pie",
                    Priority = 5,
                    Title = $"{label} Share",
                    Subtitle = "Distribution by server",
                    CategoryField = ValidField(profile.SeriesField!),
                    ValueField = ValidField(yField),
                    ShowLegend = true,
                    Aggregation = "sum",
                    FormatHint = formatHint,
                    Goal = "composition",
                    AllowMaximize = true,
                    SupportedInteractions = staticInteractions,
                    RecommendedHeight = "compact",
                    ColorIntent = "categorical",
                    Transform = new AskChartTransform
                    {
                        Type = "group",
                        GroupBy = [profile.SeriesField!],
                        AggregateField = yField,
                        AggregateFn = "sum"
                    }
                });
            }
        }

        var defaultChart = charts.OrderBy(c => c.Priority).First();

        return new AskChartDetails
        {
            Enabled = true,
            Chartable = true,
            DefaultChartId = defaultChart.ChartId,
            Charts = charts,
            RecommendedXField = profile.TimeField,
            RecommendedYField = profile.NumericField,
            RecommendedSeriesField = profile.SeriesField,
            ForecastSkipReason = forecastSkipReason
        };
    }

    /// <summary>
    /// Computes anomaly metadata from actual result rows using per-server rolling baseline.
    /// Key invariants:
    /// - hasAnomalies is NEVER true when points is empty
    /// - hasPointAnomalies = true only when real timestamped anomaly points exist
    /// - hasPeerDeviation = true only when servers differ significantly in scale
    /// - peer_deviation never creates plot points
    /// </summary>
    internal static AskAnomalyConfig ComputeAnomalyMetadata(
        AskResponseResult result,
        string numericField,
        string? seriesField,
        string? timeField,
        bool serverWise,
        double threshold = 2.5,
        string? metricKey = null)
    {
        var config = new AskAnomalyConfig
        {
            Method = "zscore",
            Threshold = threshold,
            ServerWise = serverWise,
            ValueField = numericField,
            TimeField = timeField,
            SeriesField = seriesField
        };

        // ── Compute series findings (the engine that detects all anomaly types) ──
        var hasOpThreshold = metricKey is not null && MetricThresholdRegistry.ContainsKey(metricKey);
        var seriesFindings = ComputeSeriesFindings(result, numericField, seriesField, timeField, metricKey, threshold);

        config.Mode = hasOpThreshold ? "risk_and_statistical" : "statistical";
        config.SeriesFindings = seriesFindings.Count > 0 ? seriesFindings : null;

        // Populate operational thresholds in response
        if (hasOpThreshold && MetricThresholdRegistry.TryGetValue(metricKey!, out var opTh))
        {
            config.Thresholds = new AskMetricThresholds
            {
                WarningLow = opTh.WarningLow,
                CriticalLow = opTh.CriticalLow,
                WarningHigh = opTh.WarningHigh,
                CriticalHigh = opTh.CriticalHigh
            };
        }

        // ── Collect real timestamped anomaly points from findings ──
        // peer_deviation findings have no points (by design)
        var allAnomalyPoints = new List<AskAnomalyPoint>();
        var hasPeerDev = false;
        if (seriesFindings.Count > 0)
        {
            foreach (var finding in seriesFindings)
            {
                if (finding.Types.Contains("peer_deviation"))
                    hasPeerDev = true;

                // Only merge real timestamped points (from spike/drop/trend_break/sustained_low/threshold_breach)
                foreach (var pt in finding.Points)
                {
                    if (!allAnomalyPoints.Any(p => p.Timestamp == pt.Timestamp && p.Server == pt.Server))
                        allAnomalyPoints.Add(pt);
                }
            }
        }

        config.Points = allAnomalyPoints;
        config.HasPointAnomalies = allAnomalyPoints.Count > 0;
        config.HasPeerDeviation = hasPeerDev;

        // INVARIANT: hasAnomalies is ONLY true when we have actual points
        config.HasAnomalies = allAnomalyPoints.Count > 0;

        // Set method based on what was found
        if (config.HasPointAnomalies && hasOpThreshold)
            config.Method = "hybrid";
        else if (config.HasPointAnomalies)
            config.Method = "zscore";

        // ── Compute global stats for baseline metadata ──
        var allRows = result.Items?
            .Where(i => string.Equals(i.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase) && i.Rows is { Count: > 0 })
            .SelectMany(i => i.Rows!)
            .ToList();

        if (allRows is not null && allRows.Count >= 3)
        {
            var globalValues = allRows
                .Select(r =>
                {
                    double val = 0;
                    return r.TryGetValue(numericField, out var mv) && mv is not null && double.TryParse(mv.ToString(), out val)
                        ? (Value: val, Valid: true) : (Value: 0.0, Valid: false);
                })
                .Where(dp => dp.Valid)
                .Select(dp => dp.Value)
                .ToList();

            if (globalValues.Count >= 3)
            {
                var globalMean = globalValues.Average();
                var globalStdDev = Math.Sqrt(globalValues.Sum(v => (v - globalMean) * (v - globalMean)) / globalValues.Count);

                config.Mean = Math.Round(globalMean, 4);
                config.StdDev = Math.Round(globalStdDev, 4);

                if (globalStdDev >= 0.0001)
                {
                    config.ThresholdUpper = Math.Round(globalMean + threshold * globalStdDev, 4);
                    config.ThresholdLower = Math.Round(globalMean - threshold * globalStdDev, 4);
                }
            }
        }

        // ── Scale profile detection ──
        config.ScaleProfile = ComputeScaleProfile(result, numericField, seriesField);

        // ── Message / NoAnomalyReason ──
        if (config.HasPointAnomalies)
        {
            var pointTypes = allAnomalyPoints.Select(p => p.Type).Where(t => t is not null).Distinct().ToList();
            config.Message = $"{allAnomalyPoints.Count} anomaly point(s) detected: {string.Join(", ", pointTypes)}.";
        }
        else if (hasPeerDev)
        {
            config.Message = "Peer deviation detected across servers, but no individual timestamped anomalies found.";
            config.NoAnomalyReason = "No significant point anomalies detected. Peer deviation exists (servers have different scales).";
        }
        else
        {
            // Compute maxZ for diagnostic
            var maxZ = 0.0;
            if (allRows is not null)
            {
                var dataPoints = allRows
                    .Select(r =>
                    {
                        var server = seriesField is not null && r.TryGetValue(seriesField, out var sv) ? sv?.ToString() : null;
                        double val = 0;
                        var hasVal = r.TryGetValue(numericField, out var mv) && mv is not null && double.TryParse(mv.ToString(), out val);
                        return hasVal ? (Server: server, Value: val, Valid: true) : (Server: (string?)null, Value: 0.0, Valid: false);
                    })
                    .Where(dp => dp.Valid)
                    .ToList();

                if (serverWise && seriesField is not null)
                {
                    foreach (var group in dataPoints.GroupBy(dp => dp.Server ?? "", StringComparer.OrdinalIgnoreCase))
                    {
                        var vals = group.Select(dp => dp.Value).ToList();
                        if (vals.Count < 3) continue;
                        var m = vals.Average();
                        var s = Math.Sqrt(vals.Sum(v => (v - m) * (v - m)) / vals.Count);
                        if (s < 0.0001) continue;
                        foreach (var dp in group)
                        {
                            var z = Math.Abs((dp.Value - m) / s);
                            if (z > maxZ) maxZ = z;
                        }
                    }
                }
                else
                {
                    var vals = dataPoints.Select(dp => dp.Value).ToList();
                    if (vals.Count >= 3)
                    {
                        var m = vals.Average();
                        var s = Math.Sqrt(vals.Sum(v => (v - m) * (v - m)) / vals.Count);
                        if (s >= 0.0001)
                        {
                            foreach (var dp in dataPoints)
                            {
                                var z = Math.Abs((dp.Value - m) / s);
                                if (z > maxZ) maxZ = z;
                            }
                        }
                    }
                }
            }

            config.NoAnomalyReason = maxZ > 0
                ? $"All values within {threshold}σ (max |z|={Math.Round(maxZ, 2):F2}, threshold={threshold}). Data is stable."
                : $"Insufficient variance across data points. StdDev near zero.";
            config.Message = "No significant anomalies detected in selected period.";
        }

        return config;
    }

    /// <summary>
    /// Computes a 6-month linear regression forecast from actual result rows.
    /// Returns null if data is insufficient or has no meaningful time span.
    /// Minimum: 10 data points spanning at least 1 day.
    /// </summary>
    internal static AskForecastConfig? ComputeLinearForecast(
        AskResponseResult result,
        string numericField,
        string? timeField,
        int horizonMonths = 6,
        double confidenceLevel = 0.95)
    {
        if (timeField is null) return null;

        var allRows = result.Items?
            .Where(i => string.Equals(i.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase) && i.Rows is { Count: > 0 })
            .SelectMany(i => i.Rows!)
            .ToList();

        if (allRows is null or { Count: < 10 })
            return null;

        // Extract (timestamp, value) tuples — handle both boxed DateTime and string timestamps
        var dataPoints = allRows
            .Select(r =>
            {
                DateTime ts = default;
                double val = 0;
                var hasTs = false;
                if (r.TryGetValue(timeField, out var tv) && tv is not null)
                {
                    if (tv is DateTime dt)
                    {
                        ts = dt;
                        hasTs = true;
                    }
                    else if (tv is DateTimeOffset dto)
                    {
                        ts = dto.UtcDateTime;
                        hasTs = true;
                    }
                    else
                    {
                        hasTs = DateTime.TryParse(tv.ToString(),
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AllowWhiteSpaces,
                            out ts);
                    }
                }
                var hasVal = r.TryGetValue(numericField, out var mv) && mv is not null
                             && double.TryParse(mv.ToString(), out val);
                return (Timestamp: ts, Value: val, Valid: hasTs && hasVal);
            })
            .Where(dp => dp.Valid)
            .OrderBy(dp => dp.Timestamp)
            .ToList();

        if (dataPoints.Count < 10)
            return null;

        // Check time span — need at least 1 day of data
        var firstTs = dataPoints[0].Timestamp;
        var lastTs = dataPoints[^1].Timestamp;
        var spanDays = (lastTs - firstTs).TotalDays;
        if (spanDays < 1.0)
            return null;

        // Linear regression: y = slope * x + intercept
        // x = days since first timestamp
        var n = dataPoints.Count;
        var xs = dataPoints.Select(dp => (dp.Timestamp - firstTs).TotalDays).ToArray();
        var ys = dataPoints.Select(dp => dp.Value).ToArray();

        var sumX = xs.Sum();
        var sumY = ys.Sum();
        var sumXY = xs.Zip(ys, (x, y) => x * y).Sum();
        var sumX2 = xs.Sum(x => x * x);

        var denom = n * sumX2 - sumX * sumX;
        if (Math.Abs(denom) < 1e-10)
            return null; // Degenerate — all same timestamp

        var slope = (n * sumXY - sumX * sumY) / denom;
        var intercept = (sumY - slope * sumX) / n;

        // R² computation
        var meanY = sumY / n;
        var ssTot = ys.Sum(y => (y - meanY) * (y - meanY));
        var ssRes = xs.Zip(ys, (x, y) => { var pred = slope * x + intercept; return (y - pred) * (y - pred); }).Sum();
        var rSquared = ssTot > 1e-10 ? 1.0 - ssRes / ssTot : 0.0;

        // Standard error for prediction intervals
        var sErr = n > 2 ? Math.Sqrt(ssRes / (n - 2)) : 0.0;

        // t-value for 95% CI with n-2 degrees of freedom (approximate for large n)
        // For n≥30 use 1.96; for smaller n use a conservative 2.0
        var tValue = n >= 30 ? 1.96 : 2.0;

        // Generate forecast points: monthly intervals for horizonMonths
        var forecastStart = lastTs.AddHours(1); // Start just after last actual
        var points = new List<AskForecastPoint>();

        for (int m = 0; m <= horizonMonths; m++)
        {
            var forecastTs = lastTs.AddMonths(m);
            var xDays = (forecastTs - firstTs).TotalDays;
            var predicted = slope * xDays + intercept;

            // Prediction interval width grows with distance from mean of x
            var xMean = sumX / n;
            var piWidth = tValue * sErr * Math.Sqrt(1.0 + 1.0 / n + (xDays - xMean) * (xDays - xMean) / (sumX2 - sumX * sumX / n));

            points.Add(new AskForecastPoint
            {
                Timestamp = forecastTs.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                Value = Math.Round(predicted, 4),
                Lower = Math.Round(predicted - piWidth, 4),
                Upper = Math.Round(predicted + piWidth, 4)
            });
        }

        return new AskForecastConfig
        {
            Horizon = $"{horizonMonths}months",
            Method = "linear_regression",
            ConfidenceLevel = confidenceLevel,
            ForecastStartUtc = forecastStart.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            Slope = Math.Round(slope, 6),
            Intercept = Math.Round(intercept, 4),
            RSquared = Math.Round(rSquared, 4),
            HistoricalPointCount = n,
            Points = points
        };
    }

    /// <summary>
    /// Derives a human-readable label from the MetricName column in result rows
    /// when no explicit metricLabel is provided (non-metric sample queries).
    /// Returns null if no meaningful label can be derived.
    /// </summary>
    private static string? DeriveMetricLabelFromResult(AskResponseResult? result)
    {
        if (result?.Items is null or { Count: 0 })
            return null;

        var metricNames = GetDistinctMetricNames(result);
        if (metricNames.Count == 0)
            return null;

        if (metricNames.Count == 1)
            return HumanizeMetricName(metricNames[0]);

        return string.Join(" / ", metricNames.Select(HumanizeMetricName));
    }

    /// <summary>Extracts distinct MetricName values from result rows.</summary>
    private static List<string> GetDistinctMetricNames(AskResponseResult result)
    {
        if (result.Items is null or { Count: 0 })
            return [];

        return result.Items
            .Where(i => string.Equals(i.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase) && i.Rows is { Count: > 0 })
            .SelectMany(i => i.Rows!)
            .Select(r => r.TryGetValue("MetricName", out var v) ? v?.ToString() : null)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()!;
    }

    /// <summary>Converts a metric name like "MemoryUsage" or "CPU_Memory" to "Memory Usage" or "CPU Memory".</summary>
    private static string HumanizeMetricName(string name)
    {
        // Replace underscores
        var s = name.Replace('_', ' ');
        // Insert space before uppercase letters preceded by lowercase: "MemoryUsage" → "Memory Usage"
        var sb = new System.Text.StringBuilder(s.Length + 4);
        for (int i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i]) && char.IsLower(s[i - 1]))
                sb.Append(' ');
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    /// <summary>Creates a filtered copy of a result containing only rows matching a specific MetricName.</summary>
    private static AskResponseResult FilterResultByMetricName(AskResponseResult result, string metricName)
    {
        var filtered = new AskResponseResult { Items = [] };
        if (result.Items is null) return filtered;

        foreach (var item in result.Items)
        {
            if (!string.Equals(item.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase) || item.Rows is not { Count: > 0 })
                continue;
            var rows = item.Rows
                .Where(r => r.TryGetValue("MetricName", out var v)
                             && string.Equals(v?.ToString(), metricName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (rows.Count > 0)
                filtered.Items.Add(new AskExecutionItem
                {
                    Target = item.Target,
                    Status = item.Status,
                    Rows = rows,
                    RowCount = rows.Count
                });
        }
        return filtered;
    }

    private static string InferFormatHint(string? metricKey)
    {
        if (string.IsNullOrWhiteSpace(metricKey)) return "number";
        if (metricKey.Contains("Percent", StringComparison.OrdinalIgnoreCase) || metricKey.Contains("_Pct", StringComparison.OrdinalIgnoreCase))
            return "percent";
        if (metricKey.Contains("_GB", StringComparison.OrdinalIgnoreCase)) return "gb";
        if (metricKey.Contains("_MB", StringComparison.OrdinalIgnoreCase)) return "mb";
        if (metricKey.Contains("Ms", StringComparison.OrdinalIgnoreCase) || metricKey.Contains("Latency", StringComparison.OrdinalIgnoreCase))
            return "milliseconds";
        if (metricKey.Contains("Seconds", StringComparison.OrdinalIgnoreCase)) return "seconds";
        if (metricKey.Contains("Count", StringComparison.OrdinalIgnoreCase)) return "count";
        if (metricKey.Contains("Rate", StringComparison.OrdinalIgnoreCase) || metricKey.Contains("PerSec", StringComparison.OrdinalIgnoreCase))
            return "rate";
        return "number";
    }

    private static AskAnswerNode ParseExplainResponse(
        string raw,
        LlmModelDefinition explainModel,
        string[] highlights,
        string severity,
        string? resultStatus)
    {
        var text = (raw ?? string.Empty).Trim();

        // Strip optional markdown fence.
        var fenced = System.Text.RegularExpressions.Regex.Match(
            text, @"```(?:json)?\s*([\s\S]*?)```", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (fenced.Success)
            text = fenced.Groups[1].Value.Trim();

        // If text doesn't start with '{', try to extract JSON object from preamble text.
        if (!text.StartsWith('{'))
        {
            var firstBrace = text.IndexOf('{');
            var lastBrace = text.LastIndexOf('}');
            if (firstBrace >= 0 && lastBrace > firstBrace)
                text = text[firstBrace..(lastBrace + 1)];
        }

        var answerStatus = string.Equals(resultStatus, "PARTIAL_SUCCESS", StringComparison.OrdinalIgnoreCase)
            ? "PARTIAL"
            : "OK";

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            string? GetStr(string name) =>
                root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
                    ? p.GetString()
                    : null;

            // Parse summary array
            string[]? summary = null;
            if (root.TryGetProperty("summary", out var summaryProp) && summaryProp.ValueKind == JsonValueKind.Array)
            {
                summary = summaryProp.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToArray();
                if (summary.Length == 0) summary = null;
            }

            // Parse sections array (reuse existing parser)
            var sections = ParseSections(root);

            // Parse keyMetrics array
            List<AskKeyMetric>? keyMetrics = null;
            if (root.TryGetProperty("keyMetrics", out var kmProp) && kmProp.ValueKind == JsonValueKind.Array)
            {
                keyMetrics = [];
                foreach (var km in kmProp.EnumerateArray())
                {
                    if (km.ValueKind != JsonValueKind.Object) continue;
                    var label = km.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
                    var val = km.TryGetProperty("value", out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()) : null;
                    if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(val)) continue;
                    var unit = km.TryGetProperty("unit", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                    var status = km.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() ?? "info" : "info";
                    keyMetrics.Add(new AskKeyMetric { Label = label!, Value = val!, Unit = unit, Status = status });
                }
                if (keyMetrics.Count == 0) keyMetrics = null;
            }

            // Parse recommendations array
            List<AskRecommendation>? recommendations = null;
            if (root.TryGetProperty("recommendations", out var recProp) && recProp.ValueKind == JsonValueKind.Array)
            {
                recommendations = [];
                foreach (var rec in recProp.EnumerateArray())
                {
                    if (rec.ValueKind != JsonValueKind.Object) continue;
                    var txt = rec.TryGetProperty("text", out var rt) && rt.ValueKind == JsonValueKind.String ? rt.GetString() : null;
                    if (string.IsNullOrWhiteSpace(txt)) continue;
                    var priority = rec.TryGetProperty("priority", out var rp) && rp.ValueKind == JsonValueKind.String ? rp.GetString() ?? "medium" : "medium";
                    recommendations.Add(new AskRecommendation { Text = txt!, Priority = priority });
                }
                if (recommendations.Count == 0) recommendations = null;
            }

            // Parse comparison array
            List<AskComparisonEntry>? comparison = null;
            if (root.TryGetProperty("comparison", out var cmpProp) && cmpProp.ValueKind == JsonValueKind.Array)
            {
                comparison = [];
                foreach (var cmp in cmpProp.EnumerateArray())
                {
                    if (cmp.ValueKind != JsonValueKind.Object) continue;
                    var target = cmp.TryGetProperty("target", out var ct) && ct.ValueKind == JsonValueKind.String ? ct.GetString() : null;
                    if (string.IsNullOrWhiteSpace(target)) continue;
                    var cmpStatus = cmp.TryGetProperty("status", out var cs) && cs.ValueKind == JsonValueKind.String ? cs.GetString() ?? "ok" : "ok";
                    var note = cmp.TryGetProperty("note", out var cn) && cn.ValueKind == JsonValueKind.String ? cn.GetString() : null;

                    Dictionary<string, object?>? metrics = null;
                    if (cmp.TryGetProperty("metrics", out var cm) && cm.ValueKind == JsonValueKind.Object)
                    {
                        metrics = [];
                        foreach (var prop in cm.EnumerateObject())
                        {
                            object? mVal = prop.Value.ValueKind switch
                            {
                                JsonValueKind.Number => prop.Value.GetDouble(),
                                JsonValueKind.String => prop.Value.GetString(),
                                _ => prop.Value.ToString()
                            };
                            metrics[prop.Name] = mVal;
                        }
                    }

                    comparison.Add(new AskComparisonEntry { Target = target!, Status = cmpStatus, Metrics = metrics, Note = note });
                }
                if (comparison.Count == 0) comparison = null;
            }

            return new AskAnswerNode
            {
                Status = answerStatus,
                Severity = severity,
                Model = new AskModelRef { Provider = explainModel.Provider, ModelKey = explainModel.ModelKey },
                Highlights = highlights.Length > 0 ? highlights : null,
                Title = GetStr("title"),
                Explanation = GetStr("explanation"),
                Anomaly = GetStr("anomaly"),
                Analysis = GetStr("analysis"),
                Suggestion = GetStr("suggestion"),
                RootCause = GetStr("rootCause"),
                Impact = GetStr("impact"),
                Summary = summary,
                Sections = sections,
                KeyMetrics = keyMetrics,
                Recommendations = recommendations,
                Comparison = comparison
            };
        }
        catch
        {
            // Attempt to salvage structured fields from malformed JSON using regex extraction.
            var salvaged = TrySalvageExplainJson(text);
            if (salvaged is not null)
            {
                return new AskAnswerNode
                {
                    Status = answerStatus,
                    Severity = severity,
                    Model = new AskModelRef { Provider = explainModel.Provider, ModelKey = explainModel.ModelKey },
                    Highlights = highlights.Length > 0 ? highlights : null,
                    Title = salvaged.Value.Title,
                    Explanation = salvaged.Value.Explanation,
                    Anomaly = salvaged.Value.Anomaly,
                    Analysis = salvaged.Value.Analysis,
                    Suggestion = salvaged.Value.Suggestion,
                    RootCause = salvaged.Value.RootCause,
                    Impact = salvaged.Value.Impact
                };
            }

            // LLM returned non-JSON (e.g. script code from mock fallback) — produce a sensible default.
            // NEVER put raw JSON into explanation — the UI would render it as unformatted text.
            string explanation;
            if (string.IsNullOrWhiteSpace(text) || LooksLikeScriptOutput(text) || text.TrimStart().StartsWith('{'))
                explanation = "Execution completed. See result.items for the collected data.";
            else
                explanation = text;
            return new AskAnswerNode
            {
                Status = answerStatus,
                Severity = severity,
                Model = new AskModelRef { Provider = explainModel.Provider, ModelKey = explainModel.ModelKey },
                Highlights = highlights.Length > 0 ? highlights : null,
                Explanation = explanation
            };
        }
    }


    /// <summary>
    /// Regex-based salvage for malformed JSON from the LLM explain step.
    /// Extracts key string fields even when JSON has trailing commas, truncation, etc.
    /// </summary>
    private static (string? Title, string? Explanation, string? Anomaly, string? Analysis,
        string? Suggestion, string? RootCause, string? Impact)? TrySalvageExplainJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.Contains('"'))
            return null;

        static string? ExtractField(string src, string fieldName)
        {
            // Match "fieldName": "value" — handles escaped quotes inside value
            var pattern = $@"""{fieldName}""\s*:\s*""((?:[^""\\]|\\.)*)""";
            var m = Regex.Match(src, pattern, RegexOptions.Singleline);
            return m.Success ? m.Groups[1].Value.Replace("\\\"", "\"").Replace("\\n", "\n") : null;
        }

        var title = ExtractField(text, "title");
        var explanation = ExtractField(text, "explanation");

        // Only salvage if we got at least a title or explanation
        if (title is null && explanation is null)
            return null;

        return (
            Title: title,
            Explanation: explanation,
            Anomaly: ExtractField(text, "anomaly"),
            Analysis: ExtractField(text, "analysis"),
            Suggestion: ExtractField(text, "suggestion"),
            RootCause: ExtractField(text, "rootCause"),
            Impact: ExtractField(text, "impact")
        );
    }

    private static readonly Regex ScriptOutputPattern =
        new(@"^(SELECT|WITH|DECLARE)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Returns true when <paramref name="text"/> looks like PS or SQL script rather than an explanation.</summary>
    private static bool LooksLikeScriptOutput(string text)
    {
        var t = text.TrimStart();
        return t.StartsWith("param(", StringComparison.OrdinalIgnoreCase)
               || t.StartsWith('$')
               || ScriptOutputPattern.IsMatch(t);
    }

    /// <summary>
    /// Extracts a concise 1-2 sentence failure reason from execution results.
    /// Avoids dumping the full SQL error chain into the UI.
    /// </summary>
    private static string ExtractConciseFailReason(AskResponseResult result)
    {
        if (result.Items is null or { Count: 0 })
            return "Script execution failed on all targets. Check server connectivity.";

        // Find the first error message
        foreach (var item in result.Items)
        {
            if (!string.Equals(item.Status, "FAILED", StringComparison.OrdinalIgnoreCase))
                continue;

            var errMsg = item.Rows?.FirstOrDefault()
                ?.TryGetValue("ErrorMessage", out var e) == true ? e?.ToString() : null;
            if (string.IsNullOrWhiteSpace(errMsg))
                continue;

            // Extract the first meaningful error from the chain
            var firstError = ExtractFirstSqlError(errMsg);
            var targetCount = result.Items.Count;
            return $"Script failed on {targetCount} target(s): {firstError}";
        }

        return "Script execution failed on all targets. Check server connectivity.";
    }

    /// <summary>
    /// Extracts the first meaningful SQL error message from a multi-error chain.
    /// Input like "Number=207; State=1; Line=30; Message=Invalid column name 'plan_handle'. Number=207; ..."
    /// Returns: "Invalid column name 'plan_handle' (Msg 207, Line 30)"
    /// </summary>
    private static string ExtractFirstSqlError(string fullError)
    {
        // Try to extract first "Message=..." from SQL error format
        var msgMatch = Regex.Match(fullError,
            @"Number=(\d+);\s*State=\d+;\s*Line=(\d+);\s*Message=([^.]+\.?)");
        if (msgMatch.Success)
        {
            var msgNum = msgMatch.Groups[1].Value;
            var line = msgMatch.Groups[2].Value;
            var msg = msgMatch.Groups[3].Value.Trim();
            return $"{msg} (Msg {msgNum}, Line {line})";
        }

        // Fallback: just take the first 150 chars
        return fullError.Length > 150 ? fullError[..150] + "..." : fullError;
    }

    /// <summary>
    /// Truncates a long error message for use in highlights.
    /// Shows only the first error from a multi-error chain.
    /// </summary>
    private static string TruncateErrorMessage(string errMsg)
    {
        var first = ExtractFirstSqlError(errMsg);
        return first.Length > 200 ? first[..200] + "..." : first;
    }

    private static readonly string[] IdentifierColumns =
    [
        "DeviceID", "DriveLetter", "Drive", "Volume", "Name",
        "JobName", "DatabaseName", "ServiceName", "ProcessName"
    ];

    private static string[] BuildHighlightsArray(AskResponseResult result)
    {
        if (result.Items is null or { Count: 0 })
            return [];

        var lines = new List<string>();

        foreach (var item in result.Items)
        {
            if (string.Equals(item.Status, "FAILED", StringComparison.OrdinalIgnoreCase))
            {
                var errMsg = item.Rows?.FirstOrDefault()
                    ?.TryGetValue("ErrorMessage", out var e) == true ? e?.ToString() : null;
                // Truncate long error messages — show only the first error, not the full dump.
                if (errMsg is not null)
                    errMsg = TruncateErrorMessage(errMsg);
                lines.Add($"{item.Target}: execution failed{(errMsg is null ? string.Empty : $" — {errMsg}")}");
                continue;
            }

            foreach (var row in item.Rows ?? [])
            {
                // Percentage-used highlight
                var pctEntry = row.FirstOrDefault(kv =>
                    kv.Key.Contains("PercentageUsed", StringComparison.OrdinalIgnoreCase)
                    || kv.Key.Contains("PercentFull", StringComparison.OrdinalIgnoreCase));

                if (pctEntry.Key is not null
                    && double.TryParse(pctEntry.Value?.ToString(), System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var pct))
                {
                    var tier = pct >= 95 ? "critical" : pct >= 80 ? "warning" : pct >= 60 ? "elevated" : "healthy";
                    var id = IdentifierColumns
                        .Select(c => row.TryGetValue(c, out var v) ? v?.ToString() : null)
                        .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
                    var label = id is null ? $"PercentageUsed" : id;
                    lines.Add($"{item.Target} {label}: is {pct:0.##}% used ({tier})");
                    continue;
                }

                // Offline / suspect / stopped state
                var stateEntry = row.FirstOrDefault(kv =>
                    string.Equals(kv.Key, "State", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(kv.Key, "ServiceStatus", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(kv.Key, "Status", StringComparison.OrdinalIgnoreCase));

                if (stateEntry.Key is not null)
                {
                    var val = stateEntry.Value?.ToString() ?? string.Empty;
                    if (string.Equals(val, "OFFLINE", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(val, "SUSPECT", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(val, "Stopped", StringComparison.OrdinalIgnoreCase))
                    {
                        lines.Add($"{item.Target}: {stateEntry.Key}={val}");
                        continue;
                    }
                }

                // Inline ErrorMessage
                if (row.TryGetValue("ErrorMessage", out var em) && !string.IsNullOrWhiteSpace(em?.ToString()))
                    lines.Add($"{item.Target}: ErrorMessage={em}");
            }

            // ── History time-series aggregation highlights ──
            if (item.Rows is { Count: > 0 }
                && item.Rows[0].ContainsKey("ServerName")
                && item.Rows[0].ContainsKey("MetricValue")
                && item.Rows[0].ContainsKey("MetricName"))
            {
                var metricName = item.Rows[0].TryGetValue("MetricName", out var mn2) ? mn2?.ToString() : null;

                var byServer = item.Rows
                    .Where(r => r.TryGetValue("ServerName", out var s) && s is not null)
                    .GroupBy(r => r["ServerName"]!.ToString()!, StringComparer.OrdinalIgnoreCase);

                foreach (var grp in byServer)
                {
                    var vals = grp
                        .Select(r => r.TryGetValue("MetricValue", out var mv) && mv is not null
                            && double.TryParse(mv.ToString(), System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : (double?)null)
                        .Where(v => v.HasValue)
                        .Select(v => v!.Value)
                        .ToList();
                    if (vals.Count == 0) continue;

                    var avg = vals.Average();
                    var status = ClassifyHistoryMetricValue(metricName, avg);
                    if (string.Equals(status, "critical", StringComparison.Ordinal))
                        lines.Add($"{grp.Key}: {metricName ?? "metric"} avg={avg:F1} (critical)");
                    else if (string.Equals(status, "warning", StringComparison.Ordinal))
                        lines.Add($"{grp.Key}: {metricName ?? "metric"} avg={avg:F1} (warning)");
                }
            }
        }

        return [.. lines];
    }

    private static string DeriveSeverity(AskResponseResult result, string[] highlights)
    {
        if (string.Equals(result.Status, "FAILED", StringComparison.OrdinalIgnoreCase))
            return "UNKNOWN";

        if (highlights.Length == 0)
            return "OK";

        if (highlights.Any(h => h.Contains("(critical)", StringComparison.OrdinalIgnoreCase)
            || h.Contains("OFFLINE", StringComparison.OrdinalIgnoreCase)
            || h.Contains("SUSPECT", StringComparison.OrdinalIgnoreCase)
            || h.Contains("execution failed", StringComparison.OrdinalIgnoreCase)))
            return "CRITICAL";

        if (highlights.Any(h => h.Contains("(warning)", StringComparison.OrdinalIgnoreCase)
            || h.Contains("Stopped", StringComparison.OrdinalIgnoreCase)
            || h.Contains("ErrorMessage=", StringComparison.OrdinalIgnoreCase)))
            return "WARNING";

        if (highlights.Any(h => h.Contains("(elevated)", StringComparison.OrdinalIgnoreCase)))
            return "INFO";

        return "OK";
    }

    private static string GenerateHighlights(AskResponseResult result)
    {
        if (result.Items is null or { Count: 0 })
            return "(none)";

        var lines = new List<string>();

        foreach (var item in result.Items)
        {
            if (string.Equals(item.Status, "FAILED", StringComparison.OrdinalIgnoreCase))
            {
                var errMsg = item.Rows?.FirstOrDefault()
                    ?.TryGetValue("ErrorMessage", out var e) == true ? e?.ToString() : null;
                lines.Add($"[{item.Target}] FAILED: {errMsg}");
                continue;
            }

            foreach (var row in item.Rows ?? [])
            {
                foreach (var kv in row)
                {
                    var key = kv.Key;
                    var val = kv.Value?.ToString() ?? string.Empty;

                    if (key.Contains("PercentageUsed", StringComparison.OrdinalIgnoreCase)
                        && double.TryParse(val, System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var pct)
                        && pct >= 90)
                    {
                        lines.Add($"[{item.Target}] HIGH {key}={val}%");
                        continue;
                    }

                    if ((string.Equals(key, "State", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(key, "ServiceStatus", StringComparison.OrdinalIgnoreCase))
                        && (string.Equals(val, "OFFLINE", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(val, "SUSPECT", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(val, "Stopped", StringComparison.OrdinalIgnoreCase)))
                    {
                        lines.Add($"[{item.Target}] {key}={val}");
                        continue;
                    }

                    if (string.Equals(key, "ErrorMessage", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(val))
                    {
                        lines.Add($"[{item.Target}] ErrorMessage={val}");
                    }
                }
            }
        }

        return lines.Count == 0 ? "(none)" : string.Join(Environment.NewLine, lines);
    }

    private static string BuildDataSample(AskResponseResult result)
    {
        if (result.Items is null or { Count: 0 })
            return "(no data)";

        const int MaxRowsPerTarget = 20;
        var sb = new System.Text.StringBuilder();

        foreach (var item in result.Items)
        {
            sb.Append('[').Append(item.Target).AppendLine("]");
            var rows = item.Rows;
            if (rows is null or { Count: 0 })
                continue;

            // ── History time-series aggregation: when data has ServerName + MetricValue,
            //    produce per-server stats so the LLM can analyse trends without seeing all rows.
            var hasHistoryShape = rows[0].ContainsKey("ServerName")
                && rows[0].ContainsKey("MetricValue");
            if (hasHistoryShape && rows.Count > MaxRowsPerTarget)
            {
                sb.AppendLine("  [PER-SERVER AGGREGATION]");
                var byServer = rows
                    .Where(r => r.TryGetValue("ServerName", out var s) && s is not null)
                    .GroupBy(r => r["ServerName"]!.ToString()!, StringComparer.OrdinalIgnoreCase);

                foreach (var grp in byServer.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var vals = grp
                        .Select(r => r.TryGetValue("MetricValue", out var mv) && mv is not null
                            && double.TryParse(mv.ToString(), System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : (double?)null)
                        .Where(v => v.HasValue)
                        .Select(v => v!.Value)
                        .ToList();

                    if (vals.Count == 0) continue;

                    var metricName = grp.First().TryGetValue("MetricName", out var mn) ? mn?.ToString() : null;
                    var metricGroup = grp.First().TryGetValue("MetricGroup", out var mg) ? mg?.ToString() : null;

                    sb.Append($"  {grp.Key}: count={vals.Count}");
                    sb.Append($", avg={vals.Average():F1}");
                    sb.Append($", min={vals.Min():F1}");
                    sb.Append($", max={vals.Max():F1}");
                    sb.Append($", p95={Percentile(vals, 0.95):F1}");

                    // Count how many readings are in warning/critical zones
                    var above80 = vals.Count(v => v >= 80);
                    var above90 = vals.Count(v => v >= 90);
                    if (above80 > 0) sb.Append($", above80%={above80}");
                    if (above90 > 0) sb.Append($", above90%={above90}");

                    if (metricName is not null) sb.Append($", metric={metricName}");
                    if (metricGroup is not null) sb.Append($", group={metricGroup}");
                    sb.AppendLine();
                }
                sb.AppendLine();
            }

            var capped = rows.Count > MaxRowsPerTarget ? rows.Take(MaxRowsPerTarget).ToList() : rows;
            foreach (var row in capped)
            {
                sb.Append("  { ");
                sb.Append(string.Join(", ", row.Select(kv => $"{kv.Key}={kv.Value}")));
                sb.AppendLine(" }");
            }

            if (rows.Count > MaxRowsPerTarget)
                sb.AppendLine($"  ... ({rows.Count - MaxRowsPerTarget} more rows truncated)");
        }

        return sb.ToString().Trim();
    }

    /// <summary>Computes the p-th percentile of a sorted list of values.</summary>
    private static double Percentile(List<double> values, double p)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        var idx = p * (sorted.Count - 1);
        var lo = (int)Math.Floor(idx);
        var hi = (int)Math.Ceiling(idx);
        return lo == hi ? sorted[lo] : sorted[lo] + (idx - lo) * (sorted[hi] - sorted[lo]);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  FLEET DRIFT ENGINE
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Detects whether the result data is drift/compare format (Category + SettingName + CurrentValue
    /// from multiple servers).
    /// </summary>
    internal static bool IsDriftData(AskResponseResult result)
    {
        if (result.Items is null || result.Items.Count < 2)
            return false;

        var targetsWithDriftSchema = 0;
        foreach (var item in result.Items)
        {
            if (item.Rows is { Count: > 0 }
                && item.Rows[0].ContainsKey("SettingName")
                && item.Rows[0].ContainsKey("CurrentValue"))
                targetsWithDriftSchema++;
        }
        return targetsWithDriftSchema >= 2;
    }

    /// <summary>
    /// Phase 1: Extract and pivot all rows into (Category, SettingName) → {server → value} map.
    /// </summary>
    internal static (List<string> Servers, Dictionary<(string Category, string Setting), Dictionary<string, string>> Pivot,
        Dictionary<(string Category, string Setting), string> Descriptions)
        ExtractDriftPivot(AskResponseResult result)
    {
        var servers = new List<string>();
        var pivot = new Dictionary<(string Category, string Setting), Dictionary<string, string>>();
        var descriptions = new Dictionary<(string Category, string Setting), string>();

        foreach (var item in result.Items!)
        {
            if (item.Rows is null) continue;
            var server = item.Target ?? "Unknown";
            if (!servers.Contains(server)) servers.Add(server);

            foreach (var row in item.Rows)
            {
                var category = row.TryGetValue("Category", out var c) ? c?.ToString() ?? "" : "";
                var setting = row.TryGetValue("SettingName", out var s) ? s?.ToString() ?? "" : "";
                var value = row.TryGetValue("CurrentValue", out var v) ? v?.ToString() ?? "<null>" : "<null>";

                if (string.IsNullOrWhiteSpace(setting)) continue;

                var key = (category, setting);
                if (!pivot.TryGetValue(key, out var dict))
                {
                    dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    pivot[key] = dict;
                }
                dict[server] = value;

                if (row.TryGetValue("Description", out var d) && d is not null)
                    descriptions.TryAdd(key, d.ToString()!);
            }
        }

        return (servers, pivot, descriptions);
    }

    /// <summary>
    /// Phase 2-4: Group by Category+SettingName, detect drift, compute majority/outliers, score severity.
    /// Returns (AskDriftReport, AskAnswerNode).
    /// </summary>
    internal static (AskDriftReport Report, AskAnswerNode Answer) BuildFleetDriftReport(AskResponseResult result)
    {
        var (servers, pivot, descriptions) = ExtractDriftPivot(result);

        // Build drift items with outlier detection
        var driftItems = new List<AskDriftItem>();
        foreach (var (key, values) in pivot)
        {
            var distinctValues = values.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (distinctValues.Count <= 1) continue;

            // Majority detection: which value do most servers share?
            var majorityGroup = values.GroupBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .First();
            var majorityValue = majorityGroup.Key;
            var hasClearMajority = majorityGroup.Count() > 1 || servers.Count == 2;

            var outlierServers = hasClearMajority
                ? values.Where(kv => !string.Equals(kv.Value, majorityValue, StringComparison.OrdinalIgnoreCase))
                    .Select(kv => kv.Key).ToList()
                : null;

            var severity = ClassifyDriftSeverity(key.Category, key.Setting, values);

            var serverValues = servers.Select(srv => new AskDriftServerValue
            {
                Server = srv,
                Value = values.GetValueOrDefault(srv, "<missing>"),
                IsOutlier = outlierServers?.Contains(srv) ?? false
            }).ToList();

            driftItems.Add(new AskDriftItem
            {
                Category = key.Category,
                SettingName = key.Setting,
                Severity = severity,
                SeverityHint = severity,
                Servers = serverValues,
                DistinctValues = distinctValues,
                MajorityValue = hasClearMajority ? majorityValue : null,
                OutlierServers = outlierServers is { Count: > 0 } ? outlierServers : null,
                Recommendation = BuildDriftRecommendation(key.Setting, severity, values, servers, majorityValue),
                Description = descriptions.GetValueOrDefault(key)
            });
        }

        // Sort: Critical first, then High, Medium, Low
        driftItems = driftItems.OrderBy(d => d.Severity switch { "Critical" => 0, "High" => 1, "Medium" => 2, _ => 3 })
            .ThenBy(d => d.Category).ThenBy(d => d.SettingName).ToList();

        var criticalCount = driftItems.Count(d => d.Severity == "Critical");
        var highCount = driftItems.Count(d => d.Severity == "High");
        var mediumCount = driftItems.Count(d => d.Severity == "Medium");
        var totalDrift = driftItems.Count;
        var alignedCount = pivot.Count - totalDrift;

        // Build report
        var report = new AskDriftReport
        {
            Summary = new AskDriftSummary
            {
                TotalServers = servers.Count,
                TotalSettingsCompared = pivot.Count,
                DriftCount = totalDrift,
                CriticalCount = criticalCount,
                HighCount = highCount,
                MediumCount = mediumCount
            },
            DriftItems = driftItems,
            AlignedSettings = alignedCount,
            Visuals = BuildDriftVisuals(driftItems, servers, pivot)
        };

        // Build answer node
        var overallSeverity = criticalCount > 0 ? "CRITICAL"
            : highCount > 0 ? "WARNING"
            : mediumCount > 0 ? "INFO"
            : "OK";

        var title = totalDrift == 0
            ? $"No Configuration Drift — {servers.Count} Servers Are Consistent"
            : $"Configuration Drift Detected: {totalDrift} Differences Across {servers.Count} Servers";

        var explanation = totalDrift == 0
            ? $"All {pivot.Count} configuration settings match across {string.Join(" and ", servers)}. No drift detected."
            : $"Compared {pivot.Count} settings across {string.Join(", ", servers)}. Found {totalDrift} differences: {criticalCount} critical, {highCount} high, {mediumCount} medium.";

        // Key metrics
        var keyMetrics = new List<AskKeyMetric>
        {
            new() { Label = "Servers Compared", Value = servers.Count.ToString(), Unit = "count", Status = "info" },
            new() { Label = "Total Settings", Value = pivot.Count.ToString(), Unit = "count", Status = "info" },
            new() { Label = "Settings with Drift", Value = totalDrift.ToString(), Unit = "count",
                     Status = totalDrift == 0 ? "ok" : totalDrift > 5 ? "warning" : "info" },
            new() { Label = "Critical Drift", Value = criticalCount.ToString(), Unit = "count",
                     Status = criticalCount > 0 ? "critical" : "ok" },
            new() { Label = "Aligned Settings", Value = alignedCount.ToString(), Unit = "count", Status = "ok" }
        };

        // Sections
        var sections = new List<AnswerSection>();

        // 1. Drift Summary
        var summaryBullets = new List<string>();
        if (totalDrift == 0)
        {
            summaryBullets.Add($"All {pivot.Count} settings are identical across all {servers.Count} servers.");
            summaryBullets.Add("No remediation needed — servers are consistently configured.");
        }
        else
        {
            if (criticalCount > 0)
                summaryBullets.Add($"{criticalCount} CRITICAL difference(s) found — these can cause performance or reliability issues.");
            if (highCount > 0)
                summaryBullets.Add($"{highCount} HIGH severity difference(s) detected — review recommended.");
            summaryBullets.Add($"{alignedCount} of {pivot.Count} settings match across all servers.");
        }
        sections.Add(new AnswerSection
        {
            Key = "findings",
            Title = "Drift Summary",
            Icon = "Search",
            Tone = overallSeverity switch { "CRITICAL" => "critical", "WARNING" => "warning", _ => totalDrift > 0 ? "info" : "ok" },
            Bullets = summaryBullets
        });

        // 2. Drift Items (top 12 by severity)
        var driftBullets = new List<string>();
        foreach (var item in driftItems.Take(12))
        {
            var vals = string.Join(", ", item.Servers.Select(sv => $"{sv.Server}={sv.Value}"));
            var outlierTag = item.OutlierServers is { Count: > 0 }
                ? $" — Outlier: {string.Join(", ", item.OutlierServers)}"
                : "";
            driftBullets.Add($"[{item.Severity}] {item.SettingName}: {vals}{outlierTag}");
        }
        if (driftBullets.Count > 0)
        {
            if (totalDrift > 12)
                driftBullets.Add($"... and {totalDrift - 12} more differences.");
            sections.Add(new AnswerSection
            {
                Key = "drift_analysis",
                Title = "Configuration Differences",
                Icon = "Compass",
                Tone = criticalCount > 0 ? "critical" : highCount > 0 ? "warning" : "info",
                Bullets = driftBullets
            });
        }

        // 3. Per-category breakdown
        var catGroups = driftItems.GroupBy(d => d.Category).ToList();
        if (catGroups.Count > 0)
        {
            var catBullets = catGroups.OrderByDescending(g => g.Count()).Take(6)
                .Select(g =>
                {
                    var catCrit = g.Count(d => d.Severity == "Critical");
                    var catHigh = g.Count(d => d.Severity == "High");
                    var suffix = catCrit > 0 ? $" ({catCrit} critical)" : catHigh > 0 ? $" ({catHigh} high)" : "";
                    return $"{g.Key}: {g.Count()} difference(s){suffix}";
                }).ToList();
            sections.Add(new AnswerSection
            {
                Key = "category_breakdown",
                Title = "Drift by Category",
                Icon = "Database",
                Tone = "info",
                Bullets = catBullets
            });
        }

        // 4. Per-server outlier status
        var serverBullets = new List<string>();
        foreach (var server in servers)
        {
            var outlierCount = driftItems.Count(d => d.OutlierServers?.Contains(server) == true);
            var critOutliers = driftItems.Count(d => d.Severity == "Critical" && d.OutlierServers?.Contains(server) == true);
            var status = critOutliers > 0 ? "CRITICAL" : outlierCount > 3 ? "WARNING" : "OK";
            serverBullets.Add($"{server}: {outlierCount} outlier deviation(s) ({status})");
        }
        sections.Add(new AnswerSection
        {
            Key = "server_breakdown",
            Title = "Peer Outlier Status",
            Icon = "Database",
            Tone = serverBullets.Any(b => b.Contains("CRITICAL")) ? "warning" : "info",
            Bullets = serverBullets
        });

        // 5. Recommendations
        var recommendations = new List<AskRecommendation>();
        var recSteps = new List<string>();
        foreach (var item in driftItems.Where(d => d.Severity is "Critical" or "High").Take(5))
        {
            recommendations.Add(new AskRecommendation
            {
                Text = item.Recommendation ?? $"Review '{item.SettingName}' difference across servers.",
                Priority = item.Severity == "Critical" ? "critical" : "high"
            });
            recSteps.Add(item.Recommendation ?? $"Review '{item.SettingName}' difference.");
        }
        if (recommendations.Count == 0 && totalDrift > 0)
        {
            recommendations.Add(new AskRecommendation { Text = $"Review {totalDrift} differences and align where appropriate.", Priority = "medium" });
            recSteps.Add($"Review {totalDrift} differences and align where appropriate.");
        }
        if (totalDrift == 0)
        {
            recommendations.Add(new AskRecommendation { Text = "No action needed. Re-run periodically to detect future drift.", Priority = "low" });
            recSteps.Add("No action needed. Re-run periodically to detect future drift.");
        }
        sections.Add(new AnswerSection
        {
            Key = "action_items",
            Title = "Recommended Actions",
            Icon = "ListChecks",
            Tone = criticalCount > 0 ? "critical" : highCount > 0 ? "warning" : "ok",
            Steps = recSteps.Take(5).ToList()
        });

        // Comparison entries (per server)
        var comparison = servers.Select(server =>
        {
            var serverMetrics = new Dictionary<string, object?>();
            foreach (var item in driftItems.Take(8))
            {
                var sv = item.Servers.FirstOrDefault(sv => sv.Server == server);
                if (sv is not null) serverMetrics[item.SettingName] = sv.Value;
            }
            var srvOutliers = driftItems.Count(d => d.OutlierServers?.Contains(server) == true);
            var srvCritical = driftItems.Count(d => d.Severity == "Critical" && d.OutlierServers?.Contains(server) == true);
            return new AskComparisonEntry
            {
                Target = server,
                Status = srvCritical > 0 ? "critical" : srvOutliers > 3 ? "warning" : "ok",
                Metrics = serverMetrics.Count > 0 ? serverMetrics : null,
                Note = totalDrift == 0 ? "All settings match" : $"{srvOutliers} outlier(s)"
            };
        }).ToList();

        var summaryArr = new List<string> { explanation };
        if (criticalCount > 0) summaryArr.Add($"{criticalCount} critical differences require immediate attention.");
        if (highCount > 0) summaryArr.Add($"{highCount} high-severity differences should be reviewed.");
        if (totalDrift == 0) summaryArr.Add("Servers are consistently configured — no action needed.");

        var answer = new AskAnswerNode
        {
            Status = overallSeverity is "CRITICAL" or "WARNING" ? "WARNING" : "OK",
            Severity = overallSeverity,
            Title = title,
            Explanation = explanation,
            Analysis = totalDrift > 0 ? $"Drift concentrated in: {string.Join(", ", catGroups.OrderByDescending(g => g.Count()).Take(3).Select(g => g.Key))}." : null,
            Anomaly = criticalCount > 0 ? $"{criticalCount} critical configuration differences that can impact performance or reliability." : null,
            Suggestion = totalDrift > 0 ? "Standardize critical settings across all servers using sp_configure or Group Policy. Schedule periodic drift checks." : null,
            RootCause = totalDrift > 0 ? "Configuration drift typically occurs from manual changes, different deployment scripts, or settings not managed by central policy." : null,
            Impact = criticalCount > 0 ? "Inconsistent settings can cause unpredictable performance, failed failovers, and difficult troubleshooting across the server fleet." : null,
            Summary = summaryArr.Take(7).ToArray(),
            KeyMetrics = keyMetrics,
            Sections = sections,
            Recommendations = recommendations,
            Comparison = comparison
        };

        return (report, answer);
    }

    /// <summary>
    /// Builds chart-ready visual data for the drift report — no text parsing needed by frontend.
    /// </summary>
    internal static AskDriftVisuals BuildDriftVisuals(
        List<AskDriftItem> driftItems,
        List<string> servers,
        Dictionary<(string Category, string Setting), Dictionary<string, string>> pivot)
    {
        var lowCount = driftItems.Count(d => d.Severity == "Low");

        // 1. Severity counts — donut chart
        var severityCounts = new AskDriftSeverityCounts
        {
            Critical = driftItems.Count(d => d.Severity == "Critical"),
            High = driftItems.Count(d => d.Severity == "High"),
            Medium = driftItems.Count(d => d.Severity == "Medium"),
            Low = lowCount
        };

        // 2. Category metrics — bar chart
        var categoryMetrics = driftItems
            .GroupBy(d => d.Category)
            .OrderByDescending(g => g.Count())
            .Take(10)
            .Select(g => new AskDriftCategoryMetric
            {
                Category = g.Key,
                Count = g.Count(),
                Critical = g.Count(d => d.Severity == "Critical"),
                High = g.Count(d => d.Severity == "High"),
                Medium = g.Count(d => d.Severity == "Medium"),
                Low = g.Count(d => d.Severity == "Low")
            }).ToList();

        // 3. Server metrics — per-server deviation bar chart
        var serverMetrics = servers.Select(server =>
        {
            var deviations = driftItems.Count(d => d.OutlierServers?.Contains(server) == true);
            var critDeviations = driftItems.Count(d => d.Severity == "Critical" && d.OutlierServers?.Contains(server) == true);
            return new AskDriftServerMetric
            {
                Server = server,
                DeviationCount = deviations,
                CriticalCount = critDeviations,
                Status = critDeviations > 0 ? "critical" : deviations > 3 ? "warning" : "ok"
            };
        }).ToList();

        // 4. Setting matrix — heatmap (top 20 drift items by severity)
        var settingMatrix = driftItems.Take(20).Select(item => new AskDriftSettingRow
        {
            Setting = item.SettingName,
            Category = item.Category,
            Severity = item.Severity,
            MajorityValue = item.MajorityValue,
            Values = item.Servers.ToDictionary(sv => sv.Server, sv => sv.Value)
        }).ToList();

        return new AskDriftVisuals
        {
            SeverityCounts = severityCounts,
            CategoryMetrics = categoryMetrics,
            ServerMetrics = serverMetrics,
            SettingMatrix = settingMatrix
        };
    }

    // ════════════════════════════════════════════════════════════════════════
    //  LIVE VISUALS — chart-ready data for any multi-server Live execution
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds visual data for any multi-server Live result — server status, metric matrix, KPI cards.
    /// Returns null when fewer than 2 servers or no useful data.
    /// </summary>
    internal static AskLiveVisuals? BuildLiveVisuals(AskResponseResult result)
    {
        if (result.Items is null || result.Items.Count < 2)
            return null;

        var successItems = result.Items.Where(i => i.Status == "SUCCESS" && i.Rows is { Count: > 0 }).ToList();
        if (successItems.Count < 2)
            return null;

        // 1. Server status strip
        var serverStatus = result.Items.Select(item => new AskLiveServerStatus
        {
            Server = item.Target ?? "Unknown",
            Status = item.Status ?? "UNKNOWN",
            RowCount = item.RowCount
        }).ToList();

        // 2. Status distribution — donut chart
        var statusCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in result.Items)
        {
            var st = item.Status ?? "UNKNOWN";
            statusCounts.TryGetValue(st, out var cnt);
            statusCounts[st] = cnt + 1;
        }

        // 3. KPI cards
        var kpiCards = new List<AskLiveKpiCard>
        {
            new() { Label = "Servers", Value = result.Items.Count.ToString(), Unit = "count", Status = "info" },
            new() { Label = "Successful", Value = successItems.Count.ToString(), Unit = "count",
                     Status = successItems.Count == result.Items.Count ? "ok" : "warning" },
            new() { Label = "Total Rows", Value = result.Items.Sum(i => i.RowCount).ToString(), Unit = "count", Status = "info" }
        };

        // 4. Metric matrix — find numeric columns common across servers for grouped bar / heatmap
        var metricMatrix = BuildLiveMetricMatrix(successItems);

        return new AskLiveVisuals
        {
            ServerStatus = serverStatus,
            StatusCounts = statusCounts.Count > 0 ? statusCounts : null,
            KpiCards = kpiCards,
            MetricMatrix = metricMatrix is { Count: > 0 } ? metricMatrix : null
        };
    }

    /// <summary>
    /// Extracts numeric columns shared across servers into a metric × server matrix.
    /// Used for grouped bar charts and comparison heatmaps.
    /// </summary>
    private static List<AskLiveMetricRow>? BuildLiveMetricMatrix(List<AskExecutionItem> items)
    {
        // Collect all column names and check which have numeric values across multiple servers
        var columnSamples = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            if (item.Rows is null) continue;
            var server = item.Target ?? "Unknown";

            foreach (var row in item.Rows)
            {
                foreach (var (col, val) in row)
                {
                    if (val is null) continue;
                    var valStr = val.ToString() ?? "";

                    // Skip non-metric columns
                    if (col.Equals("ServerName", StringComparison.OrdinalIgnoreCase) ||
                        col.Equals("CapturedAtUtc", StringComparison.OrdinalIgnoreCase) ||
                        col.Equals("Category", StringComparison.OrdinalIgnoreCase) ||
                        col.Equals("SettingName", StringComparison.OrdinalIgnoreCase) ||
                        col.Equals("Description", StringComparison.OrdinalIgnoreCase) ||
                        col.Equals("ConfigStatus", StringComparison.OrdinalIgnoreCase) ||
                        col.Equals("ErrorMessage", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!columnSamples.TryGetValue(col, out var serverValues))
                    {
                        serverValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        columnSamples[col] = serverValues;
                    }

                    // Keep first value per server per column (or aggregate later)
                    serverValues.TryAdd(server, valStr);
                }
            }
        }

        // Filter to columns with numeric-looking values from 2+ servers
        var matrix = new List<AskLiveMetricRow>();
        foreach (var (col, serverValues) in columnSamples)
        {
            if (serverValues.Count < 2) continue;

            // Check if at least half the values look numeric
            var numericCount = serverValues.Values.Count(v => double.TryParse(v, out _));
            if (numericCount < serverValues.Count / 2.0) continue;

            var unit = InferMetricUnit(col);
            matrix.Add(new AskLiveMetricRow
            {
                Metric = col,
                Unit = unit,
                Values = new Dictionary<string, string>(serverValues)
            });
        }

        return matrix.Count > 0 ? matrix.Take(20).ToList() : null;
    }

    /// <summary>
    /// Builds a pivot-format data sample for sending drift items to LLM.
    /// Only includes items where isDrift=true.
    /// </summary>
    internal static string BuildDriftPromptInput(AskDriftReport report, List<string> servers)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"SERVERS: {string.Join(", ", servers)}");
        sb.AppendLine($"TOTAL SETTINGS COMPARED: {report.Summary.TotalSettingsCompared}");
        sb.AppendLine($"DRIFT COUNT: {report.Summary.DriftCount} (Critical={report.Summary.CriticalCount}, High={report.Summary.HighCount}, Medium={report.Summary.MediumCount})");
        sb.AppendLine($"ALIGNED: {report.AlignedSettings}");
        sb.AppendLine();

        if (report.DriftItems.Count == 0)
        {
            sb.AppendLine("NO DRIFT DETECTED — all settings match across all servers.");
            return sb.ToString().Trim();
        }

        sb.AppendLine("DRIFT ITEMS (only differing settings):");
        foreach (var item in report.DriftItems)
        {
            sb.Append($"  [{item.Severity}] {item.Category} / {item.SettingName}: ");
            sb.Append(string.Join(" | ", item.Servers.Select(sv => $"{sv.Server}={sv.Value}{(sv.IsOutlier ? " [OUTLIER]" : "")}")));
            if (item.MajorityValue is not null)
                sb.Append($" (majority={item.MajorityValue})");
            sb.AppendLine();
        }

        return sb.ToString().Trim();
    }

    /// <summary>Builds a deterministic recommendation for a drift item.</summary>
    private static string BuildDriftRecommendation(string settingName, string severity, Dictionary<string, string> values, List<string> servers, string majorityValue)
    {
        var name = settingName.ToLowerInvariant();

        if (name.Contains("max degree of parallelism") || name.Contains("maxdop"))
            return "Standardize MAXDOP across peer production servers unless workload-specific exception exists. MAXDOP=0 allows unlimited parallelism and should be avoided.";
        if (name.Contains("max server memory"))
            return "Align max server memory based on each server's physical RAM. Leave 4-6 GB for the OS. Inconsistent memory caps cause unpredictable failover behavior.";
        if (name.Contains("cost threshold"))
            return "Standardize cost threshold for parallelism. Default of 5 is generally too low for OLTP workloads. Consider 50 as a starting point.";
        if (name.Contains("recoverymodel"))
            return "Ensure recovery model matches backup/DR strategy. SIMPLE loses point-in-time recovery. All production databases should typically use FULL.";
        if (name.Contains("autoshrink"))
            return "URGENT: Disable auto-shrink on all servers. Auto-shrink causes severe index fragmentation and performance degradation.";
        if (name.Contains("trustworthy"))
            return "URGENT: Disable TRUSTWORTHY unless explicitly required. It enables privilege escalation from db_owner to sysadmin.";
        if (name.Contains("backup compression"))
            return "Enable backup compression default across all servers to reduce backup time and storage.";
        if (name.Contains("xp_cmdshell"))
            return "Ensure xp_cmdshell is disabled on all servers unless a documented exception exists. It allows OS command execution from SQL.";
        if (name.Contains("compatibilitylevel"))
            return "Align database compatibility levels to match the SQL Server version for consistent query optimizer behavior.";
        if (name.Contains("collation"))
            return "Collation mismatch can cause tempdb spill errors in cross-database joins. Standardize collation across the fleet.";
        if (name.Contains("pageverify"))
            return "Use CHECKSUM page verify on all databases for the best corruption detection.";
        if (name.Contains("optimize for ad hoc"))
            return "Enable 'optimize for ad hoc workloads' to prevent single-use plans from bloating the plan cache.";
        if (name.Contains("enabled") && name.Contains("firewall"))
            return "URGENT: Ensure all firewall profiles are enabled. Disabled firewall on any server creates a security exposure.";
        if (name.Contains("defenderservice") || name.Contains("windefend"))
            return "URGENT: Windows Defender must be running on all servers. A stopped antivirus service is a critical security risk.";
        if (name.Contains("hotfixcount") || name.Contains("hotfix"))
            return "Patch level differences indicate inconsistent patching. Schedule a patch alignment window.";
        if (name.Contains("pendingreboot"))
            return "Schedule reboots for servers with pending reboot to apply pending OS/security updates.";
        if (name.Contains("domain") || name.Contains("partofdomain"))
            return "Domain membership mismatch is a critical security and management concern. Investigate immediately.";
        if (name.Contains("rdpenabled"))
            return "RDP exposure should be consistent. If disabled on some servers, ensure alternative remote access is available.";
        if (name.Contains("winrm"))
            return "WinRM should be enabled on all servers for remote management and monitoring.";

        return severity switch
        {
            "Critical" => $"URGENT: Align '{settingName}' across servers — inconsistency creates risk.",
            "High" => $"Review and standardize '{settingName}' across the fleet.",
            _ => $"Consider aligning '{settingName}' for consistency."
        };
    }

    // ── SEVERITY CLASSIFICATION ─────────────────────────────────────────

    /// <summary>Classifies drift severity based on category, setting name, and actual values.</summary>
    internal static string ClassifyDriftSeverity(string category, string settingName, Dictionary<string, string> values)
    {
        var name = settingName.ToLowerInvariant();
        var cat = category.ToLowerInvariant();

        // ── SQL Server CRITICAL ─────────────────────────────────────────
        if (name.Contains("max degree of parallelism") || name.Contains("maxdop"))
        {
            if (values.Values.Any(v => v == "0") && values.Values.Any(v => v != "0"))
                return "Critical";
            return "Critical";
        }
        if (name.Contains("cost threshold for parallelism"))
            return "Critical";
        if (name.Contains("max server memory") || name.Contains("min server memory"))
            return "Critical";
        if (name.Contains("backup compression default"))
            return "Critical";
        if (name.Contains("clr enabled"))
            return "Critical";
        if (name.Contains("xp_cmdshell"))
            return "Critical";
        if (name.Contains("contained database"))
            return "Critical";
        if (name.Contains("default trace"))
            return "Critical";
        if (name.Contains("optimize for ad hoc"))
            return "Critical";
        if (name.Contains("remote admin connections"))
            return "Critical";
        if (name.Contains("priority boost"))
            return "Critical";
        if (name.Contains("fill factor"))
            return "Critical";

        // ── Windows CRITICAL ────────────────────────────────────────────
        if (name.Contains("hotfixcount"))
            return "Critical";
        if ((name.Contains("enabled") || name.Contains(":enabled")) && cat.Contains("firewall"))
        {
            if (values.Values.Any(v => v.Equals("NO", StringComparison.OrdinalIgnoreCase)))
                return "Critical";
        }
        if (name.Contains("defenderservice") || (name.Contains("windefend") && name.Contains("status")))
        {
            if (values.Values.Any(v => !v.Equals("Running", StringComparison.OrdinalIgnoreCase) && !v.Equals("4", StringComparison.OrdinalIgnoreCase)))
                return "Critical";
        }
        if (name.Contains("rdpenabled"))
            return "Critical";
        if (name.Contains("partofdomain") || (name == "domain" && cat == "system"))
            return "Critical";

        // ── SQL Server HIGH ─────────────────────────────────────────────
        if (name.Contains("compatibilitylevel"))
            return "High";
        if (name.Contains("recoverymodel"))
            return "High";
        if (name.Contains("collation") && !name.Contains("serverproperty"))
            return "High";
        if (name.Contains("pageverify"))
            return "High";
        if (name.Contains("trustworthy"))
        {
            if (values.Values.Any(v => v.Equals("ON", StringComparison.OrdinalIgnoreCase)))
                return "Critical";
            return "High";
        }
        if (name.Contains("autoshrink"))
        {
            if (values.Values.Any(v => v.Equals("ON", StringComparison.OrdinalIgnoreCase)))
                return "Critical";
            return "High";
        }
        if (name.Contains("brokerenabled"))
            return "High";
        if (name.Contains("productversion") || (name == "edition" && cat.Contains("server")))
            return "High";
        if (name.Contains("ishadrenabled") || name.Contains("hadrmanagerstatus"))
            return "High";

        // ── Windows HIGH ────────────────────────────────────────────────
        if (name.Contains("starttype") && cat.Contains("critical"))
            return "High";
        if (name.Contains("status") && cat.Contains("critical"))
        {
            if (values.Values.Any(v => v.Equals("Stopped", StringComparison.OrdinalIgnoreCase)))
                return "High";
        }
        if (name.Contains("version") && cat == "os")
            return "High";
        if (name.Contains("buildnumber") && cat == "os")
            return "High";
        if (name.Contains("caption") && cat == "os")
            return "High";
        if (name.Contains("winrm") && name.Contains("status"))
        {
            if (values.Values.Any(v => v.Equals("Stopped", StringComparison.OrdinalIgnoreCase)))
                return "High";
        }

        // ── MEDIUM ──────────────────────────────────────────────────────
        if (name.Contains("schedulercount") || name.Contains("logicalcpu") || name.Contains("totalcores") || name.Contains("totallogicalcpu"))
            return "Medium";
        if (name.Contains("physicalmemory") || name.Contains("totalphysicalmemory"))
            return "Medium";
        if (name.Contains("tempdb") && (name.Contains("filecount") || name.Contains("datafilecount")))
            return "Medium";
        if (name.Contains("autocreatestat") || name.Contains("autoupdatestat"))
            return "Medium";
        if (name.Contains("readcommittedsnapshot") || name.Contains("querystore"))
            return "Medium";
        if (name.Contains("timezone") || name.Contains("baseutcoffset"))
            return "Medium";
        if (cat.Contains("autoservice"))
            return "Medium";
        if (name.Contains("filesystem"))
            return "Medium";
        if (name.Contains("dhcpenabled"))
            return "Medium";

        // ── LOW: expected per-server differences ────────────────────────
        if (name.Contains("servername") || name.Contains("instancename") || name.Contains("computername")
            || name.Contains("physicalnetbios") || name.Contains("processid")
            || name.Contains("sqlserverstarttimeutc") || name.Contains("lastbootuptime")
            || name.Contains("installdate") || name.Contains("serialnumber")
            || name.Contains("biosserial") || name.Contains("macaddress")
            || name.Contains("ipaddress") || name.Contains("defaultgw") || name.Contains("dnsserver")
            || name.Contains("freegb") || name.Contains("freepct") || name.Contains("capturedatutc")
            || name.Contains("hotfixid") || (cat == "patch" && !name.Contains("count")))
            return "Low";

        return "Medium";
    }

    private async Task<AskAnswerNode?> GenerateBlockExplanationAsync(
        AskApiRequest request,
        PolicyDecision decision,
        CancellationToken cancellationToken)
    {
        try
        {
            var tuneModel = _modelSelector.SelectTuneModel();
            var client = ResolveClient(tuneModel.Provider);

            var prompt = PromptTemplates.PolicyBlockExplain
                .Replace("{{$environment}}", request.Environment ?? string.Empty, StringComparison.Ordinal)
                .Replace("{{$question}}", request.Question ?? string.Empty, StringComparison.Ordinal)
                .Replace("{{$blockReason}}", decision.ReasonCode ?? "UNKNOWN", StringComparison.Ordinal)
                .Replace("{{$blockMessage}}", decision.Message ?? string.Empty, StringComparison.Ordinal);

            var raw = await client.GenerateAsync(
                prompt,
                request.Question ?? string.Empty,
                request.Environment ?? string.Empty,
                tuneModel.ModelKey,
                cancellationToken,
                apiKey: tuneModel.ApiKeyEncrypted);

            var json = raw.Trim();
            // Strip markdown fences if present
            if (json.StartsWith("```", StringComparison.Ordinal))
            {
                var firstNewline = json.IndexOf('\n');
                if (firstNewline > 0) json = json[(firstNewline + 1)..];
                if (json.EndsWith("```", StringComparison.Ordinal))
                    json = json[..^3].TrimEnd();
            }

            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new AskAnswerNode
            {
                Status = "BLOCKED",
                Severity = decision.ReasonCode == "DANGEROUS_ACTION" ? "CRITICAL" : "WARNING",
                Title = root.TryGetProperty("title", out var t) ? t.GetString() : null,
                Explanation = root.TryGetProperty("explanation", out var e) ? e.GetString() : decision.Message,
                Suggestion = root.TryGetProperty("suggestion", out var s) ? s.GetString() : null
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate LLM block explanation, falling back to static message");
            return new AskAnswerNode
            {
                Status = "BLOCKED",
                Severity = "WARNING",
                Explanation = decision.Message
            };
        }
    }

    private static AskApiResponse CreatePolicyBlockedResponse(
        AskApiRequest request,
        PolicyDecision decision,
        AskAnswerNode? blockExplanation = null)
    {
        var env = request.Environment ?? string.Empty;
        var isClarify = decision.NeedsClarification;
        var tuningStatus = isClarify ? "NEEDS_CLARIFICATION" : "BLOCKED";
        var resultStatus = isClarify ? "NEEDS_CLARIFICATION" : "STOPPED";
        var message = isClarify
            ? decision.Message ?? "Please clarify your request."
            : $"BLOCKED: {decision.ReasonCode} - {decision.Message}";

        // Build explanation with suggestions for NEEDS_CLARIFICATION
        var answerText = message;
        if (isClarify && decision.ClarifySuggestion1 is not null)
        {
            answerText = $"{message}\n\n1) {decision.ClarifySuggestion1}\n2) {decision.ClarifySuggestion2}";
        }

        return new AskApiResponse
        {
            Meta = new AskResponseMeta
            {
                ConversationId = request.ConversationId ?? string.Empty,
                BearerToken = request.BearerToken ?? string.Empty,
                TimestampUtc = DateTime.UtcNow.ToString("o")
            },
            Request = new AskResponseRequest
            {
                Environment = env,
                Question = request.Question ?? string.Empty,
                SelectedTargets = request.SelectedTargets ?? [],
                TargetType = EnvironmentRules.IsSqlServer(env) ? "SqlServer"
                           : EnvironmentRules.IsWindows(env) ? "Windows"
                           : null
            },
            Tuning = new AskResponseTuning
            {
                TunedQuestion = request.Question ?? string.Empty,
                Status = tuningStatus,
                StopReason = message
            },
            Plan = new AskResponsePlan
            {
                Mode = "ANSWER_ONLY"
            },
            Result = new AskResponseResult
            {
                Kind = "ANSWER_ONLY",
                Status = resultStatus,
                AnswerText = answerText
            },
            Answer = isClarify
                ? new AskAnswerNode
                {
                    Status = "NEEDS_CLARIFICATION",
                    Explanation = message,
                    Suggestion = decision.ClarifySuggestion1 is not null
                        ? $"1) {decision.ClarifySuggestion1}\n2) {decision.ClarifySuggestion2}"
                        : null
                }
                : blockExplanation
        };
    }

    private static AskApiResponse CreateStoppedResponse(
        AskApiRequest request,
        string tunedQuestion,
        string message,
        LlmModelDefinition tuneModel,
        LlmModelDefinition planModel,
        LlmModelDefinition generateModel)
    {
        var response = CreateBaseResponse(request, tunedQuestion, tuneModel, planModel, generateModel);
        response.Tuning.Status = "STOPPED";
        response.Tuning.StopReason = message;
        response.Plan.Mode = "STOPPED";

        if (EnvironmentRules.IsGeneral(request.Environment))
        {
            response.Result.Kind = "ANSWER_ONLY";
            response.Result.Status = "STOPPED";
            response.Result.AnswerText = message;
            return response;
        }

        response.Result.Kind = "EXECUTION";
        response.Result.Status = "STOPPED";
        return response;
    }

    private static AskApiResponse CreateBaseResponse(
        AskApiRequest request,
        string tunedQuestion,
        LlmModelDefinition tuneModel,
        LlmModelDefinition planModel,
        LlmModelDefinition generateModel,
        IModelSelector? modelSelector = null)
    {
        _ = planModel; // reserved for future plan-model tracking
        return new AskApiResponse
        {
            Meta = new AskResponseMeta
            {
                ConversationId = request.ConversationId ?? string.Empty,
                BearerToken = request.BearerToken ?? string.Empty,
                TimestampUtc = DateTime.UtcNow.ToString("o")
            },
            Request = new AskResponseRequest
            {
                Environment = request.Environment ?? string.Empty,
                Question = request.Question ?? string.Empty,
                SelectedTargets = request.SelectedTargets ?? [],
                TargetType = EnvironmentRules.IsSqlServer(request.Environment ?? string.Empty) ? "SqlServer"
                           : EnvironmentRules.IsWindows(request.Environment ?? string.Empty) ? "Windows"
                           : null
            },
            Tuning = new AskResponseTuning
            {
                TunedQuestion = tunedQuestion ?? string.Empty,
                Status = "OK",
                Model = new AskModelRef
                {
                    Provider = tuneModel.Provider,
                    ModelKey = tuneModel.ModelKey
                }
            },
            Plan = new AskResponsePlan
            {
                Model = new AskModelRef
                {
                    Provider = generateModel.Provider,
                    ModelKey = generateModel.ModelKey
                }
            },
            Result = new AskResponseResult(),
            Models = BuildPipelineModels(modelSelector)
        };
    }

    private static AskPipelineModels? BuildPipelineModels(IModelSelector? selector)
    {
        if (selector is null) return null;
        try
        {
            var tune = selector.SelectTuneModel();
            var templateFind = selector.SelectTemplateFindModel();
            var generate = selector.SelectGenerateModel();
            var validate = selector.SelectValidateModel();
            var explain = selector.SelectExplainModel();

            return new AskPipelineModels
            {
                UseForTune = $"{tune.DisplayName} ({tune.Provider})",
                UseForTemplateFind = $"{templateFind.DisplayName} ({templateFind.Provider})",
                UseForGenerate = $"{generate.DisplayName} ({generate.Provider})",
                UseForRepair = $"{validate.DisplayName} ({validate.Provider})",
                UseForExplain = $"{explain.DisplayName} ({explain.Provider})"
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Builds the complete pipeline process flow from the populated response.
    /// Reads tuning, plan, script, retryAttempts, result, and answer nodes to reconstruct
    /// the full step-by-step pipeline trace.
    /// </summary>
    private static AskPipelineProcess BuildPipelineProcess(AskApiResponse response)
    {
        var process = new AskPipelineProcess();
        var stepNum = 0;
        var isSample = string.Equals(response.Plan.Mode, "SAMPLE_ONLY", StringComparison.OrdinalIgnoreCase);

        // ── 1a. SAMPLE_FETCH (sample execution path) ────────────────────────────
        if (isSample)
        {
            var sampleDetail = response.Plan.SampleId.HasValue
                ? $"Resolved sample #{response.Plan.SampleId}"
                : !string.IsNullOrWhiteSpace(response.Plan.GroupKey)
                    ? $"Resolved sample by group \"{response.Plan.GroupKey}\""
                    : "Resolved curated sample script.";

            if (!string.IsNullOrWhiteSpace(response.Plan.ScriptLanguage))
                sampleDetail += $" Language: {response.Plan.ScriptLanguage}.";

            process.Steps.Add(new AskProcessStep
            {
                Step = ++stepNum,
                Phase = "SAMPLE_FETCH",
                Status = "SUCCESS",
                Detail = sampleDetail
            });
        }

        // ── 1b. TUNING ─────────────────────────────────────────────────────────
        if (!isSample || !string.Equals(response.Tuning.Status, "SKIPPED", StringComparison.OrdinalIgnoreCase))
        {
            var tuningStatus = string.IsNullOrWhiteSpace(response.Tuning.TunedQuestion) ? "FAILED" : "SUCCESS";
            process.Steps.Add(new AskProcessStep
            {
                Step = ++stepNum,
                Phase = "TUNING",
                Status = tuningStatus,
                Detail = tuningStatus == "SUCCESS"
                    ? $"Tuned: \"{Truncate(response.Tuning.TunedQuestion, 100)}\""
                    : "Tuning failed or produced no output.",
                Model = response.Tuning.Model is not null
                    ? $"{response.Tuning.Model.ModelKey} ({response.Tuning.Model.Provider})"
                    : null
            });
        }

        // ── 2. TOPIC_CLASSIFY (only for LLM_ONLY mode with topic) ──────────────
        if (!string.IsNullOrWhiteSpace(response.Plan.Topic))
        {
            process.Steps.Add(new AskProcessStep
            {
                Step = ++stepNum,
                Phase = "TOPIC_CLASSIFY",
                Status = "SUCCESS",
                Detail = $"Topic: {response.Plan.Topic} (score: {response.Plan.TopicScore})"
            });
        }

        // ── 3. GENERATE ────────────────────────────────────────────────────────
        if (response.Script is not null)
        {
            var contractDetail = response.Plan.ContractPassed == true
                ? "Contract passed."
                : response.Plan.ContractViolation is not null
                    ? $"Contract violation: {Truncate(response.Plan.ContractViolation, 80)}"
                    : "";

            var genDetail = $"Generated {response.Plan.ScriptLanguage ?? "script"} via {response.Plan.GeneratorMode ?? "LLM"}.";
            if (!string.IsNullOrWhiteSpace(contractDetail))
                genDetail += " " + contractDetail;

            process.Steps.Add(new AskProcessStep
            {
                Step = ++stepNum,
                Phase = "GENERATE",
                Status = "SUCCESS",
                Detail = genDetail,
                Model = response.Plan.Model is not null
                    ? $"{response.Plan.Model.ModelKey} ({response.Plan.Model.Provider})"
                    : null
            });
        }

        // ── 4. SAFETY_CHECK ────────────────────────────────────────────────────
        if (response.Script is not null)
        {
            var safetyPassed = response.Script.Validation?.IsSafeReadOnly == true;
            process.Steps.Add(new AskProcessStep
            {
                Step = ++stepNum,
                Phase = "SAFETY_CHECK",
                Status = safetyPassed ? "PASSED" : "BLOCKED",
                Detail = safetyPassed ? "Script is read-only safe." : "Script blocked by safety scanner."
            });
        }

        // ── 5. VALIDATE + REPAIR (from retryAttempts) ──────────────────────────
        if (response.RetryAttempts is not null)
        {
            foreach (var attempt in response.RetryAttempts)
            {
                if (string.Equals(attempt.Phase, "VALIDATE", StringComparison.OrdinalIgnoreCase))
                {
                    var validStatus = attempt.Status;
                    var detail = validStatus switch
                    {
                        "SUCCESS" => $"Compile-check passed on {attempt.Target}.",
                        "FAILED" when attempt.ErrorType == "CONNECTION" =>
                            $"Connection failed on {attempt.Target}: {Truncate(attempt.ErrorMessage, 80)}",
                        "FAILED" when attempt.ErrorType == "SYNTAX" =>
                            $"Syntax error on {attempt.Target}: {Truncate(attempt.ErrorMessage, 80)}",
                        "BLOCKED" => $"Blocked: {Truncate(attempt.ErrorMessage, 80)}",
                        _ => $"{validStatus} on {attempt.Target}."
                    };

                    if (attempt.RepairedByLlm)
                        detail += " (after LLM repair)";

                    process.Steps.Add(new AskProcessStep
                    {
                        Step = ++stepNum,
                        Phase = "VALIDATE",
                        Status = validStatus,
                        Detail = detail,
                        Server = attempt.Target,
                        Error = validStatus != "SUCCESS" ? attempt.ErrorMessage : null
                    });
                }
            }
        }
        else if (response.Script is not null && response.Result.Kind == "EXECUTION")
        {
            // No retryAttempts means validation passed on first try — add implicit step
            var validationTarget = response.Result.Items?.FirstOrDefault()?.Target;
            process.Steps.Add(new AskProcessStep
            {
                Step = ++stepNum,
                Phase = "VALIDATE",
                Status = "SUCCESS",
                Detail = $"Compile-check passed on {validationTarget ?? "first target"}.",
                Server = validationTarget
            });
        }

        // ── 6. EXECUTE ─────────────────────────────────────────────────────────
        if (response.Result.Kind == "EXECUTION" && response.Result.Items is not null)
        {
            var successCount = response.Result.Items.Count(i => i.Status == "SUCCESS");
            var failCount = response.Result.Items.Count(i => i.Status != "SUCCESS");
            var totalRows = response.Result.Summary?.TotalRowCount ?? 0;

            var executeDetail = failCount == 0
                ? $"{successCount}/{response.Result.Items.Count} servers succeeded, {totalRows} total rows."
                : $"{successCount} succeeded, {failCount} failed out of {response.Result.Items.Count} servers.";

            process.Steps.Add(new AskProcessStep
            {
                Step = ++stepNum,
                Phase = "EXECUTE",
                Status = failCount == 0 ? "SUCCESS" : (successCount > 0 ? "PARTIAL" : "FAILED"),
                Detail = executeDetail,
                Servers = response.Result.Items.Select(i => new AskProcessServerResult
                {
                    Target = i.Target,
                    Status = i.Status,
                    RowCount = i.RowCount,
                    Error = i.Status != "SUCCESS"
                        ? i.Rows?.FirstOrDefault()?.TryGetValue("Error", out var err) == true ? err?.ToString() : null
                        : null
                }).ToList()
            });
        }

        // ── 7. EXPLAIN ─────────────────────────────────────────────────────────
        if (response.Answer is not null)
        {
            process.Steps.Add(new AskProcessStep
            {
                Step = ++stepNum,
                Phase = "EXPLAIN",
                Status = "SUCCESS",
                Detail = $"Generated explanation: \"{Truncate(response.Answer.Title ?? response.Answer.Explanation, 80)}\"",
                Model = response.Answer.Model is not null
                    ? $"{response.Answer.Model.ModelKey} ({response.Answer.Model.Provider})"
                    : null
            });
        }

        // ── 8. DIAGNOSTIC_ANALYSIS (Root Cause Engine) ──────────────────────────
        if (response.DiagnosticReport is not null)
        {
            var diag = response.DiagnosticReport;
            var diagDetail = $"Primary cause: {Truncate(diag.PrimaryCause, 60)}. Confidence: {diag.Confidence:P0}. Owner: {diag.Owner}.";
            process.Steps.Add(new AskProcessStep
            {
                Step = ++stepNum,
                Phase = "DIAGNOSTIC_ANALYSIS",
                Status = "SUCCESS",
                Detail = diagDetail
            });
        }

        // ── 9. DRIFT_ANALYSIS (Fleet Drift Engine) ─────────────────────────────
        if (response.DriftReport is not null)
        {
            var drift = response.DriftReport;
            var driftDetail = $"{drift.Summary.TotalServers} servers compared. {drift.Summary.DriftCount} drifted settings, {drift.AlignedSettings} aligned.";
            if (drift.Summary.CriticalCount > 0)
                driftDetail += $" {drift.Summary.CriticalCount} critical.";
            process.Steps.Add(new AskProcessStep
            {
                Step = ++stepNum,
                Phase = "DRIFT_ANALYSIS",
                Status = "SUCCESS",
                Detail = driftDetail
            });
        }

        // Stopped / blocked pipelines
        if (response.Result.Kind == "STOPPED" || response.Result.Status == "STOPPED")
        {
            process.Steps.Add(new AskProcessStep
            {
                Step = ++stepNum,
                Phase = "STOPPED",
                Status = "STOPPED",
                Detail = Truncate(response.Result.AnswerText ?? "Pipeline stopped.", 150)
            });
        }

        process.Outcome = process.Steps.LastOrDefault()?.Status ?? "UNKNOWN";
        return process;
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return value.Length <= maxLength ? value : value[..maxLength] + "…";
    }

    private sealed class ScriptPlan
    {
        [JsonPropertyName("readOnly")]
        public bool ReadOnly { get; set; }

        [JsonPropertyName("environment")]
        public string Environment { get; set; } = string.Empty;

        [JsonPropertyName("scriptLanguage")]
        public string ScriptLanguage { get; set; } = string.Empty;

        [JsonPropertyName("intent")]
        public string Intent { get; set; } = string.Empty;

        [JsonPropertyName("filters")]
        public List<PlanFilter>? Filters { get; set; } = [];

        [JsonPropertyName("timeWindow")]
        public PlanTimeWindow? TimeWindow { get; set; }

        [JsonPropertyName("needsClarification")]
        public bool NeedsClarification { get; set; }

        [JsonPropertyName("clarificationQuestion")]
        public string? ClarificationQuestion { get; set; }

        [JsonPropertyName("confidence")]
        public double Confidence { get; set; }
    }

    private sealed class PlanFilter
    {
        [JsonPropertyName("field")]
        public string? Field { get; set; }

        [JsonPropertyName("op")]
        public string? Op { get; set; }

        [JsonPropertyName("value")]
        public JsonElement Value { get; set; }

        [JsonPropertyName("unit")]
        public string? Unit { get; set; }
    }

    private sealed class PlanTimeWindow
    {
        [JsonPropertyName("value")]
        public int Value { get; set; }

        [JsonPropertyName("unit")]
        public string Unit { get; set; } = string.Empty;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  ROOT CAUSE DIAGNOSTIC ENGINE
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds a structured AskAnswerNode from a diagnostic report for the explain response.
    /// </summary>
    internal static AskAnswerNode BuildDiagnosticAnswerNode(
        AskDiagnosticReport report,
        string severity,
        string[] highlightsArray)
    {
        var title = report.Confidence >= 0.3
            ? $"Root Cause: {report.PrimaryCause}"
            : "Diagnostic Analysis Complete — No Critical Issues Found";

        var sections = new List<AnswerSection>();

        // ── Section 1: Fleet Overview ──────────────────────────────────
        var overviewBullets = new List<string>
        {
            report.PrimaryCause,
            $"Confidence: {report.Confidence:P0}",
            $"Escalate to: {report.Owner}"
        };

        if (report.ContributingFactors is { Count: > 0 })
            overviewBullets.Add($"Contributing factors: {string.Join(", ", report.ContributingFactors)}");

        var criticalServers = report.ServerDiagnostics.Where(sd => sd.Status == "critical").Select(sd => sd.Server).ToList();
        var warningServers = report.ServerDiagnostics.Where(sd => sd.Status == "warning").Select(sd => sd.Server).ToList();
        var healthyServers = report.ServerDiagnostics.Where(sd => sd.Status == "healthy").Select(sd => sd.Server).ToList();

        if (criticalServers.Count > 0)
            overviewBullets.Add($"Critical servers: {string.Join(", ", criticalServers)}");
        if (warningServers.Count > 0)
            overviewBullets.Add($"Warning servers: {string.Join(", ", warningServers)}");
        if (healthyServers.Count > 0)
            overviewBullets.Add($"Healthy servers: {string.Join(", ", healthyServers)}");

        // Top evidence across all servers
        var topEvidence = report.Evidence?.Take(5).ToList();
        if (topEvidence is { Count: > 0 })
        {
            overviewBullets.Add("Key evidence:");
            foreach (var ev in topEvidence)
                overviewBullets.Add($"  {ev}");
        }

        sections.Add(new AnswerSection
        {
            Key = "overview",
            Title = "Diagnostic Overview",
            Icon = "AlertTriangle",
            Tone = criticalServers.Count > 0 ? "critical" : warningServers.Count > 0 ? "warning" : "ok",
            Bullets = overviewBullets
        });

        // ── Section 2+: Per-server findings ────────────────────────────
        foreach (var sd in report.ServerDiagnostics.OrderByDescending(s => s.Status == "critical" ? 3 : s.Status == "warning" ? 2 : 1))
        {
            var serverBullets = new List<string>();

            // Lead with a clear summary of what's wrong on this server
            serverBullets.Add($"Primary cause: {sd.PrimaryCause}");
            serverBullets.Add($"Escalate to: {sd.Owner}");

            // Top cause scores for this server
            var topScores = sd.CauseScores.Where(cs => cs.Score > 0).Take(3).ToList();
            if (topScores.Count > 0)
            {
                serverBullets.Add($"Cause scores: {string.Join(" | ", topScores.Select(cs => $"{cs.Label}: {cs.Score}"))}");
            }

            // Non-config findings first (these are the actionable ones)
            var actionableFindings = sd.Findings.Where(f => f.Type != "config_concern").ToList();
            foreach (var f in actionableFindings.Take(10))
            {
                var findingLine = $"[{f.Severity.ToUpperInvariant()}] {f.Title}";
                if (!string.IsNullOrWhiteSpace(f.Detail))
                    findingLine += $" — {f.Detail}";
                if (f.Spid is > 0)
                    findingLine += $" (SPID {f.Spid})";
                if (f.LatencyMs is > 0)
                    findingLine += $" [{f.LatencyMs:F0}ms latency]";
                if (f.WaitTimeMs is > 0)
                    findingLine += $" [{f.WaitTimeMs:#,##0}ms cumulative wait]";
                if (f.CpuTimeMs is > 0)
                    findingLine += $" [{f.CpuTimeMs:#,##0}ms CPU]";
                if (f.BlockedCount is > 0)
                    findingLine += $" [{f.BlockedCount} sessions blocked]";
                if (!string.IsNullOrWhiteSpace(f.SqlText) && f.SqlText.Length > 5)
                    findingLine += $" | SQL: {(f.SqlText.Length > 200 ? f.SqlText[..200] + "…" : f.SqlText)}";
                serverBullets.Add(findingLine);
            }

            // Config concerns separately
            var configs = sd.Findings.Where(f => f.Type == "config_concern").ToList();
            if (configs.Count > 0)
            {
                foreach (var cfg in configs.Take(5))
                {
                    var cfgLine = $"[CONFIG] {cfg.Title}";
                    if (!string.IsNullOrWhiteSpace(cfg.Detail))
                        cfgLine += $" — {cfg.Detail}";
                    serverBullets.Add(cfgLine);
                }
            }

            // If no findings at all, say so explicitly
            if (sd.Findings.Count == 0)
                serverBullets.Add("No specific findings — server appears healthy based on collected metrics.");

            var serverTone = sd.Status == "critical" ? "critical" : sd.Status == "warning" ? "warning" : "ok";

            // Server-specific actions as Steps
            var serverSteps = sd.Actions?.Count > 0 ? sd.Actions : null;

            sections.Add(new AnswerSection
            {
                Key = $"server-{sd.Server.Replace("\\", "-", StringComparison.Ordinal).Replace(",", "-", StringComparison.Ordinal)}",
                Title = $"{sd.Server} — {sd.Status.ToUpperInvariant()}: {sd.PrimaryCause}",
                Icon = sd.Status == "critical" ? "AlertTriangle" : sd.Status == "warning" ? "Compass" : "ShieldCheck",
                Tone = serverTone,
                Bullets = serverBullets,
                Steps = serverSteps
            });
        }

        // ── Section: Recommended Actions ───────────────────────────────
        if (report.Actions is { Count: > 0 })
        {
            sections.Add(new AnswerSection
            {
                Key = "actions",
                Title = "Recommended Actions",
                Icon = "Lightbulb",
                Tone = "info",
                Steps = report.Actions
            });
        }

        // ── Section: Healthy Areas ─────────────────────────────────────
        if (report.NotPrimaryCause is { Count: > 0 })
        {
            sections.Add(new AnswerSection
            {
                Key = "healthy-areas",
                Title = "Areas Checked — No Issues Found",
                Icon = "ShieldCheck",
                Tone = "ok",
                Bullets = report.NotPrimaryCause
            });
        }

        // Build summary lines
        var summaryLines = new List<string> { report.PrimaryCause };
        if (criticalServers.Count > 0)
            summaryLines.Add($"Critical servers: {string.Join(", ", criticalServers)}. Escalate to {report.Owner}.");
        if (report.ContributingFactors is { Count: > 0 })
            summaryLines.Add($"Also contributing: {string.Join(", ", report.ContributingFactors)}.");

        // ── Root cause and impact (for UI diagnostic dashboard) ──────────
        var rootCause = BuildDiagnosticRootCause(report);
        var impact = BuildDiagnosticImpact(report);

        // ── Key metrics: extract top KPIs per server ─────────────────────
        var keyMetrics = BuildDiagnosticKeyMetrics(report);

        // ── Comparison: per-server side-by-side ──────────────────────────
        var comparison = report.ServerDiagnostics.Count > 1
            ? report.ServerDiagnostics.Select(sd => new AskComparisonEntry
            {
                Target = sd.Server,
                Status = sd.Status,
                Metrics = sd.CauseScores.Where(cs => cs.Score > 0)
                    .ToDictionary(cs => cs.Label ?? cs.Bucket ?? "unknown", cs => (object?)cs.Score),
                Note = sd.PrimaryCause
            }).ToList()
            : null;

        // ── Prioritized recommendations from per-server findings ─────────
        var recommendations = BuildDiagnosticRecommendations(report);

        return new AskAnswerNode
        {
            Status = "OK",
            Severity = severity,
            Highlights = highlightsArray.Length > 0 ? highlightsArray : null,
            Title = title,
            Summary = summaryLines.ToArray(),
            Sections = sections,
            RootCause = rootCause,
            Impact = impact,
            KeyMetrics = keyMetrics.Count > 0 ? keyMetrics : null,
            Recommendations = recommendations.Count > 0 ? recommendations : null,
            Comparison = comparison,
        };
    }

    /// <summary>Build a specific root cause string with server names and metric values.</summary>
    private static string BuildDiagnosticRootCause(AskDiagnosticReport report)
    {
        var parts = new List<string>();
        foreach (var sd in report.ServerDiagnostics.Where(s => s.Status is "critical" or "warning"))
        {
            var topFindings = sd.Findings
                .Where(f => f.Severity is "critical" or "high")
                .Take(2)
                .Select(f => f.Title + (f.Detail?.Length > 0 ? $" ({f.Detail.Split(';')[0].Trim()})" : ""))
                .ToList();

            if (topFindings.Count > 0)
                parts.Add($"{sd.Server}: {string.Join("; ", topFindings)}");
        }

        return parts.Count > 0
            ? string.Join(" | ", parts)
            : report.PrimaryCause;
    }

    /// <summary>Build impact statement from blocking chains, pending grants, connection floods.</summary>
    private static string BuildDiagnosticImpact(AskDiagnosticReport report)
    {
        var impacts = new List<string>();
        foreach (var sd in report.ServerDiagnostics)
        {
            foreach (var f in sd.Findings)
            {
                if (f.BlockedCount is > 0)
                    impacts.Add($"{sd.Server}: {f.BlockedCount} sessions blocked by SPID {f.Spid}");
                if (f.Type == "pending_grant")
                    impacts.Add($"{sd.Server}: queries waiting for memory grants");
                if (f.Type == "connection_flood")
                    impacts.Add($"{sd.Server}: connection flood detected ({f.Detail})");
            }
        }

        return impacts.Count > 0
            ? string.Join(". ", impacts) + "."
            : null!;
    }

    /// <summary>Extract top KPI cards from per-server diagnostic data.</summary>
    private static List<AskKeyMetric> BuildDiagnosticKeyMetrics(AskDiagnosticReport report)
    {
        var metrics = new List<AskKeyMetric>();

        foreach (var sd in report.ServerDiagnostics)
        {
            // Find CPU metric
            var cpuFinding = sd.Findings.FirstOrDefault(f =>
                f.Title.Contains("CPU", StringComparison.OrdinalIgnoreCase) && f.Type != "config_concern");
            if (cpuFinding != null)
            {
                var cpuVal = cpuFinding.CpuTimeMs?.ToString() ?? cpuFinding.Detail?.Split(';').FirstOrDefault()?.Trim() ?? "?";
                metrics.Add(new AskKeyMetric
                {
                    Label = $"CPU ({sd.Server})",
                    Value = cpuVal,
                    Unit = cpuFinding.CpuTimeMs.HasValue ? "ms" : "percent",
                    Status = cpuFinding.Severity is "critical" ? "critical" : cpuFinding.Severity is "high" ? "warning" : "ok"
                });
            }

            // Find blocking
            var blockFinding = sd.Findings.FirstOrDefault(f => f.BlockedCount is > 0);
            if (blockFinding != null)
            {
                metrics.Add(new AskKeyMetric
                {
                    Label = $"Blocking ({sd.Server})",
                    Value = blockFinding.BlockedCount?.ToString() ?? "?",
                    Unit = "sessions",
                    Status = blockFinding.BlockedCount >= 5 ? "critical" : "warning"
                });
            }

            // Find memory/PLE
            var pleFinding = sd.Findings.FirstOrDefault(f =>
                f.Title.Contains("PLE", StringComparison.OrdinalIgnoreCase) ||
                f.Title.Contains("PageLife", StringComparison.OrdinalIgnoreCase));
            if (pleFinding != null)
            {
                metrics.Add(new AskKeyMetric
                {
                    Label = $"PLE ({sd.Server})",
                    Value = pleFinding.Detail?.Split(';').FirstOrDefault()?.Trim() ?? "?",
                    Unit = "seconds",
                    Status = pleFinding.Severity is "critical" ? "critical" : "warning"
                });
            }

            // Find IO
            var ioFinding = sd.Findings.FirstOrDefault(f =>
                f.LatencyMs is > 0 && f.Type != "config_concern");
            if (ioFinding != null)
            {
                metrics.Add(new AskKeyMetric
                {
                    Label = $"Disk I/O ({sd.Server})",
                    Value = $"{ioFinding.LatencyMs:F1}",
                    Unit = "ms",
                    Status = ioFinding.LatencyMs >= 50 ? "critical" : ioFinding.LatencyMs >= 20 ? "warning" : "ok"
                });
            }
        }

        // Deduplicate and cap at 8
        return metrics.Take(8).ToList();
    }

    /// <summary>Build prioritized recommendations from per-server findings and fleet actions.</summary>
    private static List<AskRecommendation> BuildDiagnosticRecommendations(AskDiagnosticReport report)
    {
        var recs = new List<AskRecommendation>();

        // Per-server critical/high findings → high-priority recommendations
        foreach (var sd in report.ServerDiagnostics)
        {
            foreach (var f in sd.Findings.Where(f => f.Severity is "critical").Take(2))
            {
                recs.Add(new AskRecommendation
                {
                    Text = $"[{sd.Server}] {f.Title}" + (!string.IsNullOrWhiteSpace(f.Detail) ? $" — {f.Detail.Split(';')[0].Trim()}" : ""),
                    Priority = "critical"
                });
            }
            foreach (var f in sd.Findings.Where(f => f.Severity is "high" && f.Type != "config_concern").Take(2))
            {
                recs.Add(new AskRecommendation
                {
                    Text = $"[{sd.Server}] {f.Title}" + (!string.IsNullOrWhiteSpace(f.Detail) ? $" — {f.Detail.Split(';')[0].Trim()}" : ""),
                    Priority = "high"
                });
            }
        }

        // Fleet-wide actions
        if (report.Actions is { Count: > 0 })
        {
            foreach (var action in report.Actions.Take(3))
            {
                if (!recs.Any(r => r.Text.Contains(action.Split(' ').FirstOrDefault() ?? "", StringComparison.OrdinalIgnoreCase)))
                    recs.Add(new AskRecommendation { Text = action, Priority = "medium" });
            }
        }

        // Config concerns as low priority
        foreach (var sd in report.ServerDiagnostics)
        {
            foreach (var f in sd.Findings.Where(f => f.Type == "config_concern").Take(2))
            {
                recs.Add(new AskRecommendation
                {
                    Text = $"[{sd.Server}] {f.Title}" + (!string.IsNullOrWhiteSpace(f.Detail) ? $" — {f.Detail}" : ""),
                    Priority = "low"
                });
            }
        }

        return recs.Take(10).ToList();
    }

    /// <summary>
    /// Detects whether the result data is diagnostic format (Category + MetricName + SeverityHint).
    /// </summary>
    internal static bool IsDiagnosticData(AskResponseResult result)
    {
        if (result.Items is null || result.Items.Count < 1)
            return false;

        foreach (var item in result.Items)
        {
            if (item.Rows is { Count: > 0 }
                && item.Rows[0].ContainsKey("MetricName")
                && item.Rows[0].ContainsKey("SeverityHint"))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Builds a root cause diagnostic report from evidence rows.
    /// Scores cause buckets PER SERVER, extracts findings (blocking, heavy queries, etc.),
    /// then aggregates fleet-wide.
    /// </summary>
    internal static AskDiagnosticReport BuildDiagnosticReport(AskResponseResult result)
    {
        var serverDiagnostics = new List<AskServerDiagnostic>();

        foreach (var item in result.Items!)
        {
            if (item.Rows is null || item.Rows.Count == 0) continue;
            var server = item.Target ?? "Unknown";

            var diag = BuildServerDiagnostic(server, item.Rows);
            serverDiagnostics.Add(diag);
        }

        // ── Fleet-wide aggregation ──────────────────────────────────────────
        var fleetScores = new Dictionary<string, (int Score, List<string> Signals)>();
        foreach (var sd in serverDiagnostics)
        {
            foreach (var cs in sd.CauseScores)
            {
                if (!fleetScores.TryGetValue(cs.Bucket, out var current))
                    current = (0, []);
                fleetScores[cs.Bucket] = (current.Score + cs.Score, current.Signals);
                foreach (var sig in cs.Signals ?? [])
                {
                    if (current.Signals.Count < 10)
                        current.Signals.Add(sig);
                }
            }
        }

        var ranked = fleetScores
            .Where(kv => kv.Value.Score > 0)
            .OrderByDescending(kv => kv.Value.Score)
            .ToList();

        var topBucket = ranked.FirstOrDefault();

        // Check if any server has real actionable issues
        var serversWithActionableIssues = serverDiagnostics.Count(sd => sd.Status is "critical" or "warning");
        var hasFleetIssues = serversWithActionableIssues > 0;

        var primaryCause = hasFleetIssues
            ? BucketToCauseStatement(topBucket.Key)
            : ranked.Count > 0
                ? $"Minor signals detected ({BucketToHumanLabel(topBucket.Key)}) but no servers are actively struggling. All servers appear healthy."
                : "All servers appear healthy — no issues detected.";
        var owner = hasFleetIssues ? BucketToOwner(topBucket.Key) : "Monitoring";

        // Confidence: aggregate top score normalized by server count
        var maxPossible = serverDiagnostics.Count * 100;
        var rawConfidence = topBucket.Value.Score > 0 && hasFleetIssues
            ? Math.Min(topBucket.Value.Score * 1.0 / Math.Max(maxPossible, 1), 0.99)
            : topBucket.Value.Score > 0
                ? Math.Min(topBucket.Value.Score * 0.3 / Math.Max(maxPossible, 1), 0.25) // Low confidence when no real issues
                : 0.0;
        // Boost if majority of servers agree on a real issue
        var serversWithIssue = serverDiagnostics.Count(sd => sd.Status is not "healthy");
        if (serversWithIssue == serverDiagnostics.Count && serverDiagnostics.Count > 1 && hasFleetIssues)
            rawConfidence = Math.Min(rawConfidence * 1.3, 0.99);

        var causeScores = ranked.Select(kv => new AskCauseScore
        {
            Bucket = kv.Key,
            Label = BucketToHumanLabel(kv.Key),
            Score = Math.Min(kv.Value.Score, 100),
            Signals = kv.Value.Signals.Take(5).ToList()
        }).ToList();

        // Evidence: top findings across all servers
        var evidence = serverDiagnostics
            .SelectMany(sd => sd.Findings
                .Where(f => f.Severity is "critical" or "high")
                .Select(f => $"[{sd.Server}] {f.Title}: {f.Detail}"))
            .Take(15)
            .ToList();

        // Contributing factors
        var contributing = ranked.Skip(1).Take(2)
            .Where(kv => kv.Value.Score >= 20)
            .Select(kv => BucketToHumanLabel(kv.Key) + $" (score: {Math.Min(kv.Value.Score, 100)})")
            .ToList();

        // Healthy areas
        var checkedBuckets = new HashSet<string>(["SQL_BLOCKING", "SQL_CPU", "SQL_IO", "SQL_MEMORY",
            "SQL_AGENT_JOB", "SQL_INDEX_MAINTENANCE", "WINDOWS_CPU", "WINDOWS_MEMORY",
            "WINDOWS_DISK", "IIS_APPPOOL", "APP_PROCESS", "BACKUP_OR_SCANNER"]);
        var healthy = checkedBuckets
            .Where(b => !fleetScores.ContainsKey(b) || fleetScores[b].Score == 0)
            .Select(b => BucketToHumanLabel(b) + " — no issues detected")
            .Take(6)
            .ToList();

        // Fleet-wide actions
        var actions = BuildDiagnosticActions(topBucket.Key, ranked, serverDiagnostics);

        return new AskDiagnosticReport
        {
            PrimaryCause = primaryCause,
            Confidence = Math.Round(rawConfidence, 2),
            Owner = owner,
            CauseScores = causeScores,
            Evidence = evidence.Count > 0 ? evidence : ["No critical or high-severity signals detected — servers appear healthy."],
            ContributingFactors = contributing.Count > 0 ? contributing : null,
            NotPrimaryCause = healthy.Count > 0 ? healthy : null,
            Actions = actions,
            ServerDiagnostics = serverDiagnostics
        };
    }

    // ── System SPID filter: these background commands are always running and not a problem ──
    private static readonly HashSet<string> SystemBackgroundCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "RESOURCE MONITOR", "LAZY WRITER", "LOG WRITER", "XE TIMER", "XE DISPATCHER",
        "LOCK MONITOR", "RECOVERY WRITER", "BRKR EVENT HNDLR", "BRKR TASK",
        "CHECKPOINT", "SYSTEM_HEALTH_MONITOR", "TRACE QUEUE TASK", "TASK MANAGER",
        "LOG POOL MEMORY NOTIFICATION", "XIO_LEASE_RENEWAL_WORKER", "XIO_RETRY_WORKER",
        "UNKNOWN TOKEN"
    };

    private static bool IsSystemBackgroundSpid(string detail)
    {
        // Parse command from detail: "db=...; status=background; command=LAZY WRITER; ..."
        var cmdStart = detail.IndexOf("command=", StringComparison.OrdinalIgnoreCase);
        if (cmdStart < 0) return false;
        cmdStart += 8;

        var cmdEnd = detail.IndexOf(';', cmdStart);
        var command = cmdEnd > cmdStart ? detail[cmdStart..cmdEnd].Trim() : detail[cmdStart..].Trim();

        // Always filter known system commands regardless of status
        if (SystemBackgroundCommands.Contains(command))
            return true;

        // Sleeping SPIDs with no SQL text and no CPU are system idle sessions
        if (detail.Contains("status=sleeping", StringComparison.OrdinalIgnoreCase)
            && detail.EndsWith("sql=", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    /// Builds per-server diagnostic: scores cause buckets, extracts findings.
    /// </summary>
    private static AskServerDiagnostic BuildServerDiagnostic(string server, List<Dictionary<string, object?>> rows)
    {
        var scores = CreateEmptyBuckets();
        var findings = new List<AskDiagnosticFinding>();

        foreach (var row in rows)
        {
            var category = row.TryGetValue("Category", out var c) ? c?.ToString() ?? "" : "";
            var metric = row.TryGetValue("MetricName", out var m) ? m?.ToString() ?? "" : "";
            var value = row.TryGetValue("MetricValue", out var v) ? v?.ToString() ?? "" : "";
            var severity = row.TryGetValue("SeverityHint", out var s) ? s?.ToString() ?? "INFO" : "INFO";
            var detail = row.TryGetValue("Detail", out var d) ? d?.ToString() ?? "" : "";

            if (string.IsNullOrWhiteSpace(metric)) continue;

            var cat = category.ToUpperInvariant();
            var met = metric.ToUpperInvariant();
            var sev = severity.ToUpperInvariant();
            double.TryParse(value, out var numVal);
            var signal = $"{metric}={value}";

            // ── Active Requests: filter out system background SPIDs ──────
            if (cat == "ACTIVEREQUEST")
            {
                if (IsSystemBackgroundSpid(detail))
                    continue; // Skip system processes entirely

                // Parse detail fields
                ParseActiveRequestDetail(detail, out var reqDb, out var reqStatus, out var reqCommand,
                    out var blockerSpid, out var reqSqlText);
                int.TryParse(metric.Replace("SPID ", "", StringComparison.OrdinalIgnoreCase), out var spid);

                // Only score user queries with meaningful CPU
                if (sev is "CRITICAL" or "HIGH" && reqStatus != "background")
                {
                    AddScore(scores, "SQL_CPU", sev == "CRITICAL" ? 30 : 15, signal);

                    findings.Add(new AskDiagnosticFinding
                    {
                        Type = "heavy_query",
                        Severity = sev == "CRITICAL" ? "critical" : "high",
                        Title = $"SPID {spid} consuming {numVal:#,##0}ms CPU",
                        Detail = $"Database: {reqDb}. Command: {reqCommand}. Status: {reqStatus}.",
                        Spid = spid > 0 ? spid : null,
                        Database = reqDb,
                        SqlText = !string.IsNullOrWhiteSpace(reqSqlText) ? reqSqlText : null,
                        CpuTimeMs = (long)numVal
                    });
                }

                // Blocked by another SPID
                if (blockerSpid > 0)
                {
                    AddScore(scores, "SQL_BLOCKING", 15, $"SPID {spid} blocked by {blockerSpid}");
                    findings.Add(new AskDiagnosticFinding
                    {
                        Type = "blocking",
                        Severity = "high",
                        Title = $"SPID {spid} is blocked by SPID {blockerSpid}",
                        Detail = $"Database: {reqDb}. Waiting query: {(string.IsNullOrWhiteSpace(reqSqlText) ? reqCommand : reqSqlText)}",
                        Spid = spid > 0 ? spid : null,
                        Database = reqDb,
                        SqlText = !string.IsNullOrWhiteSpace(reqSqlText) ? reqSqlText : null
                    });
                }

                // Index rebuild/maintenance running
                if (reqCommand.Contains("INDEX", StringComparison.OrdinalIgnoreCase)
                    || reqCommand.Contains("DBCC", StringComparison.OrdinalIgnoreCase)
                    || (reqSqlText?.Contains("INDEX", StringComparison.OrdinalIgnoreCase) == true
                        && reqSqlText.Contains("REBUILD", StringComparison.OrdinalIgnoreCase)))
                {
                    AddScore(scores, "SQL_INDEX_MAINTENANCE", 25, $"SPID {spid}: {reqCommand}");
                }

                continue;
            }

            // ── Blocking tree ────────────────────────────────────────────
            if (cat == "BLOCKING")
            {
                if (met.Contains("LEADBLOCKER"))
                {
                    int.TryParse(metric.Replace("LeadBlocker SPID ", "", StringComparison.OrdinalIgnoreCase), out var blockerSpid);
                    var blockedCount = (int)numVal;
                    var points = blockedCount >= 10 ? 40 : blockedCount >= 3 ? 30 : 15;
                    AddScore(scores, "SQL_BLOCKING", points, $"SPID {blockerSpid} blocking {blockedCount} sessions");

                    findings.Add(new AskDiagnosticFinding
                    {
                        Type = "blocking",
                        Severity = blockedCount >= 10 ? "critical" : blockedCount >= 3 ? "high" : "medium",
                        Title = $"SPID {blockerSpid} is the lead blocker — {blockedCount} sessions waiting",
                        Detail = $"This SPID is holding locks that are blocking {blockedCount} other sessions. Check what this SPID is executing.",
                        Spid = blockerSpid > 0 ? blockerSpid : null,
                        BlockedCount = blockedCount
                    });
                }
                else if (met == "TOTALBLOCKEDREQUESTS")
                {
                    if (numVal >= 10) AddScore(scores, "SQL_BLOCKING", 35, signal);
                    else if (numVal >= 3) AddScore(scores, "SQL_BLOCKING", 20, signal);
                    else if (numVal >= 1) AddScore(scores, "SQL_BLOCKING", 10, signal);
                }
                continue;
            }

            // ── Waits (cumulative since startup — normalize per task) ────
            if (cat == "WAITS")
            {
                // Parse waiting_tasks_count from detail: "Waiting tasks=741"
                double waitingTasks = 0;
                var wtIdx = detail.IndexOf("Waiting tasks=", StringComparison.OrdinalIgnoreCase);
                if (wtIdx >= 0)
                {
                    var wtStart = wtIdx + 14;
                    var wtEnd = detail.IndexOfAny([';', ' '], wtStart);
                    if (wtEnd < 0) wtEnd = detail.Length;
                    double.TryParse(detail[wtStart..wtEnd], out waitingTasks);
                }
                var avgWaitMs = waitingTasks > 0 ? numVal / waitingTasks : numVal;

                if (met.StartsWith("LCK_", StringComparison.Ordinal))
                {
                    // Lock waits: score based on avg wait per task AND total cumulative
                    var lockScore = avgWaitMs >= 100 ? 25 : avgWaitMs >= 20 ? 15 : numVal >= 50000 ? 10 : 5;
                    var lockSeverity = avgWaitMs >= 100 ? "high" : avgWaitMs >= 20 ? "medium" : "info";
                    AddScore(scores, "SQL_BLOCKING", lockScore, signal);
                    findings.Add(new AskDiagnosticFinding
                    {
                        Type = "lock_wait",
                        Severity = lockSeverity,
                        Title = $"Lock wait: {metric}",
                        Detail = $"Total: {numVal:#,##0}ms across {waitingTasks:#,##0} tasks (avg {avgWaitMs:F1}ms/task). {detail}",
                        WaitTimeMs = (long)numVal
                    });
                }
                if (met.StartsWith("PAGEIOLATCH_", StringComparison.Ordinal))
                {
                    // PAGEIOLATCH: avg per task matters more than cumulative
                    var ioScore = avgWaitMs >= 30 ? 25 : avgWaitMs >= 10 ? 15 : 3;
                    var ioSeverity = avgWaitMs >= 30 ? "high" : avgWaitMs >= 10 ? "medium" : "info";
                    AddScore(scores, "SQL_IO", ioScore, signal);
                    if (avgWaitMs >= 10) // Only report if actually slow per-task
                    {
                        findings.Add(new AskDiagnosticFinding
                        {
                            Type = "wait_pressure",
                            Severity = ioSeverity,
                            Title = $"I/O wait: {metric}",
                            Detail = $"Total: {numVal:#,##0}ms across {waitingTasks:#,##0} tasks (avg {avgWaitMs:F1}ms/task). {detail}",
                            WaitTimeMs = (long)numVal
                        });
                    }
                }
                if (met is "SOS_SCHEDULER_YIELD" or "CXPACKET" or "CXCONSUMER")
                {
                    var cpuScore = avgWaitMs >= 5 ? 15 : 3;
                    AddScore(scores, "SQL_CPU", cpuScore, signal);
                }
                if (met == "WRITELOG")
                {
                    var wlScore = avgWaitMs >= 10 ? 10 : 2;
                    AddScore(scores, "SQL_IO", wlScore, signal);
                }
                if (met == "THREADPOOL")
                {
                    AddScore(scores, "SQL_CPU", 20, signal);
                    findings.Add(new AskDiagnosticFinding
                    {
                        Type = "wait_pressure",
                        Severity = "high",
                        Title = "Thread pool exhaustion (THREADPOOL wait)",
                        Detail = $"Total: {numVal:#,##0}ms across {waitingTasks:#,##0} tasks (avg {avgWaitMs:F1}ms/task). Worker thread starvation detected.",
                        WaitTimeMs = (long)numVal
                    });
                }
                continue;
            }

            // ── Index Maintenance operations ────────────────────────────
            if (cat == "INDEXMAINTENANCE")
            {
                int.TryParse(metric.Replace("SPID ", "", StringComparison.OrdinalIgnoreCase), out var spid);
                AddScore(scores, "SQL_INDEX_MAINTENANCE", 35, signal);
                AddScore(scores, "SQL_BLOCKING", 20, "Index ops often cause blocking");

                // Parse command from detail
                var idxCommand = "";
                var idxDb = "";
                if (detail.Contains("command="))
                {
                    var cmdStart = detail.IndexOf("command=", StringComparison.OrdinalIgnoreCase) + 8;
                    var cmdEnd = detail.IndexOf(';', cmdStart);
                    idxCommand = cmdEnd > cmdStart ? detail[cmdStart..cmdEnd].Trim() : detail[cmdStart..].Trim();
                }
                if (detail.Contains("db="))
                {
                    var dbStart = detail.IndexOf("db=", StringComparison.OrdinalIgnoreCase) + 3;
                    var dbEnd = detail.IndexOf(';', dbStart);
                    idxDb = dbEnd > dbStart ? detail[dbStart..dbEnd].Trim() : detail[dbStart..].Trim();
                }

                findings.Add(new AskDiagnosticFinding
                {
                    Type = "maintenance_job",
                    Severity = "high",
                    Title = $"Index maintenance in progress (SPID {spid}, {numVal:F0}% complete)",
                    Detail = $"Database: {idxDb}. Operation: {idxCommand}. This may cause blocking and elevated I/O.",
                    Spid = spid > 0 ? spid : null,
                    Database = !string.IsNullOrWhiteSpace(idxDb) ? idxDb : null
                });
                continue;
            }

            // ── SQL Agent Jobs ──────────────────────────────────────────
            if (cat == "SQLAGENT")
            {
                var jobName = metric.Replace("RunningJob:", "", StringComparison.OrdinalIgnoreCase);
                var isMaintenanceJob = jobName.Contains("index", StringComparison.OrdinalIgnoreCase)
                    || jobName.Contains("rebuild", StringComparison.OrdinalIgnoreCase)
                    || jobName.Contains("maintenance", StringComparison.OrdinalIgnoreCase);

                AddScore(scores, "SQL_AGENT_JOB", sev == "HIGH" ? 30 : 10, signal);
                if (isMaintenanceJob)
                    AddScore(scores, "SQL_INDEX_MAINTENANCE", 25, signal);

                findings.Add(new AskDiagnosticFinding
                {
                    Type = "maintenance_job",
                    Severity = sev == "HIGH" ? "high" : "info",
                    Title = $"SQL Agent job running: {jobName}",
                    Detail = isMaintenanceJob
                        ? $"This is a maintenance/rebuild job — may cause blocking and I/O pressure. Started: {value}"
                        : $"Job started: {value}"
                });
                continue;
            }

            // ── Memory Grants ──────────────────────────────────────────
            if (cat == "MEMORYGRANT")
            {
                int.TryParse(metric.Replace("SPID ", "", StringComparison.OrdinalIgnoreCase), out var spid);
                if (sev is "HIGH" or "CRITICAL")
                {
                    AddScore(scores, "SQL_MEMORY", 20, signal);
                    findings.Add(new AskDiagnosticFinding
                    {
                        Type = "memory_pressure",
                        Severity = "high",
                        Title = $"SPID {spid} has large memory grant ({numVal:#,##0} KB)",
                        Detail = detail,
                        Spid = spid > 0 ? spid : null
                    });
                }
                continue;
            }

            if (cat == "MEMORY" && met == "PENDINGMEMORYGRANTS")
            {
                if (numVal >= 5) AddScore(scores, "SQL_MEMORY", 35, signal);
                else if (numVal >= 1) AddScore(scores, "SQL_MEMORY", 20, signal);

                if (numVal >= 1)
                {
                    findings.Add(new AskDiagnosticFinding
                    {
                        Type = "memory_pressure",
                        Severity = numVal >= 5 ? "critical" : "high",
                        Title = $"{(int)numVal} queries waiting for memory grants",
                        Detail = "Queries are queued waiting for memory — indicates memory pressure. Check max server memory setting."
                    });
                }
                continue;
            }

            // ── File I/O ────────────────────────────────────────────────
            if (cat == "FILEIO")
            {
                // Parse avg_write_ms from detail
                double writeMs = 0;
                if (detail.Contains("avg_write_ms="))
                {
                    var ws = detail.IndexOf("avg_write_ms=", StringComparison.OrdinalIgnoreCase) + 13;
                    var we = detail.IndexOfAny([';', ' '], ws);
                    if (we < 0) we = detail.Length;
                    double.TryParse(detail[ws..we], out writeMs);
                }

                var maxLatency = Math.Max(numVal, writeMs);
                var colonIdx = metric.IndexOf(':');
                var dbName = colonIdx > 0 ? metric[..colonIdx] : metric;

                // Skip system databases with moderate latency — only flag user DBs or severe latency
                var isSystemDb = dbName.Equals("model", StringComparison.OrdinalIgnoreCase)
                    || dbName.Equals("tempdb", StringComparison.OrdinalIgnoreCase)
                    || dbName.Equals("msdb", StringComparison.OrdinalIgnoreCase)
                    || dbName.Equals("master", StringComparison.OrdinalIgnoreCase);

                // Tiered scoring based on actual latency severity
                if (maxLatency >= 100)
                {
                    // Severe: >100ms — definitely a problem
                    AddScore(scores, "SQL_IO", 30, signal);
                    findings.Add(new AskDiagnosticFinding
                    {
                        Type = "io_latency",
                        Severity = "critical",
                        Title = $"Severe I/O latency on {dbName} ({maxLatency:F0}ms)",
                        Detail = $"Read: {numVal:F1}ms, Write: {writeMs:F1}ms. File: {(colonIdx > 0 ? metric[(colonIdx + 1)..] : metric)}",
                        Database = dbName,
                        LatencyMs = maxLatency
                    });
                }
                else if (maxLatency >= 50)
                {
                    // High: 50-100ms — concerning
                    AddScore(scores, "SQL_IO", 20, signal);
                    findings.Add(new AskDiagnosticFinding
                    {
                        Type = "io_latency",
                        Severity = "high",
                        Title = $"High I/O latency on {dbName} ({maxLatency:F0}ms)",
                        Detail = $"Read: {numVal:F1}ms, Write: {writeMs:F1}ms. File: {(colonIdx > 0 ? metric[(colonIdx + 1)..] : metric)}",
                        Database = dbName,
                        LatencyMs = maxLatency
                    });
                }
                else if (maxLatency >= 20 && !isSystemDb)
                {
                    // Moderate: 20-50ms on user databases — worth noting but low score
                    AddScore(scores, "SQL_IO", 5, signal);
                    findings.Add(new AskDiagnosticFinding
                    {
                        Type = "io_latency",
                        Severity = "medium",
                        Title = $"Moderate I/O latency on {dbName} ({maxLatency:F0}ms)",
                        Detail = $"Read: {numVal:F1}ms, Write: {writeMs:F1}ms. File: {(colonIdx > 0 ? metric[(colonIdx + 1)..] : metric)}",
                        Database = dbName,
                        LatencyMs = maxLatency
                    });
                }
                // Below 20ms or system DB below 50ms — normal, skip
                continue;
            }

            // ── Config concerns ─────────────────────────────────────────
            if (cat == "CONFIG" && sev == "HIGH")
            {
                findings.Add(new AskDiagnosticFinding
                {
                    Type = "config_concern",
                    Severity = "info",
                    Title = $"Config: {metric} = {value}",
                    Detail = detail
                });
                continue;
            }

            // ── Windows CPU ─────────────────────────────────────────────
            if (cat == "CPU" && met == "HOSTCPUPCT")
            {
                if (numVal >= 90) AddScore(scores, "WINDOWS_CPU", 40, signal);
                else if (numVal >= 75) AddScore(scores, "WINDOWS_CPU", 25, signal);
                continue;
            }

            // ── Windows Memory ──────────────────────────────────────────
            if (cat == "MEMORY" && met == "MEMORYUSEDPCT")
            {
                if (numVal >= 90) AddScore(scores, "WINDOWS_MEMORY", 35, signal);
                else if (numVal >= 80) AddScore(scores, "WINDOWS_MEMORY", 20, signal);
                continue;
            }

            // ── Windows Disk ────────────────────────────────────────────
            if (cat == "DISK")
            {
                if (sev is "CRITICAL" or "HIGH")
                    AddScore(scores, "WINDOWS_DISK", 30, signal);
                continue;
            }

            // ── Key Process: sqlservr, w3wp, powershell, pwsh ───────────
            if (cat == "KEYPROCESS")
            {
                if (met.Contains("SQLSERVR") && met.Contains("CPUPCT"))
                {
                    if (numVal >= 70) AddScore(scores, "SQL_CPU", 40, signal);
                    else if (numVal >= 25) AddScore(scores, "SQL_CPU", 20, signal);
                }
                if (met.Contains("W3WP") && met.Contains("CPUPCT"))
                {
                    if (numVal >= 70) AddScore(scores, "APP_PROCESS", 40, signal);
                    else if (numVal >= 25) AddScore(scores, "APP_PROCESS", 20, signal);
                }
                if ((met.Contains("POWERSHELL") || met.Contains("PWSH")) && met.Contains("CPUPCT"))
                {
                    if (numVal >= 50)
                    {
                        AddScore(scores, "APP_PROCESS", 30, signal);
                        findings.Add(new AskDiagnosticFinding
                        {
                            Type = "heavy_query",
                            Severity = numVal >= 70 ? "critical" : "high",
                            Title = $"PowerShell process using {numVal}% CPU",
                            Detail = $"Process: {metric}. {detail}. Check scheduled tasks or scripts running continuously.",
                            CpuTimeMs = (long)numVal
                        });
                    }
                }
                continue;
            }

            // ── Top Process CPU ─────────────────────────────────────────
            if (cat == "TOPPROCESSCPU" && sev is "CRITICAL" or "HIGH")
            {
                if (met.Contains("SQLSERVR", StringComparison.OrdinalIgnoreCase))
                    AddScore(scores, "SQL_CPU", 30, signal);
                else if (met.Contains("W3WP", StringComparison.OrdinalIgnoreCase))
                    AddScore(scores, "APP_PROCESS", 30, signal);
                else if (met.Contains("POWERSHELL", StringComparison.OrdinalIgnoreCase)
                    || met.Contains("PWSH", StringComparison.OrdinalIgnoreCase))
                    AddScore(scores, "APP_PROCESS", 25, signal);
                else
                {
                    // Any other process consuming high CPU — report it
                    findings.Add(new AskDiagnosticFinding
                    {
                        Type = "heavy_query",
                        Severity = sev == "CRITICAL" ? "critical" : "high",
                        Title = $"Process '{metric}' using {numVal}% CPU",
                        Detail = detail,
                        CpuTimeMs = (long)numVal
                    });
                }
                continue;
            }

            // ── SQL Server Memory Pressure ───────────────────────────────
            if (cat == "SQLMEMORY")
            {
                if (met == "TOTALSQLSERVERMEMORY" || met == "TOTALSQLSERVERMEMORYMB")
                {
                    // Parse SqlPctOfHost from detail
                    var pctMatch = detail.IndexOf("SqlPctOfHost=", StringComparison.OrdinalIgnoreCase);
                    double sqlPctOfHost = 0;
                    if (pctMatch >= 0)
                    {
                        var pctStart = pctMatch + 13;
                        var pctEnd = detail.IndexOf('%', pctStart);
                        if (pctEnd > pctStart)
                            double.TryParse(detail[pctStart..pctEnd], out sqlPctOfHost);
                    }

                    if (sqlPctOfHost >= 85 || sev is "CRITICAL")
                    {
                        AddScore(scores, "SQL_MEMORY", 40, signal);
                        findings.Add(new AskDiagnosticFinding
                        {
                            Type = "memory_pressure",
                            Severity = "critical",
                            Title = $"SQL Server consuming {numVal:#,##0}MB ({sqlPctOfHost:F0}% of host RAM)",
                            Detail = $"All SQL Server instances combined are using {sqlPctOfHost:F0}% of host memory. {detail}"
                        });
                    }
                    else if (sqlPctOfHost >= 60 || sev == "HIGH")
                    {
                        AddScore(scores, "SQL_MEMORY", 25, signal);
                        findings.Add(new AskDiagnosticFinding
                        {
                            Type = "memory_pressure",
                            Severity = "high",
                            Title = $"SQL Server consuming {numVal:#,##0}MB ({sqlPctOfHost:F0}% of host RAM)",
                            Detail = $"SQL Server instances are using significant host memory. {detail}"
                        });
                    }
                }
                else if (met.Contains("WORKINGSETMB") && sev is "HIGH" or "CRITICAL")
                {
                    // Per-instance memory
                    findings.Add(new AskDiagnosticFinding
                    {
                        Type = "memory_pressure",
                        Severity = sev == "CRITICAL" ? "critical" : "high",
                        Title = $"SQL Server instance using {numVal:#,##0}MB RAM — {metric}",
                        Detail = detail
                    });
                }
                continue;
            }

            // ── SQL Server Service status ────────────────────────────────
            if (cat == "SQLSERVICE")
            {
                if (sev == "HIGH")
                {
                    AddScore(scores, "SQL_CPU", 15, signal);
                    findings.Add(new AskDiagnosticFinding
                    {
                        Type = "config_concern",
                        Severity = "high",
                        Title = $"SQL Service not running: {metric} = {value}",
                        Detail = detail
                    });
                }
                continue;
            }

            // ── Scheduled Tasks running ──────────────────────────────────
            if (cat == "SCHEDULEDTASK")
            {
                if (met == "TOTALRUNNINGTASKS")
                {
                    if (numVal >= 5)
                    {
                        AddScore(scores, "APP_PROCESS", 20, signal);
                        findings.Add(new AskDiagnosticFinding
                        {
                            Type = "maintenance_job",
                            Severity = "high",
                            Title = $"{(int)numVal} scheduled tasks running concurrently",
                            Detail = "Many scheduled tasks running may compete for CPU and memory. " + detail
                        });
                    }
                }
                else if (sev is "HIGH")
                {
                    AddScore(scores, "APP_PROCESS", 15, signal);
                    findings.Add(new AskDiagnosticFinding
                    {
                        Type = "maintenance_job",
                        Severity = "high",
                        Title = $"Scheduled task running: {metric}",
                        Detail = detail
                    });
                }
                continue;
            }

            // ── PowerShell / WS-Man sessions ─────────────────────────────
            if (cat == "PSSESSION")
            {
                if (met == "TOTALPSSESSIONS" && numVal >= 3)
                {
                    var memTotal = "";
                    var tmIdx = detail.IndexOf("TotalMemMB=", StringComparison.OrdinalIgnoreCase);
                    if (tmIdx >= 0) memTotal = detail[(tmIdx + 11)..detail.IndexOf(';', tmIdx > 0 ? tmIdx : 0)];

                    AddScore(scores, "APP_PROCESS", numVal >= 10 ? 25 : 15, signal);
                    findings.Add(new AskDiagnosticFinding
                    {
                        Type = "heavy_query",
                        Severity = numVal >= 10 ? "high" : "medium",
                        Title = $"{(int)numVal} PowerShell/wsmprovhost sessions active (total {memTotal}MB)",
                        Detail = detail
                    });
                }
                else if (sev is "HIGH" or "CRITICAL")
                {
                    findings.Add(new AskDiagnosticFinding
                    {
                        Type = "heavy_query",
                        Severity = "high",
                        Title = $"PS session {metric} using {numVal}MB RAM",
                        Detail = detail
                    });
                }
                continue;
            }

            // ── IIS App Pool (trim quotes from appcmd) ──────────────────
            if (cat == "IISAPPPOOL")
            {
                var cleanValue = value.Trim('"', ' ');
                if (!cleanValue.Equals("Started", StringComparison.OrdinalIgnoreCase))
                {
                    AddScore(scores, "IIS_APPPOOL", 25, signal);
                    findings.Add(new AskDiagnosticFinding
                    {
                        Type = "config_concern",
                        Severity = "high",
                        Title = $"IIS App Pool '{metric.Trim('"', ' ')}' is {cleanValue}",
                        Detail = "Application pool is not running — web applications may be down."
                    });
                }
                continue;
            }

            // ── Background processes ────────────────────────────────────
            if (cat == "BACKGROUNDPROCESS")
            {
                AddScore(scores, "BACKUP_OR_SCANNER", 15, signal);
                continue;
            }

            // ── Pending Reboot ─────────────────────────────────────────
            if (cat == "MAINTENANCE" && met.Contains("PENDINGREBOOT"))
            {
                if (value.Equals("YES", StringComparison.OrdinalIgnoreCase))
                {
                    AddScore(scores, "WINDOWS_MEMORY", 15, "PendingReboot=YES");
                    findings.Add(new AskDiagnosticFinding
                    {
                        Type = "config_concern",
                        Severity = "medium",
                        Title = "Pending reboot detected",
                        Detail = "Server has pending OS updates or configuration changes that require a reboot. " +
                                 "Until restarted, patches are unapplied and some services may behave unexpectedly."
                    });
                }
                continue;
            }

            // ── Event Log Errors (1-hour spike) ───────────────────────────
            if (cat == "EVENTLOG")
            {
                if (met.Contains("ERRORSLAST1HOUR"))
                {
                    if (numVal >= 50)
                    {
                        AddScore(scores, "WINDOWS_CPU", 20, signal); // Event storm can cause CPU
                        findings.Add(new AskDiagnosticFinding
                        {
                            Type = "config_concern",
                            Severity = "critical",
                            Title = $"{(int)numVal} error/critical events in last hour",
                            Detail = "Event log storm — indicates a recurring failure (service crash, driver error, or security event). " +
                                     "Check Application and System logs for the top repeating source."
                        });
                    }
                    else if (numVal >= 10)
                    {
                        AddScore(scores, "WINDOWS_CPU", 10, signal);
                        findings.Add(new AskDiagnosticFinding
                        {
                            Type = "config_concern",
                            Severity = "high",
                            Title = $"{(int)numVal} error events in last hour",
                            Detail = detail
                        });
                    }
                }
                else if (met.Contains("LATESTERROR"))
                {
                    findings.Add(new AskDiagnosticFinding
                    {
                        Type = "config_concern",
                        Severity = sev == "HIGH" ? "high" : "medium",
                        Title = $"Latest error: {metric.Replace("LatestError:", "", StringComparison.OrdinalIgnoreCase)}",
                        Detail = $"Event ID {value}: {detail}"
                    });
                }
                continue;
            }

            // ── Windows Service Failures ──────────────────────────────────
            if (cat == "SERVICE" && met.Contains(":STATUS"))
            {
                if (!value.Equals("Running", StringComparison.OrdinalIgnoreCase)
                    && detail.Contains("Automatic", StringComparison.OrdinalIgnoreCase))
                {
                    var svcName = met.Replace(":STATUS", "", StringComparison.OrdinalIgnoreCase);
                    AddScore(scores, "WINDOWS_CPU", 15, signal);
                    findings.Add(new AskDiagnosticFinding
                    {
                        Type = "config_concern",
                        Severity = "high",
                        Title = $"Service '{svcName}' is {value} (should be Running)",
                        Detail = $"Automatic service is not running. {detail}"
                    });
                }
                continue;
            }

            // ── SQL Service Status ────────────────────────────────────────
            if (cat == "SQLSERVICE" && met.Contains(":STATUS"))
            {
                if (!value.Equals("Running", StringComparison.OrdinalIgnoreCase)
                    && detail.Contains("Automatic", StringComparison.OrdinalIgnoreCase))
                {
                    var svcName = met.Replace(":STATUS", "", StringComparison.OrdinalIgnoreCase);
                    AddScore(scores, "SQL_CPU", 30, signal);
                    findings.Add(new AskDiagnosticFinding
                    {
                        Type = "config_concern",
                        Severity = "critical",
                        Title = $"SQL Service '{svcName}' is {value}",
                        Detail = $"SQL Server service is down. {detail}"
                    });
                }
                continue;
            }

            // ── TCP Connection Flood ──────────────────────────────────────
            if (cat == "TCP" && met.Contains("STATE:ESTABLISHED") && numVal >= 1000)
            {
                AddScore(scores, "APP_PROCESS", 20, signal);
                findings.Add(new AskDiagnosticFinding
                {
                    Type = "connection_flood",
                    Severity = "high",
                    Title = $"{(int)numVal} established TCP connections",
                    Detail = "High connection count may indicate connection pool leak or DDoS."
                });
                continue;
            }

            // ── Network throughput ────────────────────────────────────────
            if (cat == "NETWORK" && sev is "HIGH" or "CRITICAL")
            {
                findings.Add(new AskDiagnosticFinding
                {
                    Type = "config_concern",
                    Severity = "high",
                    Title = $"High network throughput on {metric}: {numVal:F1} MB/s",
                    Detail = detail
                });
                continue;
            }

            // ── Volume low space ────────────────────────────────────────
            if (cat == "VOLUME" && met.Contains("FREEPCT"))
            {
                if (numVal <= 1)
                {
                    AddScore(scores, "WINDOWS_DISK", 40, signal);
                    findings.Add(new AskDiagnosticFinding
                    {
                        Type = "io_latency",
                        Severity = "critical",
                        Title = $"Drive {metric.Replace(":FREEPCT", "", StringComparison.OrdinalIgnoreCase)} almost full ({numVal:F1}% free)",
                        Detail = "Less than 1% free — imminent risk of service failure. " + detail
                    });
                }
                else if (numVal <= 5)
                {
                    AddScore(scores, "WINDOWS_DISK", 25, signal);
                    findings.Add(new AskDiagnosticFinding
                    {
                        Type = "io_latency",
                        Severity = "high",
                        Title = $"Drive {metric.Replace(":FREEPCT", "", StringComparison.OrdinalIgnoreCase)} low on space ({numVal:F1}% free)",
                        Detail = detail
                    });
                }
                else if (numVal <= 15)
                {
                    AddScore(scores, "WINDOWS_DISK", 10, signal);
                    findings.Add(new AskDiagnosticFinding
                    {
                        Type = "io_latency",
                        Severity = "medium",
                        Title = $"Drive {metric.Replace(":FREEPCT", "", StringComparison.OrdinalIgnoreCase)} at {numVal:F1}% free",
                        Detail = detail
                    });
                }
            }
        }

        // Sort and build cause scores
        var ranked = scores
            .Where(kv => kv.Value.Score > 0)
            .OrderByDescending(kv => kv.Value.Score)
            .ToList();

        var topBucket = ranked.FirstOrDefault();
        var topScore = topBucket.Value.Score;

        var causeSummary = ranked.Select(kv => new AskCauseScore
        {
            Bucket = kv.Key,
            Label = BucketToHumanLabel(kv.Key),
            Score = Math.Min(kv.Value.Score, 100),
            Signals = kv.Value.Signals.Take(5).ToList()
        }).ToList();

        // Determine status — require real actionable findings for critical/warning
        var hasActionableFindings = findings.Any(f => f.Severity is "critical" or "high");
        string status;
        if (topScore >= 40 && hasActionableFindings)
            status = "critical";
        else if (topScore >= 15 && hasActionableFindings)
            status = "warning";
        else if (topScore >= 15)
            status = "info"; // scores exist but only from low-severity cumulative data
        else
            status = "healthy";

        var primaryCause = hasActionableFindings
            ? BucketToCauseStatement(topBucket.Key)
            : topScore > 0
                ? $"Minor signals detected ({BucketToHumanLabel(topBucket.Key)}) — no immediate action needed"
                : "No issues detected — server appears healthy";

        // Server-specific actions
        var serverActions = hasActionableFindings
            ? BuildDiagnosticActions(topBucket.Key, ranked, null)
            : ["No immediate action required — continue monitoring."];

        return new AskServerDiagnostic
        {
            Server = server,
            Status = status,
            PrimaryCause = primaryCause,
            Owner = hasActionableFindings ? BucketToOwner(topBucket.Key) : "Monitoring",
            CauseScores = causeSummary,
            Findings = findings
                .OrderByDescending(f => f.Severity == "critical" ? 4 : f.Severity == "high" ? 3 : f.Severity == "medium" ? 2 : 1)
                .Take(15)
                .ToList(),
            Actions = serverActions
        };
    }

    private static void ParseActiveRequestDetail(string detail,
        out string db, out string status, out string command, out int blockerSpid, out string sqlText)
    {
        db = ""; status = ""; command = ""; blockerSpid = 0; sqlText = "";

        foreach (var part in detail.Split(';'))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("db=", StringComparison.OrdinalIgnoreCase))
                db = trimmed[3..].Trim();
            else if (trimmed.StartsWith("status=", StringComparison.OrdinalIgnoreCase))
                status = trimmed[7..].Trim();
            else if (trimmed.StartsWith("command=", StringComparison.OrdinalIgnoreCase))
                command = trimmed[8..].Trim();
            else if (trimmed.StartsWith("blocker=", StringComparison.OrdinalIgnoreCase))
                int.TryParse(trimmed[8..].Trim(), out blockerSpid);
            else if (trimmed.StartsWith("sql=", StringComparison.OrdinalIgnoreCase))
                sqlText = trimmed[4..].Trim();
        }
    }

    private static Dictionary<string, (int Score, List<string> Signals)> CreateEmptyBuckets() => new()
    {
        ["SQL_BLOCKING"] = (0, []),
        ["SQL_CPU"] = (0, []),
        ["SQL_IO"] = (0, []),
        ["SQL_MEMORY"] = (0, []),
        ["SQL_AGENT_JOB"] = (0, []),
        ["SQL_INDEX_MAINTENANCE"] = (0, []),
        ["WINDOWS_CPU"] = (0, []),
        ["WINDOWS_MEMORY"] = (0, []),
        ["WINDOWS_DISK"] = (0, []),
        ["IIS_APPPOOL"] = (0, []),
        ["APP_PROCESS"] = (0, []),
        ["BACKUP_OR_SCANNER"] = (0, []),
    };

    private static void AddScore(Dictionary<string, (int Score, List<string> Signals)> scores, string bucket, int points, string signal)
    {
        if (!scores.TryGetValue(bucket, out var current))
            current = (0, []);
        scores[bucket] = (current.Score + points, current.Signals);
        if (current.Signals.Count < 8)
            current.Signals.Add(signal);
    }

    private static string BucketToHumanLabel(string bucket) => bucket switch
    {
        "SQL_BLOCKING" => "SQL Blocking",
        "SQL_CPU" => "SQL CPU Pressure",
        "SQL_IO" => "SQL I/O Latency",
        "SQL_MEMORY" => "SQL Memory Pressure",
        "SQL_AGENT_JOB" => "SQL Agent Job",
        "SQL_INDEX_MAINTENANCE" => "Index Maintenance",
        "WINDOWS_CPU" => "Host CPU",
        "WINDOWS_MEMORY" => "Host Memory",
        "WINDOWS_DISK" => "Disk I/O",
        "IIS_APPPOOL" => "IIS Application Pool",
        "APP_PROCESS" => "Application Process",
        "BACKUP_OR_SCANNER" => "Backup / Scanner",
        _ => bucket
    };

    private static string BucketToCauseStatement(string? bucket) => bucket switch
    {
        "SQL_BLOCKING" => "SQL Server blocking is the primary bottleneck — sessions are waiting on locks held by other queries.",
        "SQL_CPU" => "SQL Server is consuming excessive CPU — check top CPU-consuming queries and MAXDOP settings.",
        "SQL_IO" => "SQL Server I/O latency is elevated — storage is slow, causing PAGEIOLATCH waits and high file latency.",
        "SQL_MEMORY" => "SQL Server is consuming most of the host memory — review max server memory settings across all instances.",
        "SQL_AGENT_JOB" => "A SQL Agent job is generating significant workload — review running jobs and their impact.",
        "SQL_INDEX_MAINTENANCE" => "Index maintenance (rebuild/reorganize) is running — this causes blocking, I/O pressure, and CPU load.",
        "WINDOWS_CPU" => "Host CPU is saturated at the OS level — identify which process is consuming CPU.",
        "WINDOWS_MEMORY" => "Host memory is under pressure — OS-level memory is nearly exhausted.",
        "WINDOWS_DISK" => "Disk I/O latency or low disk space is degrading performance.",
        "IIS_APPPOOL" => "IIS application pool issue detected — one or more pools may be stopped.",
        "APP_PROCESS" => "Application processes (w3wp, PowerShell, scheduled tasks) are consuming high resources.",
        "BACKUP_OR_SCANNER" => "Backup, antivirus scanner, or background maintenance is generating load.",
        _ => "No clear root cause identified — servers appear healthy."
    };

    private static string BucketToOwner(string? bucket) => bucket switch
    {
        "SQL_BLOCKING" or "SQL_CPU" or "SQL_IO" or "SQL_MEMORY" or "SQL_AGENT_JOB" or "SQL_INDEX_MAINTENANCE"
            => "DBA Team",
        "IIS_APPPOOL" or "APP_PROCESS"
            => "Application Team",
        "WINDOWS_CPU" or "WINDOWS_MEMORY" or "WINDOWS_DISK" or "BACKUP_OR_SCANNER"
            => "Infra Team",
        _ => "Shared"
    };

    private static List<string> BuildDiagnosticActions(string? topBucket,
        List<KeyValuePair<string, (int Score, List<string> Signals)>> ranked,
        List<AskServerDiagnostic>? serverDiagnostics)
    {
        var actions = new List<string>();

        switch (topBucket)
        {
            case "SQL_BLOCKING":
                actions.Add("Identify the lead blocker SPID and review its executing statement — kill it if safe.");
                actions.Add("Check if blocking is caused by an index rebuild or long-running maintenance job.");
                actions.Add("Review 'blocked process threshold' sp_configure setting — enable blocked process report.");
                break;
            case "SQL_CPU":
                actions.Add("Identify the top CPU-consuming user sessions and review their query plans.");
                actions.Add("Review MAXDOP and cost threshold for parallelism settings.");
                actions.Add("Check for missing indexes causing expensive scans.");
                break;
            case "SQL_IO":
                actions.Add("Review file I/O latency by database — the databases with highest write latency need attention.");
                actions.Add("Check for PAGEIOLATCH waits indicating storage bottleneck.");
                actions.Add("Verify disk subsystem health — consider moving hot files to faster storage.");
                break;
            case "SQL_MEMORY":
                actions.Add("Review 'max server memory (MB)' for each SQL Server instance — sum of all instances should leave 4GB+ for the OS.");
                actions.Add("Check if multiple SQL instances are competing for RAM — reduce max memory per instance.");
                actions.Add("Check pending memory grants — queries waiting for memory indicate internal SQL pressure.");
                break;
            case "SQL_AGENT_JOB":
                actions.Add("Review running SQL Agent jobs — consider pausing non-critical ones.");
                actions.Add("Reschedule maintenance and rebuild jobs to off-peak hours.");
                break;
            case "SQL_INDEX_MAINTENANCE":
                actions.Add("Consider pausing or rescheduling the running index rebuild — it's causing blocking.");
                actions.Add("Switch from REBUILD to REORGANIZE for reduced blocking impact (online rebuild if Enterprise).");
                actions.Add("Review MAXDOP setting for maintenance operations to reduce parallel CPU pressure.");
                break;
            case "WINDOWS_CPU":
                actions.Add("Identify the top CPU process — is it sqlservr.exe, w3wp.exe, or another service?");
                actions.Add("Check for runaway processes or unexpected high-CPU background tasks.");
                break;
            case "WINDOWS_MEMORY":
                actions.Add("Identify which process is consuming the most memory.");
                actions.Add("Check SQL max server memory — it may be over-allocated relative to host RAM.");
                break;
            case "WINDOWS_DISK":
                actions.Add("Check disk queue length and latency counters — sustained queue > 2 is concerning.");
                actions.Add("Verify volume free space — volumes below 5% free degrade performance.");
                actions.Add("Check for backup or antivirus scanning generating I/O load.");
                break;
            case "IIS_APPPOOL":
                actions.Add("Check stopped application pools and restart if needed.");
                actions.Add("Review IIS request queue and worker process health.");
                break;
            case "APP_PROCESS":
                actions.Add("Check for PowerShell scripts running continuously from scheduled tasks.");
                actions.Add("Identify which w3wp process is consuming high CPU — check the associated application pool.");
                actions.Add("Review running scheduled tasks and wsmprovhost sessions for unexpected resource use.");
                break;
            case "BACKUP_OR_SCANNER":
                actions.Add("Check for active backup or antivirus scan processes — they generate heavy I/O.");
                actions.Add("Schedule scans and backups during off-peak hours.");
                break;
        }

        // Add secondary actions from contributing factors
        var secondary = ranked.Skip(1).FirstOrDefault();
        if (secondary.Key is not null && secondary.Value.Score >= 25)
        {
            actions.Add($"Also investigate: {BucketToHumanLabel(secondary.Key)} (score: {Math.Min(secondary.Value.Score, 100)}).");
        }

        // Add per-server callout if multiple servers have different issues
        if (serverDiagnostics is { Count: > 1 })
        {
            var critical = serverDiagnostics.Where(sd => sd.Status == "critical").Select(sd => sd.Server).ToList();
            if (critical.Count > 0 && critical.Count < serverDiagnostics.Count)
            {
                actions.Add($"Priority servers: {string.Join(", ", critical)} — these need immediate attention.");
            }
        }

        return actions.Take(6).ToList();
    }
}
