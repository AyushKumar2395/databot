using System.Text.Json;
using System.Text.RegularExpressions;
using Application.Common.Models;

namespace Infrastructure.Services;

internal static partial class MockLlmBehavior
{
    private const string DangerousRequestMessage =
        "BLOCKED: DANGEROUS_REQUEST - Only safe read-only diagnostics and inventory questions are allowed.";

    private static readonly string[] GreetingWords =
    [
        "hi", "hello", "hey", "good morning", "good afternoon", "good evening"
    ];

    private static readonly string[] SexualWords =
    [
        "sex", "sexual", "erotic", "porn", "sexting", "nude"
    ];

    private static readonly string[] SqlKeywords =
    [
        "sql", "database", "databases", "db", "query", "table", "index", "backup", "restore", "login",
        "user", "users", "principal", "principals", "server principal", "database principal",
        "wait", "waits", "wait stats", "wait statistics", "blocking", "blocked", "lock", "locks",
        "session", "sessions", "job", "jobs", "agent", "tempdb", "deadlock",
        "always on", "availability group", "replication", "replica", "hadr",
        "active requests", "who is active", "sp_who", "sp_whoisactive", "running requests",
        "fragmentation", "fragmented", "index health", "missing index",
        "transaction log", "log space", "log file", "log usage",
        "version", "build", "service pack", "cumulative update",
        "table size", "table sizes", "top table", "cpu query", "expensive query", "long running",
        "top cpu", "log", "size", "disk space",
        "configuration", "configure", "maxdop", "max degree", "max memory", "cost threshold",
        "server property", "serverproperty", "setting", "option", "trace flag",
        "permission", "role", "schema", "column", "object", "filegroup", "partition",
        "statistics", "linked server", "errorlog", "error log", "compatibility",
        "collation", "recovery model", "suspect", "mirror", "log shipping",
        "credential", "certificate", "encryption", "audit", "policy"
    ];

    private static readonly string[] WindowsKeywords =
    [
        "windows", "server", "service", "process", "event log", "disk", "drive", "drives", "volume", "storage",
        "memory", "cpu", "port", "firewall", "patch", "iis", "cluster", "certificate", "network", "uptime",
        "hotfix", "hotfixes", "installed update", "processor", "ram", "restarted", "rebooted", "boot",
        "event", "ip address", "network adapter", "application pool", "w3svc"
    ];

    private static readonly string[] WindowsDangerousTokens =
    [
        "shutdown", "poweroff", "restart-computer", "stop-process", "remove-item", "set-itemproperty", "format-volume",
        "stop service", "start service", "restart service", "disable firewall", "enable firewall",
        "create user", "delete user", "change password", "install", "uninstall"
    ];

    private static readonly string[] RestartInquiryTokens =
    [
        "when", "last", "history", "unexpected", "pending", "required", "time", "was", "got"
    ];

    public static string BuildTuneLine(string rawQuestion, string environmentTag, string routedQueryCode)
    {
        var queryCodeEcho = routedQueryCode ?? string.Empty;
        var question = rawQuestion?.Trim() ?? string.Empty;

        // Phase 1: Validation
        if (string.IsNullOrWhiteSpace(question) || IsGreetingOnly(question) || !HasMeaningfulContent(question))
            return $"MISMATCH: EMPTY_OR_UNCLEAR - Please ask a clear question.||{queryCodeEcho}";

        if (EnvironmentRules.IsGeneral(environmentTag) && ContainsAny(question, SexualWords))
        {
            return
                $"GENERAL_REFUSAL: I can't help with sexual content. I can help with general, educational, or technical questions instead.||{queryCodeEcho}";
        }

        // History environments accept broad monitoring keywords — skip Live-only mismatch checks.
        if (!EnvironmentRules.IsHistory(environmentTag))
        {
            if (EnvironmentRules.IsSqlServer(environmentTag) && !ContainsAny(question, SqlKeywords))
            {
                return
                    $"MISMATCH: SQLSERVER_ONLY - Only SQL Server related questions are allowed in this mode. Choose Windows/General for other topics.||{queryCodeEcho}";
            }

            if (EnvironmentRules.IsWindows(environmentTag) && !ContainsAny(question, WindowsKeywords))
            {
                return
                    $"MISMATCH: WINDOWS_ONLY - Only Windows/Infrastructure questions are allowed in this mode. Choose SQL/General for other topics.||{queryCodeEcho}";
            }
        }

        if (IsDangerousRequest(question, environmentTag))
            return $"{DangerousRequestMessage}||{queryCodeEcho}";

        // Phase 2: Tuning (grammar + normalization for template/LLM selection).
        var tuned = TuneQuestion(question, environmentTag);
        return $"{tuned}||{queryCodeEcho}";
    }

    public static string BuildGenerateScript(string environmentTag, string tunedQuestion)
    {
        // History environments generate centralized T-SQL — handle before Live checks.
        if (EnvironmentRules.IsHistory(environmentTag))
            return BuildHistoryScript(environmentTag, tunedQuestion);

        if (EnvironmentRules.IsSqlServer(environmentTag))
        {
            // ── Drift / Compare detection ───────────────────────────────────
            if (ContainsAny(tunedQuestion, new[] { "compare", "drift", "difference", "diff ", "side by side", "compare all", "compare selected", "compare server", "configuration compare", "config drift" }))
            {
                return SqlServerDriftScript;
            }

            if (ContainsAny(tunedQuestion, new[] { "failed job", "failed jobs", "job failure" }))
            {
                return """
WITH failed_jobs AS (
    SELECT TOP (50)
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
    DB_NAME() AS [DatabaseName],
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

            if (ContainsAny(tunedQuestion, new[] { "larger than", "greater than", "bigger than" }) &&
                tunedQuestion.Contains("gb", StringComparison.OrdinalIgnoreCase) &&
                ContainsAny(tunedQuestion, new[] { "database", "databases" }))
            {
                var gbMatch = Regex.Match(tunedQuestion, @"(\d+)\s*gb", RegexOptions.IgnoreCase);
                var gb = gbMatch.Success && int.TryParse(gbMatch.Groups[1].Value, out var parsed) ? parsed : 10;
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
    DB_NAME() AS [DatabaseName],
    GETDATE() AS [CapturedAt],
    d.name AS [Database],
    CAST(ds.TotalSizeBytes / (1024.0 * 1024 * 1024) AS decimal(18, 2)) AS [SizeGB]
FROM sys.databases AS d
INNER JOIN db_size AS ds ON d.database_id = ds.database_id
WHERE ds.TotalSizeBytes > CAST({gb} AS bigint) * 1024 * 1024 * 1024
ORDER BY ds.TotalSizeBytes DESC;
""";
            }

            if (ContainsAny(tunedQuestion, new[] { "backup history", "backup set", "backups last" }))
            {
                return """
DECLARE @Days int = 7;
WITH backup_history AS (
    SELECT TOP (50)
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
    DB_NAME() AS [DatabaseName],
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

            // Wait statistics
            if (ContainsAny(tunedQuestion, new[] { "wait stats", "wait statistics", "top wait", "top waits", "wait type" }))
            {
                return """
SELECT TOP (50)
    @@SERVERNAME AS [ServerName],
    DB_NAME() AS [DatabaseName],
    GETDATE() AS [CapturedAt],
    ws.wait_type AS [WaitType],
    ws.waiting_tasks_count AS [WaitingTasksCount],
    ws.wait_time_ms AS [WaitTimeMs],
    ws.max_wait_time_ms AS [MaxWaitTimeMs],
    ws.signal_wait_time_ms AS [SignalWaitTimeMs],
    ws.wait_time_ms - ws.signal_wait_time_ms AS [ResourceWaitTimeMs]
FROM sys.dm_os_wait_stats AS ws
WHERE ws.wait_type NOT IN (
    'SLEEP_TASK','BROKER_TO_FLUSH','BROKER_EVENTHANDLER','CHECKPOINT_QUEUE',
    'DBMIRROR_EVENTS_QUEUE','DISPATCHER_QUEUE_SEMAPHORE','FT_IFTS_SCHEDULER_IDLE_WAIT',
    'HADR_FILESTREAM_IOMGR_IOCOMPLETION','HADR_WORK_QUEUE','LAZYWRITER_SLEEP',
    'LOGMGR_QUEUE','ONDEMAND_TASK_QUEUE','REQUEST_FOR_DEADLOCK_SEARCH',
    'RESOURCE_QUEUE','SERVER_IDLE_CHECK','SLEEP_DBSTARTUP','SLEEP_DBRECOVER',
    'SLEEP_MASTERDBREADY','SLEEP_MASTERMDREADY','SLEEP_MASTERUPGRADED',
    'SLEEP_MSDBSTARTUP','SLEEP_TEMPDBSTARTUP','SLEEP_SYSTEMTASK',
    'SNI_HTTP_ACCEPT','SP_SERVER_DIAGNOSTICS_SLEEP','SQLTRACE_BUFFER_FLUSH',
    'SQLTRACE_INCREMENTAL_FLUSH_SLEEP','WAIT_XTP_OFFLINE_CKPT_NEW_LOG',
    'WAITFOR','XIO_IDLE','XE_DISPATCHER_WAIT','XE_TIMER_EVENT',
    'BROKER_RECEIVE_WAITFOR','CLR_AUTO_EVENT','CLR_MANUAL_EVENT','SQLTRACE_WAIT_ENTRIES')
ORDER BY ws.wait_time_ms DESC;
""";
            }

            // Blocking / active requests
            if (ContainsAny(tunedQuestion, new[] { "blocking", "blocked process", "active request", "active requests", "who is active", "running quer", "sp_whoisactive", "active session" }))
            {
                return """
SELECT TOP (50)
    @@SERVERNAME AS [ServerName],
    DB_NAME() AS [DatabaseName],
    GETDATE() AS [CapturedAt],
    r.session_id AS [SessionId],
    r.blocking_session_id AS [BlockingSessionId],
    r.status AS [Status],
    r.wait_type AS [WaitType],
    r.wait_time AS [WaitTimeMs],
    r.cpu_time AS [CpuTimeMs],
    r.total_elapsed_time AS [ElapsedTimeMs],
    DB_NAME(r.database_id) AS [RequestDatabase],
    SUBSTRING(st.text,
        (r.statement_start_offset / 2) + 1,
        ((CASE r.statement_end_offset WHEN -1 THEN DATALENGTH(st.text) ELSE r.statement_end_offset END
          - r.statement_start_offset) / 2) + 1) AS [SqlText]
FROM sys.dm_exec_requests AS r
CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) AS st
WHERE r.session_id > 50
ORDER BY r.total_elapsed_time DESC;
""";
            }

            // Index fragmentation
            if (ContainsAny(tunedQuestion, new[] { "fragmentation", "fragmented", "index health", "index frag" }))
            {
                return """
SELECT TOP (50)
    @@SERVERNAME AS [ServerName],
    DB_NAME() AS [DatabaseName],
    GETDATE() AS [CapturedAt],
    DB_NAME(ips.database_id) AS [FragDatabase],
    OBJECT_NAME(ips.object_id, ips.database_id) AS [TableName],
    i.name AS [IndexName],
    ips.index_type_desc AS [IndexType],
    CAST(ips.avg_fragmentation_in_percent AS decimal(5, 2)) AS [FragmentationPct],
    ips.page_count AS [PageCount]
FROM sys.dm_db_index_physical_stats(DB_ID(), NULL, NULL, NULL, 'LIMITED') AS ips
INNER JOIN sys.indexes AS i ON ips.object_id = i.object_id AND ips.index_id = i.index_id
WHERE ips.avg_fragmentation_in_percent > 10
  AND ips.page_count > 100
ORDER BY ips.avg_fragmentation_in_percent DESC;
""";
            }

            // Missing indexes
            if (ContainsAny(tunedQuestion, new[] { "missing index", "missing indexes" }))
            {
                return """
SELECT TOP (50)
    @@SERVERNAME AS [ServerName],
    DB_NAME() AS [DatabaseName],
    GETDATE() AS [CapturedAt],
    DB_NAME(mid.database_id) AS [MissingIndexDatabase],
    OBJECT_NAME(mid.object_id, mid.database_id) AS [TableName],
    CAST(migs.avg_total_user_cost * migs.avg_user_impact * (migs.user_seeks + migs.user_scans) AS decimal(18, 2)) AS [ImprovementMeasure],
    mid.equality_columns AS [EqualityColumns],
    mid.inequality_columns AS [InequalityColumns],
    mid.included_columns AS [IncludedColumns],
    migs.user_seeks AS [UserSeeks],
    migs.user_scans AS [UserScans],
    CAST(migs.avg_user_impact AS decimal(5, 2)) AS [AvgUserImpactPct]
FROM sys.dm_db_missing_index_group_stats AS migs
INNER JOIN sys.dm_db_missing_index_groups AS mig ON migs.group_handle = mig.index_group_handle
INNER JOIN sys.dm_db_missing_index_details AS mid ON mig.index_handle = mid.index_handle
ORDER BY migs.avg_total_user_cost * migs.avg_user_impact * (migs.user_seeks + migs.user_scans) DESC;
""";
            }

            // Top CPU-consuming queries
            if (ContainsAny(tunedQuestion, new[] { "top cpu", "expensive quer", "costly quer", "cpu quer", "resource intensive", "high cpu" }))
            {
                return """
SELECT TOP (50)
    @@SERVERNAME AS [ServerName],
    DB_NAME() AS [DatabaseName],
    GETDATE() AS [CapturedAt],
    qs.total_worker_time / qs.execution_count AS [AvgCpuTimeUs],
    qs.total_elapsed_time / qs.execution_count AS [AvgElapsedTimeUs],
    qs.execution_count AS [ExecutionCount],
    qs.total_logical_reads / qs.execution_count AS [AvgLogicalReads],
    DB_NAME(st.dbid) AS [QueryDatabase],
    SUBSTRING(st.text,
        (qs.statement_start_offset / 2) + 1,
        ((CASE qs.statement_end_offset WHEN -1 THEN DATALENGTH(st.text) ELSE qs.statement_end_offset END
          - qs.statement_start_offset) / 2) + 1) AS [QueryText]
FROM sys.dm_exec_query_stats AS qs
CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) AS st
WHERE qs.execution_count > 0
ORDER BY qs.total_worker_time / qs.execution_count DESC;
""";
            }

            // Transaction log space
            if (ContainsAny(tunedQuestion, new[] { "log space", "transaction log", "log usage", "log file size", "log full" }))
            {
                return """
SELECT TOP (50)
    @@SERVERNAME AS [ServerName],
    DB_NAME() AS [DatabaseName],
    GETDATE() AS [CapturedAt],
    d.name AS [Database],
    d.recovery_model_desc AS [RecoveryModel],
    SUM(CASE WHEN mf.type = 1 THEN CAST(mf.size AS bigint) * 8 / 1024 ELSE 0 END) AS [LogSizeMB],
    SUM(CASE WHEN mf.type = 0 THEN CAST(mf.size AS bigint) * 8 / 1024 ELSE 0 END) AS [DataSizeMB],
    SUM(CAST(mf.size AS bigint) * 8 / 1024) AS [TotalSizeMB]
FROM sys.databases AS d
INNER JOIN sys.master_files AS mf ON d.database_id = mf.database_id
GROUP BY d.name, d.recovery_model_desc
ORDER BY LogSizeMB DESC;
""";
            }

            // Always-On / Availability Group health
            if (ContainsAny(tunedQuestion, new[] { "always on", "availability group", "ag health", "replica", "hadr", "secondary replica" }))
            {
                return """
SELECT TOP (50)
    @@SERVERNAME AS [ServerName],
    DB_NAME() AS [DatabaseName],
    GETDATE() AS [CapturedAt],
    ag.name AS [AvailabilityGroup],
    ar.replica_server_name AS [ReplicaServer],
    ars.role_desc AS [Role],
    ars.operational_state_desc AS [OperationalState],
    ars.synchronization_health_desc AS [SynchronizationHealth],
    ars.connected_state_desc AS [ConnectedState],
    ar.availability_mode_desc AS [AvailabilityMode],
    ar.failover_mode_desc AS [FailoverMode]
FROM sys.availability_groups AS ag
INNER JOIN sys.availability_replicas AS ar ON ag.group_id = ar.group_id
INNER JOIN sys.dm_hadr_availability_replica_states AS ars ON ar.replica_id = ars.replica_id
ORDER BY ag.name, ar.replica_server_name;
""";
            }

            // SQL Server version / build info
            if (ContainsAny(tunedQuestion, new[] { "version", "sql version", "build", "product version", "@@version", "service pack", "cumulative update" }))
            {
                return """
SELECT
    @@SERVERNAME AS [ServerName],
    DB_NAME() AS [DatabaseName],
    GETDATE() AS [CapturedAt],
    @@VERSION AS [FullVersion],
    CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(50)) AS [ProductVersion],
    CAST(SERVERPROPERTY('ProductLevel') AS nvarchar(50)) AS [ProductLevel],
    CAST(SERVERPROPERTY('ProductUpdateLevel') AS nvarchar(50)) AS [ProductUpdateLevel],
    CAST(SERVERPROPERTY('Edition') AS nvarchar(50)) AS [Edition],
    CAST(SERVERPROPERTY('EngineEdition') AS int) AS [EngineEdition],
    CAST(SERVERPROPERTY('Collation') AS nvarchar(50)) AS [Collation],
    CAST(SERVERPROPERTY('MachineName') AS nvarchar(50)) AS [MachineName];
""";
            }

            // Table sizes
            if (ContainsAny(tunedQuestion, new[] { "table size", "largest table", "biggest table", "top table", "table space" }))
            {
                return """
SELECT TOP (50)
    @@SERVERNAME AS [ServerName],
    DB_NAME() AS [DatabaseName],
    GETDATE() AS [CapturedAt],
    s.name AS [SchemaName],
    t.name AS [TableName],
    SUM(p.rows) AS [RowCount],
    CAST(SUM(au.total_pages) * 8.0 / 1024 AS decimal(18, 2)) AS [TotalSizeMB],
    CAST(SUM(au.used_pages) * 8.0 / 1024 AS decimal(18, 2)) AS [UsedSizeMB],
    CAST((SUM(au.total_pages) - SUM(au.used_pages)) * 8.0 / 1024 AS decimal(18, 2)) AS [UnusedSizeMB]
FROM sys.tables AS t
INNER JOIN sys.schemas AS s ON t.schema_id = s.schema_id
INNER JOIN sys.indexes AS i ON t.object_id = i.object_id
INNER JOIN sys.partitions AS p ON i.object_id = p.object_id AND i.index_id = p.index_id
INNER JOIN sys.allocation_units AS au ON p.partition_id = au.container_id
WHERE t.is_ms_shipped = 0
GROUP BY s.name, t.name
ORDER BY SUM(au.total_pages) DESC;
""";
            }

            // Server logins
            if (ContainsAny(tunedQuestion, new[] { "login", "logins", "server principal", "server principals", "sql login", "windows login" }))
            {
                return """
SELECT TOP (50)
    @@SERVERNAME AS [ServerName],
    DB_NAME() AS [DatabaseName],
    GETDATE() AS [CapturedAt],
    sp.name AS [LoginName],
    sp.type_desc AS [LoginType],
    sp.is_disabled AS [IsDisabled],
    sp.create_date AS [CreatedDate],
    sp.modify_date AS [ModifiedDate],
    ISNULL(sl.is_policy_checked, 0) AS [IsPolicyChecked],
    ISNULL(sl.is_expiration_checked, 0) AS [IsExpirationChecked]
FROM sys.server_principals AS sp
LEFT JOIN sys.sql_logins AS sl ON sp.principal_id = sl.principal_id
WHERE sp.type IN ('S', 'U', 'G')
  AND sp.name NOT LIKE '##%'
ORDER BY sp.type_desc, sp.name;
""";
            }

            // TempDB usage
            if (ContainsAny(tunedQuestion, new[] { "tempdb", "temp db", "temp database", "tempdb usage", "tempdb space" }))
            {
                return """
SELECT TOP (50)
    @@SERVERNAME AS [ServerName],
    DB_NAME() AS [DatabaseName],
    GETDATE() AS [CapturedAt],
    s.session_id AS [SessionId],
    s.login_name AS [LoginName],
    s.host_name AS [HostName],
    s.program_name AS [ProgramName],
    su.user_objects_alloc_page_count AS [UserObjAllocPages],
    su.internal_objects_alloc_page_count AS [InternalObjAllocPages],
    (su.user_objects_alloc_page_count + su.internal_objects_alloc_page_count) * 8 / 1024 AS [TempDbUsageMB]
FROM sys.dm_db_session_space_usage AS su
INNER JOIN sys.dm_exec_sessions AS s ON su.session_id = s.session_id
WHERE (su.user_objects_alloc_page_count + su.internal_objects_alloc_page_count) > 0
ORDER BY (su.user_objects_alloc_page_count + su.internal_objects_alloc_page_count) DESC;
""";
            }

            // Long-running queries
            if (ContainsAny(tunedQuestion, new[] { "long running", "slow quer", "running long", "high elapsed", "long query" }))
            {
                return """
SELECT TOP (50)
    @@SERVERNAME AS [ServerName],
    DB_NAME() AS [DatabaseName],
    GETDATE() AS [CapturedAt],
    r.session_id AS [SessionId],
    r.status AS [Status],
    r.total_elapsed_time AS [ElapsedTimeMs],
    r.cpu_time AS [CpuTimeMs],
    r.logical_reads AS [LogicalReads],
    r.writes AS [Writes],
    DB_NAME(r.database_id) AS [RequestDatabase],
    SUBSTRING(st.text,
        (r.statement_start_offset / 2) + 1,
        ((CASE r.statement_end_offset WHEN -1 THEN DATALENGTH(st.text) ELSE r.statement_end_offset END
          - r.statement_start_offset) / 2) + 1) AS [SqlText]
FROM sys.dm_exec_requests AS r
CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) AS st
WHERE r.session_id > 50
  AND r.total_elapsed_time > 5000
ORDER BY r.total_elapsed_time DESC;
""";
            }

            return """
SELECT TOP (50)
    @@SERVERNAME AS [ServerName],
    DB_NAME() AS [DatabaseName],
    GETDATE() AS [CapturedAt],
    d.name AS [Database],
    d.state_desc AS [State],
    d.recovery_model_desc AS [RecoveryModel]
FROM sys.databases AS d
ORDER BY d.name;
""";
        }

        if (EnvironmentRules.IsWindows(environmentTag))
        {
            // ── Drift / Compare detection ───────────────────────────────────
            if (ContainsAny(tunedQuestion, new[] { "compare", "drift", "difference", "diff ", "side by side", "compare all", "compare selected", "compare server", "compare windows", "config drift" }))
            {
                return WindowsDriftScript;
            }

            // CPU / processor
            if (ContainsAny(tunedQuestion, new[] { "cpu", "processor", "processor usage", "cpu usage", "cpu load" }))
            {
                return """
param([string]$TargetServer)
$Result = @()
try {
    if ([string]::IsNullOrWhiteSpace($TargetServer)) { $TargetServer = $env:COMPUTERNAME }
    $cpus = Get-CimInstance -ClassName Win32_Processor -ErrorAction Stop
    foreach ($cpu in $cpus) {
        $Result += [pscustomobject]@{
            ServerName   = $TargetServer
            CapturedAt   = Get-Date
            Status       = 'OK'
            ErrorMessage = $null
            Name         = $cpu.Name
            LoadPercent  = $cpu.LoadPercentage
            Cores        = $cpu.NumberOfCores
            LogicalProcs = $cpu.NumberOfLogicalProcessors
            MaxClockMHz  = $cpu.MaxClockSpeed
        }
    }
} catch {
    $Result += [pscustomobject]@{
        ServerName   = if ([string]::IsNullOrWhiteSpace($TargetServer)) { $env:COMPUTERNAME } else { $TargetServer }
        CapturedAt   = Get-Date
        Status       = 'ERROR'
        ErrorMessage = $_.Exception.Message
    }
}
$Result
""";
            }

            // Memory / RAM
            if (ContainsAny(tunedQuestion, new[] { "memory", "ram", "physical memory", "memory usage", "free memory" }))
            {
                return """
param([string]$TargetServer)
$Result = @()
try {
    if ([string]::IsNullOrWhiteSpace($TargetServer)) { $TargetServer = $env:COMPUTERNAME }
    $os = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
    $Result += [pscustomobject]@{
        ServerName         = $TargetServer
        CapturedAt         = Get-Date
        Status             = 'OK'
        ErrorMessage       = $null
        TotalPhysicalMemGB = [math]::Round($os.TotalVisibleMemorySize / 1MB, 2)
        FreePhysicalMemGB  = [math]::Round($os.FreePhysicalMemory / 1MB, 2)
        UsedPhysicalMemGB  = [math]::Round(($os.TotalVisibleMemorySize - $os.FreePhysicalMemory) / 1MB, 2)
        UsedMemPercent     = [math]::Round(100 * ($os.TotalVisibleMemorySize - $os.FreePhysicalMemory) / $os.TotalVisibleMemorySize, 1)
    }
} catch {
    $Result += [pscustomobject]@{
        ServerName   = if ([string]::IsNullOrWhiteSpace($TargetServer)) { $env:COMPUTERNAME } else { $TargetServer }
        CapturedAt   = Get-Date
        Status       = 'ERROR'
        ErrorMessage = $_.Exception.Message
    }
}
$Result
""";
            }

            // Running processes
            if (ContainsAny(tunedQuestion, new[] { "process", "processes", "running process", "top process" }))
            {
                return """
param([string]$TargetServer)
$Result = @()
try {
    if ([string]::IsNullOrWhiteSpace($TargetServer)) { $TargetServer = $env:COMPUTERNAME }
    $procs = Get-Process -ErrorAction Stop | Sort-Object CPU -Descending | Select-Object -First 50
    foreach ($p in $procs) {
        $Result += [pscustomobject]@{
            ServerName   = $TargetServer
            CapturedAt   = Get-Date
            Status       = 'OK'
            ErrorMessage = $null
            ProcessName  = $p.ProcessName
            PID          = $p.Id
            CPUSeconds   = [math]::Round($p.CPU, 2)
            MemoryMB     = [math]::Round($p.WorkingSet64 / 1MB, 2)
            StartTime    = $p.StartTime
        }
    }
} catch {
    $Result += [pscustomobject]@{
        ServerName   = if ([string]::IsNullOrWhiteSpace($TargetServer)) { $env:COMPUTERNAME } else { $TargetServer }
        CapturedAt   = Get-Date
        Status       = 'ERROR'
        ErrorMessage = $_.Exception.Message
    }
}
$Result
""";
            }

            // Event log errors (last 24h)
            if (ContainsAny(tunedQuestion, new[] { "event log", "error event", "event viewer", "windows event", "system event", "application event", "event error" }))
            {
                return """
param([string]$TargetServer)
$Result = @()
try {
    if ([string]::IsNullOrWhiteSpace($TargetServer)) { $TargetServer = $env:COMPUTERNAME }
    $cutoff = (Get-Date).AddHours(-24)
    $events = Get-WinEvent -FilterHashtable @{ LogName = 'System','Application'; Level = 1,2; StartTime = $cutoff } `
              -ErrorAction SilentlyContinue | Select-Object -First 50
    if ($events) {
        foreach ($e in $events) {
            $Result += [pscustomobject]@{
                ServerName   = $TargetServer
                CapturedAt   = Get-Date
                Status       = 'OK'
                ErrorMessage = $null
                TimeCreated  = $e.TimeCreated
                LogName      = $e.LogName
                Level        = $e.LevelDisplayName
                Source       = $e.ProviderName
                EventId      = $e.Id
                Message      = ($e.Message -replace '\r?\n', ' ').Substring(0, [math]::Min(500, $e.Message.Length))
            }
        }
    } else {
        $Result += [pscustomobject]@{
            ServerName   = $TargetServer
            CapturedAt   = Get-Date
            Status       = 'OK'
            ErrorMessage = $null
            Message      = 'No critical or error events in the last 24 hours.'
        }
    }
} catch {
    $Result += [pscustomobject]@{
        ServerName   = if ([string]::IsNullOrWhiteSpace($TargetServer)) { $env:COMPUTERNAME } else { $TargetServer }
        CapturedAt   = Get-Date
        Status       = 'ERROR'
        ErrorMessage = $_.Exception.Message
    }
}
$Result
""";
            }

            // Installed patches / hotfixes
            if (ContainsAny(tunedQuestion, new[] { "patch", "patches", "hotfix", "hotfixes", "installed update", "windows update", "kb" }))
            {
                return """
param([string]$TargetServer)
$Result = @()
try {
    if ([string]::IsNullOrWhiteSpace($TargetServer)) { $TargetServer = $env:COMPUTERNAME }
    $hotfixes = Get-HotFix -ErrorAction Stop | Sort-Object InstalledOn -Descending | Select-Object -First 50
    foreach ($hf in $hotfixes) {
        $Result += [pscustomobject]@{
            ServerName   = $TargetServer
            CapturedAt   = Get-Date
            Status       = 'OK'
            ErrorMessage = $null
            HotFixID     = $hf.HotFixID
            Description  = $hf.Description
            InstalledOn  = $hf.InstalledOn
            InstalledBy  = $hf.InstalledBy
        }
    }
} catch {
    $Result += [pscustomobject]@{
        ServerName   = if ([string]::IsNullOrWhiteSpace($TargetServer)) { $env:COMPUTERNAME } else { $TargetServer }
        CapturedAt   = Get-Date
        Status       = 'ERROR'
        ErrorMessage = $_.Exception.Message
    }
}
$Result
""";
            }

            // Uptime / last reboot
            if (ContainsAny(tunedQuestion, new[] { "uptime", "last boot", "last restart", "last reboot", "restarted", "rebooted", "boot time" }))
            {
                return """
param([string]$TargetServer)
$Result = @()
try {
    if ([string]::IsNullOrWhiteSpace($TargetServer)) { $TargetServer = $env:COMPUTERNAME }
    $os = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
    $uptime = (Get-Date) - $os.LastBootUpTime
    $Result += [pscustomobject]@{
        ServerName    = $TargetServer
        CapturedAt    = Get-Date
        Status        = 'OK'
        ErrorMessage  = $null
        LastBootTime  = $os.LastBootUpTime
        UptimeDays    = [math]::Round($uptime.TotalDays, 2)
        UptimeHours   = [math]::Round($uptime.TotalHours, 2)
        UptimeDisplay = '{0}d {1}h {2}m' -f $uptime.Days, $uptime.Hours, $uptime.Minutes
    }
} catch {
    $Result += [pscustomobject]@{
        ServerName   = if ([string]::IsNullOrWhiteSpace($TargetServer)) { $env:COMPUTERNAME } else { $TargetServer }
        CapturedAt   = Get-Date
        Status       = 'ERROR'
        ErrorMessage = $_.Exception.Message
    }
}
$Result
""";
            }

            // Network adapters / IP configuration
            if (ContainsAny(tunedQuestion, new[] { "network", "ip address", "network adapter", "network interface", "ip config", "ipconfig" }))
            {
                return """
param([string]$TargetServer)
$Result = @()
try {
    if ([string]::IsNullOrWhiteSpace($TargetServer)) { $TargetServer = $env:COMPUTERNAME }
    $adapters = Get-CimInstance -ClassName Win32_NetworkAdapterConfiguration -ErrorAction Stop |
                Where-Object { $_.IPEnabled -eq $true }
    foreach ($a in $adapters) {
        $Result += [pscustomobject]@{
            ServerName   = $TargetServer
            CapturedAt   = Get-Date
            Status       = 'OK'
            ErrorMessage = $null
            Description  = $a.Description
            IPAddress    = ($a.IPAddress -join ', ')
            SubnetMask   = ($a.IPSubnet -join ', ')
            DefaultGW    = ($a.DefaultIPGateway -join ', ')
            DNSServers   = ($a.DNSServerSearchOrder -join ', ')
            MACAddress   = $a.MACAddress
            DHCPEnabled  = $a.DHCPEnabled
        }
    }
} catch {
    $Result += [pscustomobject]@{
        ServerName   = if ([string]::IsNullOrWhiteSpace($TargetServer)) { $env:COMPUTERNAME } else { $TargetServer }
        CapturedAt   = Get-Date
        Status       = 'ERROR'
        ErrorMessage = $_.Exception.Message
    }
}
$Result
""";
            }

            // Disk / drive / volume space
            if (ContainsAny(tunedQuestion, new[] { "disk", "drive", "drives", "volume", "storage", "free space", "disk space" }))
            {
                return """
param([string]$TargetServer)
$Result = @()
try {
    if ([string]::IsNullOrWhiteSpace($TargetServer)) { $TargetServer = $env:COMPUTERNAME }
    $disks = Get-CimInstance -ClassName Win32_LogicalDisk -ErrorAction Stop
    foreach ($disk in $disks) {
        $sizeMB  = [math]::Round($disk.Size      / 1MB, 2)
        $freeMB  = [math]::Round($disk.FreeSpace / 1MB, 2)
        $usedMB  = [math]::Round(($disk.Size - $disk.FreeSpace) / 1MB, 2)
        $pctUsed = if ($disk.Size -gt 0) { [math]::Round(100 * ($disk.Size - $disk.FreeSpace) / $disk.Size, 1) } else { 0 }
        $Result += [pscustomobject]@{
            ServerName     = $TargetServer
            CapturedAt     = Get-Date
            Status         = 'OK'
            ErrorMessage   = $null
            DeviceID       = $disk.DeviceID
            VolumeName     = $disk.VolumeName
            DriveType      = $disk.DriveType
            TotalSizeMB    = $sizeMB
            FreeMB         = $freeMB
            UsedMB         = $usedMB
            PercentageUsed = $pctUsed
        }
    }
} catch {
    $Result += [pscustomobject]@{
        ServerName   = if ([string]::IsNullOrWhiteSpace($TargetServer)) { $env:COMPUTERNAME } else { $TargetServer }
        CapturedAt   = Get-Date
        Status       = 'ERROR'
        ErrorMessage = $_.Exception.Message
    }
}
$Result
""";
            }

            // IIS / web server / application pools
            if (ContainsAny(tunedQuestion, new[] { "iis", "web server", "application pool", "w3svc", "app pool", "website" }))
            {
                return """
param([string]$TargetServer)
$Result = @()
try {
    if ([string]::IsNullOrWhiteSpace($TargetServer)) { $TargetServer = $env:COMPUTERNAME }
    Import-Module WebAdministration -ErrorAction Stop
    $pools = Get-ChildItem IIS:\AppPools -ErrorAction Stop
    foreach ($pool in $pools) {
        $Result += [pscustomobject]@{
            ServerName      = $TargetServer
            CapturedAt      = Get-Date
            Status          = 'OK'
            ErrorMessage    = $null
            AppPoolName     = $pool.Name
            AppPoolState    = $pool.State
            ManagedRuntime  = $pool.managedRuntimeVersion
            PipelineMode    = $pool.managedPipelineMode
            AutoStart       = $pool.autoStart
        }
    }
} catch {
    $Result += [pscustomobject]@{
        ServerName   = if ([string]::IsNullOrWhiteSpace($TargetServer)) { $env:COMPUTERNAME } else { $TargetServer }
        CapturedAt   = Get-Date
        Status       = 'ERROR'
        ErrorMessage = $_.Exception.Message
    }
}
$Result
""";
            }

            return """
param([string]$TargetServer)
$Result = @()
try {
  if ([string]::IsNullOrWhiteSpace($TargetServer)) { $TargetServer = $env:COMPUTERNAME }
  $os = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
  $Result += [pscustomobject]@{
    ServerName = $TargetServer
    CapturedAt = Get-Date
    Status = 'OK'
    ErrorMessage = $null
    Caption = $os.Caption
    Version = $os.Version
    LastBootUpTime = $os.LastBootUpTime
  }
} catch {
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

        if (EnvironmentRules.IsGeneral(environmentTag))
            return BuildGeneralAnswer(tunedQuestion);

        return string.Empty;
    }

    public static string BuildHistoryScript(string environmentTag, string tunedQuestion)
    {
        var serverFilter = EnvironmentRules.IsSqlServer(environmentTag)
            ? "(@Server IS NULL OR h.SQLServer = @Server)"
            : "(@Server IS NULL OR h.WinServer = @Server)";
        var serverCol = EnvironmentRules.IsSqlServer(environmentTag) ? "h.SQLServer" : "h.WinServer";
        var table = EnvironmentRules.IsSqlServer(environmentTag)
            ? "[SQLGig].[Monitor].[SQLServer_Details_History] AS h"
            : "[SQLGig].[Monitor].[WINServer_Details_History] AS h";

        // Question-aware metric selection — parse keywords to pick the right column.
        var (metricGroup, metricName, metricExpr) = ResolveHistoryMetric(environmentTag, tunedQuestion);

        return $"""
DECLARE @Server nvarchar(128) = NULL;
DECLARE @FromUtc datetime2(0) = DATEADD(HOUR,-24,SYSUTCDATETIME());
DECLARE @ToUtc datetime2(0) = SYSUTCDATETIME();
DECLARE @Top int = 200;
SELECT TOP (@Top)
    {serverCol} AS [ServerName],
    h.DateTime AS [CapturedAtUtc],
    N'{metricGroup}' COLLATE DATABASE_DEFAULT AS [MetricGroup],
    N'{metricName}' COLLATE DATABASE_DEFAULT AS [MetricName],
    CAST({metricExpr} AS decimal(18,2)) AS [MetricValue],
    N'Mock history query' COLLATE DATABASE_DEFAULT AS [Detail]
FROM {table}
WHERE h.DateTime >= @FromUtc
  AND h.DateTime < @ToUtc
  AND {serverFilter}
ORDER BY h.DateTime DESC;
""";
    }

    /// <summary>
    /// Maps user question keywords to the correct History table column.
    /// Returns (MetricGroup, MetricName, SqlExpression) for the mock script.
    /// </summary>
    private static (string Group, string Name, string Expr) ResolveHistoryMetric(string env, string question)
    {
        var q = question.ToLowerInvariant();
        var isSql = EnvironmentRules.IsSqlServer(env);

        // ── Memory / RAM ──
        if (q.Contains("ram") || q.Contains("memory") || q.Contains("available memory"))
        {
            if (isSql) return ("Memory", "AvailableMemory_GB", "h.AvailableMemory_GB");
            return ("Memory", "AvailableMemory", "h.AvailableMemory");
        }
        if (q.Contains("memory usage") || q.Contains("mem usage") || q.Contains("memory percent"))
        {
            if (isSql) return ("Memory", "UsedMemory_GB", "h.UsedMemory_GB");
            return ("Memory", "MemoryUsage", "h.MemoryUsage");
        }
        if (q.Contains("ple") || q.Contains("page life"))
            return ("Memory", "PageLifeExpectancy_seconds", "h.PageLifeExpectancy_seconds");
        if (q.Contains("memory grant") || q.Contains("grants pending"))
            return ("Memory", "MemoryGrantsPending", "h.MemoryGrantsPending");

        // ── Disk ──
        if (q.Contains("disk queue") || q.Contains("queue length"))
        {
            return isSql
                ? ("Disk", "DiskQueueLength", "h.DiskQueueLength")
                : ("Disk", "AvgDiskQueueLengthTotal", "h.AvgDiskQueueLengthTotal");
        }
        if (q.Contains("disk read"))
            return isSql ? ("Disk", "DiskReads", "h.PageReads_sec") : ("Disk", "DiskReadsPerSecTotal", "h.DiskReadsPerSecTotal");
        if (q.Contains("disk write"))
            return isSql ? ("Disk", "DiskWrites", "h.PageWrites_sec") : ("Disk", "DiskWritesPerSecTotal", "h.DiskWritesPerSecTotal");
        if (q.Contains("disk") || q.Contains("io"))
        {
            return isSql
                ? ("Disk", "PageReads_sec", "h.PageReads_sec")
                : ("Disk", "PercentageDiskTimeTotal", "h.PercentageDiskTimeTotal");
        }

        // ── Network ──
        if (q.Contains("network") || q.Contains("bandwidth") || q.Contains("bytes sent"))
            return ("Network", "NetworkBytesSent_KBPS", "h.NetworkBytesSent_KBPS");
        if (q.Contains("bytes received") || q.Contains("network received"))
            return ("Network", "NetworkBytesReceived_KBPS", "h.NetworkBytesReceived_KBPS");

        // ── Blocking / Deadlocks ──
        if (q.Contains("blocking"))
            return ("Blocking", "BlockingCount", "h.BlockingCount");
        if (q.Contains("deadlock"))
            return ("Blocking", "DeadlockCount", "h.DeadlockCount");
        if (q.Contains("lock"))
            return ("Blocking", "LockCount", "h.LockCount");

        // ── Connections / Sessions ──
        if (q.Contains("connection") || q.Contains("user connection") || q.Contains("session"))
            return isSql ? ("Sessions", "UserConnections", "h.UserConnections") : ("Sessions", "ProcessCount", "h.ProcessCount");

        // ── Jobs / Backups ──
        if (q.Contains("failed job") || q.Contains("job fail"))
            return ("Jobs", "FailedJobCount", "h.FailedJobCount");
        if (q.Contains("backup") || q.Contains("failed backup"))
            return ("Backups", "FailedFullBackupCount", "h.FailedFullBackupCount");

        // ── SQL Performance ──
        if (q.Contains("batch request") || q.Contains("batch"))
            return ("Performance", "BatchRequests_sec", "h.BatchRequests_sec");
        if (q.Contains("transaction") || q.Contains("txn"))
            return ("Performance", "Transactions_sec", "h.Transactions_sec");
        if (q.Contains("compilat") || q.Contains("recompil"))
            return ("Performance", "SQLReCompilations_sec", "h.SQLReCompilations_sec");

        // ── Waits ──
        if (q.Contains("wait"))
            return ("Waits", "WaitStats", "h.WaitStats");

        // ── Windows: Process / Thread ──
        if (q.Contains("process count") || q.Contains("processes"))
            return ("System", "ProcessCount", "h.ProcessCount");
        if (q.Contains("thread"))
            return ("System", "ThreadCount", "h.ThreadCount");
        if (q.Contains("handle"))
            return ("System", "TotalHandles", "h.TotalHandles");

        // ── CPU (default) ──
        if (isSql) return ("InstanceHealth", "PageLifeExpectancy_seconds", "h.PageLifeExpectancy_seconds");
        return ("CPU", "PercentProcessorTime", "h.PercentProcessorTime");
    }

    public static string BuildValidateTemplateJson(string environmentTag, string tunedQuestion, string promptTemplate)
    {
        _ = environmentTag;
        _ = tunedQuestion;

        var safeScript = ExtractRenderedScriptFromValidatePrompt(promptTemplate);
        var payload = new
        {
            isValid = true,
            changesMade = false,
            changeSummary = "No patch needed in mock validator.",
            validatedScript = safeScript,
            updatedBoundParameters = new { },
            confidence = 0.75
        };

        return JsonSerializer.Serialize(payload);
    }

    private static string ExtractRenderedScriptFromValidatePrompt(string promptTemplate)
    {
        if (string.IsNullOrWhiteSpace(promptTemplate))
            return string.Empty;

        var startMarker = "Current Rendered Script:";
        var endMarker = "Safety Policy JSON:";
        var start = promptTemplate.IndexOf(startMarker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return string.Empty;

        start += startMarker.Length;
        var end = promptTemplate.IndexOf(endMarker, start, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
            end = promptTemplate.Length;

        return promptTemplate[start..end].Trim();
    }

    public static string BuildGeneralAnswer(string question)
    {
        if (string.IsNullOrWhiteSpace(question))
            return "Please ask a clear question.";

        if (ContainsAny(question, SexualWords))
        {
            return
                "I can't help with sexual content. I can help with general, educational, or technical questions instead.";
        }

        var n = Normalize(question);

        // Return structured JSON so TryParseGeneralAnswerJson can parse it,
        // even in mock/fallback mode the UI gets a proper answer layout.
        return $$"""
        {
          "title": "{{EscapeJson(n)}}",
          "explanation": "This is a general knowledge question. An LLM provider is required to generate detailed answers. Please configure a valid API key for Claude, Gemini, or OpenAI in appsettings.json.",
          "summary": [
            "DataBot requires a configured LLM provider to answer general questions.",
            "Currently no LLM API key is available — showing a placeholder response.",
            "Configure Claude, Gemini, or OpenAI API keys to enable real answers.",
            "SQL Server and Windows environments can still generate and execute scripts."
          ],
          "sections": [
            {
              "key": "overview",
              "title": "Your Question",
              "icon": "Compass",
              "tone": "info",
              "bullets": ["{{EscapeJson(n)}}"]
            },
            {
              "key": "setup",
              "title": "How to Enable Real Answers",
              "icon": "Settings",
              "tone": "warning",
              "steps": [
                "Open appsettings.json in the Databot project.",
                "Add your API key under LLM:Claude:ApiKey, LLM:Gemini:ApiKey, or LLM:OpenAI:ApiKey.",
                "Ensure the LLMModels table has a model with UseForTune=1 and a valid provider.",
                "Restart the API — General questions will then return real LLM answers."
              ]
            },
            {
              "key": "alternatives",
              "title": "What Works Without LLM",
              "icon": "CheckCircle",
              "tone": "ok",
              "bullets": [
                "SQL Server Live/History — script generation uses mock fallback scripts.",
                "Windows Live/History — PowerShell scripts work with mock fallback.",
                "Heartbeat Monitor — fully functional without LLM.",
                "Question Samples — pre-authored scripts execute without LLM."
              ]
            },
            {
              "key": "note",
              "title": "Note",
              "icon": "Info",
              "tone": "info",
              "bullets": ["This placeholder appears because no LLM API key is configured or the LLM call failed."]
            }
          ]
        }
        """;
    }

    private static string EscapeJson(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");

    public static AskAnswerNode BuildStructuredGeneralAnswer(string question, LlmModelDefinition model)
    {
        var normalized = Normalize(question);

        if (string.IsNullOrWhiteSpace(question))
        {
            return new AskAnswerNode
            {
                Status = "OK",
                Severity = "INFO",
                Title = "Clarification Needed",
                Summary = ["Please provide a clear question so I can assist you."],
                Details = "No question was provided. Please rephrase your request.",
                Explanation = "Please ask a clear question.",
                Model = new AskModelRef { Provider = model.Provider, ModelKey = model.ModelKey }
            };
        }

        if (ContainsAny(question, SexualWords))
        {
            return new AskAnswerNode
            {
                Status = "OK",
                Severity = "INFO",
                Title = "Request Declined",
                Summary = ["This type of content is not supported."],
                Details = "I can't help with sexual content. I can help with general, educational, or technical questions instead.",
                Explanation = "I can't help with sexual content. I can help with general, educational, or technical questions instead.",
                Sections =
                [
                    new AnswerSection
                    {
                        Key = "overview", Title = "Content Policy", Icon = "ShieldCheck", Tone = "warning",
                        Bullets = ["This type of content is not supported by DataBot.", "Please ask a general, educational, or technical question instead."]
                    },
                    new AnswerSection
                    {
                        Key = "alternatives", Title = "What You Can Ask", Icon = "Compass", Tone = "info",
                        Bullets = ["SQL Server diagnostics and inventory.", "Windows Server health and configuration.", "General technical or educational questions."]
                    },
                    new AnswerSection
                    {
                        Key = "guidance", Title = "How to Proceed", Icon = "Lightbulb", Tone = "ok",
                        Steps = ["Rephrase your question as a technical or educational query."]
                    },
                    new AnswerSection
                    {
                        Key = "policy", Title = "Policy Note", Icon = "Info", Tone = "info",
                        Bullets = ["DataBot is designed for enterprise operations support."]
                    }
                ],
                Model = new AskModelRef { Provider = model.Provider, ModelKey = model.ModelKey }
            };
        }

        return new AskAnswerNode
        {
            Status = "OK",
            Severity = "INFO",
            Title = normalized,
            Summary =
            [
                $"This answer covers: {normalized}.",
                "DataBot provides general information based on enterprise knowledge.",
                "For deeper analysis, refine the question with specific details.",
                "Switch to SqlServer_Live or Windows_Live for executable diagnostics."
            ],
            Details = $"Overview:\n- {normalized}",
            Explanation = $"DataBot has prepared a general answer for your question about {normalized}. Review the sections below for structured information.",
            Sections =
            [
                new AnswerSection
                {
                    Key = "overview", Title = "Overview", Icon = "Compass", Tone = "info",
                    Bullets = [$"General answer for: {normalized}.", "This is a knowledge-based response, not an executable query."]
                },
                new AnswerSection
                {
                    Key = "principles", Title = "Key Points", Icon = "BookOpen", Tone = "info",
                    Bullets = [normalized, "Consult official documentation for authoritative details."]
                },
                new AnswerSection
                {
                    Key = "context", Title = "Context", Icon = "Info", Tone = "info",
                    Bullets = ["This answer is generated in General mode (no script execution).", "For live data, switch to SqlServer_Live or Windows_Live."]
                },
                new AnswerSection
                {
                    Key = "next_steps", Title = "Next Steps", Icon = "Lightbulb", Tone = "ok",
                    Steps = ["Review the information above.", "Refine your question with specific details if needed.", "Switch environment for executable diagnostics."]
                }
            ],
            Model = new AskModelRef { Provider = model.Provider, ModelKey = model.ModelKey }
        };
    }

    private static bool IsDangerousRequest(string question, string environmentTag)
    {
        var normalized = Normalize(question).ToLowerInvariant();

        if (EnvironmentRules.IsSqlServer(environmentTag))
            return IsDangerousSqlRequest(normalized);

        if (EnvironmentRules.IsWindows(environmentTag))
            return IsDangerousWindowsRequest(normalized);

        return false;
    }

    private static bool IsDangerousSqlRequest(string normalized)
    {
        if (Regex.IsMatch(
                normalized,
                @"\b(insert\s+into|update\s+\S+|delete\s+from|merge\s+into|truncate\s+table|drop\s+(table|database|index|view|procedure|proc|login|user|schema|role|function|trigger)|alter\s+(table|database|index|view|procedure|proc|login|user|schema|role|function|trigger|event\s+session)|create\s+(table|database|index|view|procedure|proc|login|user|schema|role|function|trigger)|grant\s+\S+\s+to|revoke\s+\S+\s+from|deny\s+\S+\s+to|kill\s+\d+|reconfigure)\b",
                RegexOptions.IgnoreCase))
            return true;

        if (Regex.IsMatch(
                normalized,
                @"\b(backup\s+(database|log)|restore\s+(database|log)|sp_add_job|sp_update_job|sp_delete_job|xevent\s+(start|stop)|event\s+session\s+(start|stop))\b",
                RegexOptions.IgnoreCase))
            return true;

        if (Regex.IsMatch(normalized, @"\b(xp_cmdshell|sp_configure|sp_oacreate|openrowset|opendatasource)\b", RegexOptions.IgnoreCase))
            return true;

        return ContainsRestartCommand(normalized) && !IsAllowedRestartInquiry(normalized);
    }

    private static bool IsDangerousWindowsRequest(string normalized)
    {
        if (ContainsAny(normalized, WindowsDangerousTokens))
            return true;

        return ContainsRestartCommand(normalized) && !IsAllowedRestartInquiry(normalized);
    }

    private static bool ContainsRestartCommand(string normalized)
    {
        return normalized.Contains("restart", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("reboot", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("shutdown", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("poweroff", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAllowedRestartInquiry(string normalized)
    {
        var startsWithImperative =
            normalized.StartsWith("restart ", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("reboot ", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("shutdown ", StringComparison.OrdinalIgnoreCase);

        if (startsWithImperative)
            return false;

        return ContainsAny(normalized, RestartInquiryTokens);
    }

    private static string TuneQuestion(string question, string environmentTag)
    {
        // Deterministic requested mock output for common SQLGIG demo question.
        if (EnvironmentRules.IsSqlServer(environmentTag) &&
            question.Contains("SQLGIG", StringComparison.OrdinalIgnoreCase))
        {
            return "List databases where name contains SQLGIG.";
        }

        var tuned = Normalize(question);
        tuned = CollapseRepeatedSpaces().Replace(tuned, " ");
        tuned = TheThenPhraseRegex().Replace(tuned, " ");
        tuned = CollapseRepeatedSpaces().Replace(tuned, " ");
        tuned = WindowsWordRegex().Replace(tuned, "Windows");
        tuned = SqlServerWordRegex().Replace(tuned, "SQL Server");
        tuned = tuned.Replace(" the version", " version", StringComparison.OrdinalIgnoreCase);

        if (EnvironmentRules.IsWindows(environmentTag))
        {
            if (ContainsRestartCommand(tuned.ToLowerInvariant()) && IsAllowedRestartInquiry(tuned.ToLowerInvariant()))
                return "Show when Windows was restarted.";

            if (WindowsVersionPattern().IsMatch(tuned))
                return "Give Windows version.";

            if (ServerDetailsPattern().IsMatch(tuned))
                return "Give server details.";
        }

        if (string.IsNullOrWhiteSpace(tuned))
            return "Show system diagnostics.";

        // Sentence casing and punctuation for a clean tuned prompt.
        tuned = char.ToUpperInvariant(tuned[0]) + tuned[1..];
        if (!tuned.EndsWith('.') && !tuned.EndsWith('!'))
            tuned += ".";

        return tuned;
    }

    private static string Normalize(string question)
    {
        return question.Trim().TrimEnd('.', '?', '!', ';');
    }

    private static bool IsGreetingOnly(string question)
    {
        var normalized = question.Trim().ToLowerInvariant();
        return GreetingWords.Any(word => normalized.Equals(word, StringComparison.Ordinal));
    }

    private static bool HasMeaningfulContent(string question)
    {
        return question.Any(char.IsLetterOrDigit);
    }

    private static bool ContainsAny(string value, IEnumerable<string> candidates)
    {
        return candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex CollapseRepeatedSpaces();

    [GeneratedRegex(@"\bwindows\b", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsWordRegex();

    [GeneratedRegex(@"\bsql\s*server\b", RegexOptions.IgnoreCase)]
    private static partial Regex SqlServerWordRegex();

    [GeneratedRegex(@"\b(give|get|show|list)\s+windows(\s+the)?\s+version\b", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsVersionPattern();

    [GeneratedRegex(@"\b(give|get|show|list)\s+server(\s+the)?\s+details\b", RegexOptions.IgnoreCase)]
    private static partial Regex ServerDetailsPattern();

    [GeneratedRegex(@"\bthe\s+then\b", RegexOptions.IgnoreCase)]
    private static partial Regex TheThenPhraseRegex();

    // ── Chart Plan mock ──────────────────────────────────────────────────────

    /// <summary>Deterministic chart plan fallback for History environments when LLM is unavailable.</summary>
    internal static string BuildChartPlanJson(
        string environment, string question, IReadOnlyList<string> availableFields,
        string? metricLabel = null, string? metricKey = null, string? metricGroup = null)
    {
        var hasTime = availableFields.Any(f => f.Equals("CapturedAtUtc", StringComparison.OrdinalIgnoreCase));
        var hasMetricValue = availableFields.Any(f => f.Equals("MetricValue", StringComparison.OrdinalIgnoreCase));
        var hasServerName = availableFields.Any(f => f.Equals("ServerName", StringComparison.OrdinalIgnoreCase));

        if (!hasMetricValue)
            return """{"enabled":false,"reason":"No numeric MetricValue field available for charting.","charts":[]}""";

        // Text/status metric columns are not chartable — check against whitelist
        if (!string.IsNullOrWhiteSpace(metricKey))
        {
            var entry = AskPipelineService.SqlServerHistoryMetricMap.GetValueOrDefault(metricKey)
                        ?? AskPipelineService.WindowsHistoryMetricMap.GetValueOrDefault(metricKey);
            if (entry is not null && entry.MetricSelectSql == "NULL AS MetricValue")
                return """{"enabled":false,"reason":"Text/status metric is not chartable.","charts":[]}""";
        }

        var qLower = question.ToLowerInvariant();
        var isTrend = qLower.Contains("trend") || qLower.Contains("over time") || qLower.Contains("history")
                      || qLower.Contains("timeline") || qLower.Contains("spike");
        var isTop = qLower.Contains("top") || qLower.Contains("highest") || qLower.Contains("worst")
                    || qLower.Contains("offender");

        // Use metric labels for titles when available
        var trendTitle = !string.IsNullOrWhiteSpace(metricLabel) ? $"{metricLabel} Trend" : "Metric Trend Over Time";
        var areaTitle = !string.IsNullOrWhiteSpace(metricLabel) ? $"{metricLabel} Area Trend" : "Metric Area Trend";
        var compareTitle = !string.IsNullOrWhiteSpace(metricLabel) ? $"{metricLabel} Server Comparison" : "Latest Comparison by Server";
        var barTitle = !string.IsNullOrWhiteSpace(metricLabel) ? $"Top Values — {metricLabel}" : "Top Values by Metric";
        var metricGroupJson = !string.IsNullOrWhiteSpace(metricGroup) ? $"\"{metricGroup.Replace("\"", "\\\"")}\"" : "null";
        var metricNameJson = !string.IsNullOrWhiteSpace(metricLabel) ? $"\"{metricLabel.Replace("\"", "\\\"")}\"" : "null";

        if (hasTime && (isTrend || !isTop))
        {
            return $$"""
            {
              "enabled": true,
              "charts": [
                {
                  "chartId": "area_trend",
                  "chartType": "area",
                  "priority": 1,
                  "title": "{{trendTitle}}",
                  "subtitle": "Trend across selected servers",
                  "xField": "CapturedAtUtc",
                  "yField": "MetricValue",
                  "seriesField": {{(hasServerName ? "\"ServerName\"" : "null")}},
                  "showLegend": {{(hasServerName ? "true" : "false")}},
                  "showMarkers": false,
                  "xAxisLabel": "Time",
                  "yAxisLabel": "Value",
                  "aggregation": "avg",
                  "timeGrain": "auto",
                  "metricGroup": {{metricGroupJson}},
                  "metricName": {{metricNameJson}},
                  "formatHint": "number",
                  "goal": "trend",
                  "allowSeriesToggle": {{(hasServerName ? "true" : "false")}},
                  "allowMaximize": true,
                  "supportedInteractions": ["legendToggle","maximize","download","tooltip","seriesHideShow"],
                  "recommendedHeight": "standard",
                  "colorIntent": {{(hasServerName ? "\"categorical\"" : "\"sequential\"")}}
                },
                {
                  "chartId": "anomaly_line",
                  "chartType": "anomalyLine",
                  "priority": 2,
                  "title": "{{areaTitle}} Anomalies",
                  "subtitle": "Spikes and drops highlighted",
                  "xField": "CapturedAtUtc",
                  "yField": "MetricValue",
                  "seriesField": {{(hasServerName ? "\"ServerName\"" : "null")}},
                  "showLegend": {{(hasServerName ? "true" : "false")}},
                  "showMarkers": true,
                  "xAxisLabel": "Time",
                  "yAxisLabel": "Value",
                  "aggregation": "avg",
                  "timeGrain": "auto",
                  "formatHint": "number",
                  "goal": "anomaly",
                  "allowSeriesToggle": {{(hasServerName ? "true" : "false")}},
                  "allowMaximize": true,
                  "anomaly": {
                    "method": "zscore",
                    "threshold": 2.5,
                    "serverWise": {{(hasServerName ? "true" : "false")}},
                    "hasAnomalies": false,
                    "hasPointAnomalies": false,
                    "hasPeerDeviation": false,
                    "mode": "risk_and_statistical",
                    "message": "No significant anomalies detected in selected period.",
                    "valueField": "MetricValue",
                    "timeField": "CapturedAtUtc",
                    "seriesField": {{(hasServerName ? "\"ServerName\"" : "null")}}
                  },
                  "supportedInteractions": ["legendToggle","maximize","download","tooltip","seriesHideShow"],
                  "recommendedHeight": "standard",
                  "colorIntent": "alert"
                },
                {{(hasServerName ? $$"""
                {
                  "chartId": "heatmap_view",
                  "chartType": "heatmap",
                  "priority": 3,
                  "title": "{{(!string.IsNullOrWhiteSpace(metricLabel) ? $"{metricLabel} Heatmap" : "Metric Heatmap")}}",
                  "subtitle": "Intensity by server and time",
                  "xField": "CapturedAtUtc",
                  "yField": "ServerName",
                  "valueField": "MetricValue",
                  "xAxisLabel": "Time",
                  "yAxisLabel": "Server",
                  "aggregation": "avg",
                  "timeGrain": "15min",
                  "formatHint": "number",
                  "goal": "density",
                  "allowMaximize": true,
                  "timeBucketed": true,
                  "supportedInteractions": ["maximize","download","tooltip"],
                  "recommendedHeight": "tall",
                  "colorIntent": "sequential",
                  "transform": {
                    "type": "group",
                    "groupBy": ["ServerName","CapturedAtUtc"],
                    "aggregateField": "MetricValue",
                    "aggregateFn": "avg"
                  }
                },
                """ : "")}}
                {
                  "chartId": "bar_compare",
                  "chartType": "bar",
                  "priority": 4,
                  "title": "{{compareTitle}}",
                  "subtitle": "Compare selected servers",
                  "categoryField": {{(hasServerName ? "\"ServerName\"" : "null")}},
                  "valueField": "MetricValue",
                  "showLegend": false,
                  "xAxisLabel": "Server",
                  "yAxisLabel": "Value",
                  "aggregation": "latest",
                  "metricGroup": {{metricGroupJson}},
                  "metricName": {{metricNameJson}},
                  "formatHint": "number",
                  "goal": "comparison",
                  "allowMaximize": true,
                  "latestSnapshotOnly": true,
                  "supportedInteractions": ["maximize","download","tooltip"],
                  "recommendedHeight": "compact",
                  "colorIntent": "categorical"
                },
                {
                  "chartId": "forecast_6m",
                  "chartType": "forecastLine",
                  "priority": 5,
                  "title": "{{(!string.IsNullOrWhiteSpace(metricLabel) ? $"{metricLabel} 6-Month Forecast" : "6-Month Forecast")}}",
                  "subtitle": "Predicted trend based on historical pattern",
                  "xField": "CapturedAtUtc",
                  "yField": "MetricValue",
                  "seriesField": {{(hasServerName ? "\"ServerName\"" : "null")}},
                  "showLegend": {{(hasServerName ? "true" : "false")}},
                  "xAxisLabel": "Time",
                  "yAxisLabel": "Value",
                  "aggregation": "avg",
                  "timeGrain": "auto",
                  "formatHint": "number",
                  "goal": "forecast",
                  "allowMaximize": true,
                  "forecast": {
                    "horizon": "6months",
                    "method": "linear_regression",
                    "confidenceLevel": 0.95
                  },
                  "supportedInteractions": ["legendToggle","maximize","download","tooltip"],
                  "recommendedHeight": "tall",
                  "colorIntent": "diverging"
                }
              ]
            }
            """;
        }

        // Bar chart fallback for top/ranking questions
        return $$"""
        {
          "enabled": true,
          "charts": [
            {
              "chartId": "bar_compare",
              "chartType": "bar",
              "priority": 1,
              "title": "{{barTitle}}",
              "subtitle": "Highest metric values in selected period",
              "categoryField": {{(hasServerName ? "\"ServerName\"" : "\"MetricName\"")}},
              "valueField": "MetricValue",
              "showLegend": false,
              "xAxisLabel": "Server",
              "yAxisLabel": "Value",
              "aggregation": "max",
              "formatHint": "number",
              "goal": "ranking",
              "allowMaximize": true,
              "latestSnapshotOnly": true,
              "supportedInteractions": ["maximize","download","tooltip"],
              "recommendedHeight": "standard",
              "colorIntent": "categorical"
            }
          ]
        }
        """;
    }

    // ── Drift Detection Scripts ─────────────────────────────────────────────

    internal const string SqlServerDriftScript = """
-- SQL Server Configuration Drift Detection
-- Collects server properties + sys.configurations in a uniform row format
-- for cross-server comparison. Runs on each selected server independently.

-- Section 1: Server Properties
SELECT
    @@SERVERNAME AS [ServerName],
    SYSUTCDATETIME() AS [CapturedAtUtc],
    N'Server Properties' AS [Category],
    p.[PropertyName] AS [SettingName],
    p.[CurrentValue],
    NULL AS [RunningValue],
    NULL AS [DefaultValue],
    N'Server-level property' AS [Description],
    N'ACTIVE' AS [ConfigStatus]
FROM (
    SELECT N'ProductVersion'     AS [PropertyName], CAST(SERVERPROPERTY('ProductVersion')     AS NVARCHAR(256)) AS [CurrentValue]
    UNION ALL SELECT N'ProductLevel',     CAST(SERVERPROPERTY('ProductLevel')     AS NVARCHAR(256))
    UNION ALL SELECT N'Edition',          CAST(SERVERPROPERTY('Edition')          AS NVARCHAR(256))
    UNION ALL SELECT N'EngineEdition',    CAST(SERVERPROPERTY('EngineEdition')    AS NVARCHAR(256))
    UNION ALL SELECT N'BuildClrVersion',  CAST(SERVERPROPERTY('BuildClrVersion')  AS NVARCHAR(256))
    UNION ALL SELECT N'Collation',        CAST(SERVERPROPERTY('Collation')        AS NVARCHAR(256))
    UNION ALL SELECT N'IsClustered',      CAST(SERVERPROPERTY('IsClustered')      AS NVARCHAR(256))
    UNION ALL SELECT N'IsHadrEnabled',    CAST(SERVERPROPERTY('IsHadrEnabled')    AS NVARCHAR(256))
    UNION ALL SELECT N'IsFullTextInstalled', CAST(SERVERPROPERTY('IsFullTextInstalled') AS NVARCHAR(256))
    UNION ALL SELECT N'FilestreamConfiguredLevel', CAST(SERVERPROPERTY('FilestreamConfiguredLevel') AS NVARCHAR(256))
    UNION ALL SELECT N'HadrManagerStatus', CAST(SERVERPROPERTY('HadrManagerStatus') AS NVARCHAR(256))
    UNION ALL SELECT N'ServerName',       CAST(SERVERPROPERTY('ServerName')       AS NVARCHAR(256))
    UNION ALL SELECT N'InstanceName',     ISNULL(CAST(SERVERPROPERTY('InstanceName') AS NVARCHAR(256)), N'DEFAULT')
    UNION ALL SELECT N'ComputerNamePhysicalNetBIOS', CAST(SERVERPROPERTY('ComputerNamePhysicalNetBIOS') AS NVARCHAR(256))
    UNION ALL SELECT N'ProcessID',        CAST(SERVERPROPERTY('ProcessID')        AS NVARCHAR(256))
    UNION ALL SELECT N'ResourceVersion',  CAST(SERVERPROPERTY('ResourceVersion')  AS NVARCHAR(256))
) AS p

UNION ALL

-- Section 2: sys.configurations (sp_configure settings)
SELECT
    @@SERVERNAME AS [ServerName],
    SYSUTCDATETIME() AS [CapturedAtUtc],
    N'Instance Configuration' AS [Category],
    c.name AS [SettingName],
    CAST(c.value AS NVARCHAR(256)) AS [CurrentValue],
    CAST(c.value_in_use AS NVARCHAR(256)) AS [RunningValue],
    CAST(c.minimum AS NVARCHAR(256)) + N' – ' + CAST(c.maximum AS NVARCHAR(256)) AS [DefaultValue],
    CAST(c.description AS NVARCHAR(256)) AS [Description],
    CASE WHEN c.value <> c.value_in_use THEN N'PENDING_RESTART' ELSE N'ACTIVE' END AS [ConfigStatus]
FROM sys.configurations AS c

UNION ALL

-- Section 3: Database-level settings (recovery model, compat, collation)
SELECT
    @@SERVERNAME AS [ServerName],
    SYSUTCDATETIME() AS [CapturedAtUtc],
    N'Database Settings' AS [Category],
    d.name + N' → ' + prop.[PropertyName] AS [SettingName],
    prop.[CurrentValue],
    NULL AS [RunningValue],
    NULL AS [DefaultValue],
    N'Database-level setting' AS [Description],
    CASE WHEN d.state_desc <> N'ONLINE' THEN d.state_desc ELSE N'ACTIVE' END AS [ConfigStatus]
FROM sys.databases AS d
CROSS APPLY (
    SELECT N'RecoveryModel' AS [PropertyName], d.recovery_model_desc AS [CurrentValue]
    UNION ALL SELECT N'CompatibilityLevel', CAST(d.compatibility_level AS NVARCHAR(20))
    UNION ALL SELECT N'Collation', ISNULL(d.collation_name, N'<inherited>')
    UNION ALL SELECT N'PageVerify', d.page_verify_option_desc
    UNION ALL SELECT N'AutoClose', CASE d.is_auto_close_on WHEN 1 THEN N'ON' ELSE N'OFF' END
    UNION ALL SELECT N'AutoShrink', CASE d.is_auto_shrink_on WHEN 1 THEN N'ON' ELSE N'OFF' END
    UNION ALL SELECT N'AutoCreateStats', CASE d.is_auto_create_stats_on WHEN 1 THEN N'ON' ELSE N'OFF' END
    UNION ALL SELECT N'AutoUpdateStats', CASE d.is_auto_update_stats_on WHEN 1 THEN N'ON' ELSE N'OFF' END
    UNION ALL SELECT N'Trustworthy', CASE d.is_trustworthy_on WHEN 1 THEN N'ON' ELSE N'OFF' END
    UNION ALL SELECT N'BrokerEnabled', CASE d.is_broker_enabled WHEN 1 THEN N'ON' ELSE N'OFF' END
) AS prop
WHERE d.database_id > 4

UNION ALL

-- Section 4: Key memory / scheduler settings
SELECT
    @@SERVERNAME,
    SYSUTCDATETIME(),
    N'Runtime State',
    s.[SettingName],
    s.[CurrentValue],
    NULL, NULL,
    s.[Description],
    N'ACTIVE'
FROM (
    SELECT N'PhysicalMemory_GB' AS [SettingName],
           CAST(CAST(physical_memory_kb / 1048576.0 AS DECIMAL(10,1)) AS NVARCHAR(50)) AS [CurrentValue],
           N'Total physical RAM' AS [Description]
    FROM sys.dm_os_sys_info
    UNION ALL
    SELECT N'LogicalCPUCount',
           CAST(cpu_count AS NVARCHAR(50)),
           N'Logical processors visible to SQL Server'
    FROM sys.dm_os_sys_info
    UNION ALL
    SELECT N'Scheduler_Count',
           CAST(scheduler_count AS NVARCHAR(50)),
           N'Active schedulers'
    FROM sys.dm_os_sys_info
    UNION ALL
    SELECT N'SQLServer_StartTime',
           CONVERT(NVARCHAR(30), sqlserver_start_time, 126),
           N'Instance start time'
    FROM sys.dm_os_sys_info
) AS s
ORDER BY [Category], [SettingName];
""";

    internal const string WindowsDriftScript = """
param([string]$TargetServer)
$Result = @()
if ([string]::IsNullOrWhiteSpace($TargetServer)) { $TargetServer = $env:COMPUTERNAME }

# Helper to add a row
function Add-DriftRow($Category, $SettingName, $CurrentValue, $Description) {
    $script:Result += [pscustomobject]@{
        ServerName    = $TargetServer
        CapturedAt    = Get-Date
        Category      = $Category
        SettingName   = $SettingName
        CurrentValue  = "$CurrentValue"
        Description   = $Description
    }
}

# ── 1. OS Information ────────────────────────────────────────────────────
try {
    $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
    Add-DriftRow 'OS Information' 'OS Version' $os.Version 'Windows version number'
    Add-DriftRow 'OS Information' 'OS Build' $os.BuildNumber 'Windows build number'
    Add-DriftRow 'OS Information' 'OS Caption' $os.Caption 'Windows edition'
    Add-DriftRow 'OS Information' 'OS Architecture' $os.OSArchitecture 'Architecture'
    Add-DriftRow 'OS Information' 'Install Date' ($os.InstallDate.ToString('yyyy-MM-dd')) 'OS installation date'
    Add-DriftRow 'OS Information' 'Last Boot' ($os.LastBootUpTime.ToString('yyyy-MM-dd HH:mm:ss')) 'Last boot time'
    Add-DriftRow 'OS Information' 'Total Memory (GB)' ([math]::Round($os.TotalVisibleMemorySize / 1MB, 1)) 'Total physical RAM'
    Add-DriftRow 'OS Information' 'Free Memory (GB)' ([math]::Round($os.FreePhysicalMemory / 1MB, 1)) 'Available physical RAM'
} catch { Add-DriftRow 'OS Information' 'ERROR' $_.Exception.Message 'Failed to query OS info' }

# ── 2. CPU Information ───────────────────────────────────────────────────
try {
    $cpus = Get-CimInstance Win32_Processor -ErrorAction Stop
    foreach ($cpu in $cpus) {
        Add-DriftRow 'CPU' 'CPU Model' $cpu.Name 'Processor model'
        Add-DriftRow 'CPU' 'Cores' $cpu.NumberOfCores 'Physical cores'
        Add-DriftRow 'CPU' 'Logical Processors' $cpu.NumberOfLogicalProcessors 'Logical processor count'
        Add-DriftRow 'CPU' 'Max Clock (MHz)' $cpu.MaxClockSpeed 'Max clock speed'
        Add-DriftRow 'CPU' 'Current Load (%)' $cpu.LoadPercentage 'Current CPU load'
    }
} catch { Add-DriftRow 'CPU' 'ERROR' $_.Exception.Message 'Failed to query CPU' }

# ── 3. Installed Hotfixes (patches) ─────────────────────────────────────
try {
    $hotfixes = Get-HotFix -ErrorAction Stop | Sort-Object InstalledOn -Descending | Select-Object -First 20
    Add-DriftRow 'Patches' 'Total Installed Patches' $hotfixes.Count 'Number of hotfixes installed'
    $latest = $hotfixes | Select-Object -First 1
    if ($latest) {
        Add-DriftRow 'Patches' 'Latest Patch' $latest.HotFixID "Installed $($latest.InstalledOn.ToString('yyyy-MM-dd') -replace '^$','unknown')"
    }
    foreach ($hf in ($hotfixes | Select-Object -First 10)) {
        Add-DriftRow 'Patches' "Patch_$($hf.HotFixID)" $hf.HotFixID "$($hf.Description) — $($hf.InstalledOn.ToString('yyyy-MM-dd') -replace '^$','unknown')"
    }
} catch { Add-DriftRow 'Patches' 'ERROR' $_.Exception.Message 'Failed to query hotfixes' }

# ── 4. Auto-Start Services ──────────────────────────────────────────────
try {
    $svc = Get-CimInstance Win32_Service -Filter "StartMode='Auto'" -ErrorAction Stop
    $running = ($svc | Where-Object State -eq 'Running').Count
    $stopped = ($svc | Where-Object State -ne 'Running').Count
    Add-DriftRow 'Services' 'Auto-Start Total' $svc.Count 'Services set to auto-start'
    Add-DriftRow 'Services' 'Auto-Start Running' $running 'Currently running'
    Add-DriftRow 'Services' 'Auto-Start Stopped' $stopped 'Expected running but stopped'
    foreach ($s in ($svc | Where-Object State -ne 'Running' | Select-Object -First 10)) {
        Add-DriftRow 'Services' "Stopped_$($s.Name)" $s.State "$($s.DisplayName) — expected running"
    }
} catch { Add-DriftRow 'Services' 'ERROR' $_.Exception.Message 'Failed to query services' }

# ── 5. Firewall Profiles ────────────────────────────────────────────────
try {
    $fw = Get-NetFirewallProfile -ErrorAction Stop
    foreach ($profile in $fw) {
        Add-DriftRow 'Firewall' "$($profile.Name)_Enabled" $profile.Enabled "$($profile.Name) profile"
        Add-DriftRow 'Firewall' "$($profile.Name)_DefaultInboundAction" $profile.DefaultInboundAction "$($profile.Name) default inbound"
        Add-DriftRow 'Firewall' "$($profile.Name)_DefaultOutboundAction" $profile.DefaultOutboundAction "$($profile.Name) default outbound"
    }
} catch { Add-DriftRow 'Firewall' 'ERROR' $_.Exception.Message 'Failed to query firewall profiles' }

# ── 6. Disk Drives ──────────────────────────────────────────────────────
try {
    $vols = Get-CimInstance Win32_LogicalDisk -Filter "DriveType=3" -ErrorAction Stop
    foreach ($v in $vols) {
        $totalGB = [math]::Round($v.Size / 1GB, 1)
        $freeGB  = [math]::Round($v.FreeSpace / 1GB, 1)
        $pctFree = if ($v.Size -gt 0) { [math]::Round(($v.FreeSpace / $v.Size) * 100, 1) } else { 0 }
        Add-DriftRow 'Disk' "$($v.DeviceID)_TotalGB" $totalGB "Drive $($v.DeviceID) total size"
        Add-DriftRow 'Disk' "$($v.DeviceID)_FreeGB" $freeGB "Drive $($v.DeviceID) free space"
        Add-DriftRow 'Disk' "$($v.DeviceID)_PctFree" "$pctFree%" "Drive $($v.DeviceID) percent free"
        Add-DriftRow 'Disk' "$($v.DeviceID)_FileSystem" $v.FileSystem "Drive $($v.DeviceID) file system"
    }
} catch { Add-DriftRow 'Disk' 'ERROR' $_.Exception.Message 'Failed to query disks' }

# ── 7. Network Adapters ─────────────────────────────────────────────────
try {
    $nics = Get-CimInstance Win32_NetworkAdapterConfiguration -Filter "IPEnabled=True" -ErrorAction Stop
    foreach ($nic in $nics) {
        $desc = $nic.Description -replace '\s+', ' '
        Add-DriftRow 'Network' "$desc → IP" ($nic.IPAddress -join ', ') 'IP addresses'
        Add-DriftRow 'Network' "$desc → DNS" ($nic.DNSServerSearchOrder -join ', ') 'DNS servers'
        Add-DriftRow 'Network' "$desc → Gateway" ($nic.DefaultIPGateway -join ', ') 'Default gateway'
        Add-DriftRow 'Network' "$desc → DHCP" $nic.DHCPEnabled 'DHCP enabled'
    }
} catch { Add-DriftRow 'Network' 'ERROR' $_.Exception.Message 'Failed to query network' }

# ── 8. PowerShell & .NET Versions ────────────────────────────────────────
try {
    Add-DriftRow 'Runtime' 'PowerShell Version' $PSVersionTable.PSVersion.ToString() 'PowerShell version'
    Add-DriftRow 'Runtime' 'CLR Version' $PSVersionTable.CLRVersion.ToString() '.NET CLR version'
} catch { }

# ── 9. Windows Defender / AV Status ──────────────────────────────────────
try {
    $def = Get-MpComputerStatus -ErrorAction Stop
    Add-DriftRow 'Security' 'RealTimeProtection' $def.RealTimeProtectionEnabled 'Defender real-time protection'
    Add-DriftRow 'Security' 'AntivirusSignatureAge (days)' $def.AntivirusSignatureAge 'Signature age in days'
    Add-DriftRow 'Security' 'AntivirusEnabled' $def.AntivirusEnabled 'Antivirus enabled'
    Add-DriftRow 'Security' 'LastFullScanAge (days)' $def.FullScanAge 'Days since last full scan'
} catch { Add-DriftRow 'Security' 'Defender' 'Not Available' 'Defender not installed or inaccessible' }

$Result
""";
}
