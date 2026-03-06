using System.Text.Json;
using System.Text.RegularExpressions;

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
        "top cpu", "log", "size", "disk space"
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

        if (IsDangerousRequest(question, environmentTag))
            return $"{DangerousRequestMessage}||{queryCodeEcho}";

        // Phase 2: Tuning (grammar + normalization for template/LLM selection).
        var tuned = TuneQuestion(question, environmentTag);
        return $"{tuned}||{queryCodeEcho}";
    }

    public static string BuildGenerateScript(string environmentTag, string tunedQuestion)
    {
        if (EnvironmentRules.IsSqlServer(environmentTag))
        {
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

        return $"General answer: {Normalize(question)}";
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
}
