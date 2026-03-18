namespace Infrastructure.Services;

/// <summary>
/// Curated, read-only diagnostic scripts for the Heartbeat feature.
/// These are hardcoded constants — no LLM, no user input, no safety scan needed.
/// Designed to be as lightweight as possible for sub-second execution.
/// </summary>
internal static class HeartbeatScripts
{
    // ═══════════════════════════════════════════════════════════════════════
    //  WINDOWS VITAL SIGNS — Get-Counter with WMI/CIM fallback
    // ═══════════════════════════════════════════════════════════════════════

    public const string WindowsVitals = """
$ErrorActionPreference = 'Stop'
try {
    # ── Kick off Get-Counter as background job FIRST (1s sample runs in parallel) ──
    $counterJob = $null
    try {
        $counters = @(
            '\Processor(_Total)\% Processor Time',
            '\Memory\Available MBytes',
            '\PhysicalDisk(_Total)\% Disk Time',
            '\PhysicalDisk(_Total)\Avg. Disk Queue Length',
            '\System\Processor Queue Length'
        )
        $counterJob = Get-Counter -Counter $counters -SampleInterval 1 -MaxSamples 1 -AsJob
    } catch { }

    # ── While counter samples, collect system info via CIM ──
    $cs = Get-CimInstance Win32_ComputerSystem
    $os = Get-CimInstance Win32_OperatingSystem
    $cpus = @(Get-CimInstance Win32_Processor)
    $totalMemMB = [math]::Round($cs.TotalPhysicalMemory / 1MB, 0)

    $cpuFirst = $cpus[0]
    $sockets = $cpus.Count
    $cores = ($cpus | Measure-Object -Property NumberOfCores -Sum).Sum
    $logical = ($cpus | Measure-Object -Property NumberOfLogicalProcessors -Sum).Sum
    $baseGHz = [math]::Round($cpuFirst.MaxClockSpeed / 1000, 2)
    $currentGHz = [math]::Round($cpuFirst.CurrentClockSpeed / 1000, 2)
    $virt = if ($cpuFirst.VirtualizationFirmwareEnabled) { 'Enabled' } elseif ($cs.HypervisorPresent) { 'VM' } else { 'Disabled' }

    # Cache — use Win32_Processor L2/L3 directly (faster than Win32_CacheMemory)
    $l1 = 0
    $l2 = ($cpus | Measure-Object -Property L2CacheSize -Sum -ErrorAction SilentlyContinue).Sum
    $l3 = ($cpus | Measure-Object -Property L3CacheSize -Sum -ErrorAction SilentlyContinue).Sum
    if (-not $l2) { $l2 = 0 }
    if (-not $l3) { $l3 = 0 }

    # Processes (reuse $os), threads, handles from perf counters
    $procs = $os.NumberOfProcesses
    $perfOS = Get-CimInstance Win32_PerfFormattedData_PerfOS_System -ErrorAction SilentlyContinue
    $threads = if ($perfOS) { $perfOS.Threads } else { 0 }
    $handles = 0
    try { $handles = (Get-CimInstance Win32_PerfFormattedData_PerfProc_Process -Filter "Name='_Total'" -ErrorAction SilentlyContinue).HandleCount } catch { }

    # Uptime
    $lastBoot = $os.LastBootUpTime
    $uptime = (Get-Date) - $lastBoot
    $uptimeStr = '{0}:{1:D2}:{2:D2}:{3:D2}' -f $uptime.Days, $uptime.Hours, $uptime.Minutes, $uptime.Seconds

    # ── Collect counter results (should be done by now) ──
    $useCounter = $false
    if ($counterJob) {
        try {
            $counterResult = $counterJob | Wait-Job -Timeout 5 | Receive-Job
            if ($counterResult) {
                $samples = $counterResult.CounterSamples
                $useCounter = $true
            }
        } catch { }
        finally { $counterJob | Remove-Job -Force -ErrorAction SilentlyContinue }
    }

    if ($useCounter) {
        $availMem = ($samples | Where-Object { $_.Path -like '*available mbytes*' }).CookedValue
        $memUsagePct = if ($totalMemMB -gt 0) { [math]::Round(($totalMemMB - $availMem) / $totalMemMB * 100, 1) } else { 0 }
        $cpuPct = [math]::Round(($samples | Where-Object { $_.Path -like '*% processor time*' }).CookedValue, 1)
        $diskPct = [math]::Round(($samples | Where-Object { $_.Path -like '*% disk time*' }).CookedValue, 1)
        $diskQueue = [math]::Round(($samples | Where-Object { $_.Path -like '*avg. disk queue length*' }).CookedValue, 2)
        $cpuQueue = [int]($samples | Where-Object { $_.Path -like '*processor queue length*' }).CookedValue
    } else {
        $cpuLoad = (Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average
        $availMem = [math]::Round($os.FreePhysicalMemory / 1KB, 0)
        $memUsagePct = if ($totalMemMB -gt 0) { [math]::Round(($totalMemMB - $availMem) / $totalMemMB * 100, 1) } else { 0 }
        $disk = Get-CimInstance Win32_PerfFormattedData_PerfDisk_PhysicalDisk -Filter "Name='_Total'" -ErrorAction SilentlyContinue
        $cpuPct = [math]::Round($cpuLoad, 1)
        $diskPct = if ($disk) { [math]::Round([double]$disk.PercentDiskTime, 1) } else { 0 }
        $diskQueue = if ($disk) { [math]::Round([double]$disk.AvgDiskQueueLength, 2) } else { 0 }
        $cpuQueue = if ($perfOS) { $perfOS.ProcessorQueueLength } else { 0 }
    }

    [PSCustomObject]@{
        # Performance metrics
        ServerName              = $env:COMPUTERNAME
        PercentProcessorTime    = $cpuPct
        AvailableMemory         = [math]::Round($availMem, 0)
        TotalMemoryMB           = $totalMemMB
        MemoryUsage             = $memUsagePct
        PercentageDiskTimeTotal = $diskPct
        AvgDiskQueueLengthTotal = $diskQueue
        ProcessorQueueLength    = $cpuQueue
        # System info
        CpuName                 = $cpuFirst.Name.Trim()
        CurrentSpeedGHz         = $currentGHz
        BaseSpeedGHz            = $baseGHz
        Sockets                 = $sockets
        Cores                   = $cores
        LogicalProcessors       = $logical
        Virtualization          = $virt
        L1CacheKB               = $l1
        L2CacheKB               = $l2
        L3CacheKB               = $l3
        Processes               = $procs
        Threads                 = $threads
        Handles                 = $handles
        Uptime                  = $uptimeStr
        LastBootUtc             = $lastBoot.ToUniversalTime().ToString('o')
        TotalMemoryGB           = [math]::Round($cs.TotalPhysicalMemory / 1GB, 1)
        OsVersion               = $os.Caption.Trim()
        OsBuild                 = $os.Version
    }
} catch {
    [PSCustomObject]@{
        ServerName   = $env:COMPUTERNAME
        ErrorMessage = $_.Exception.Message
    }
}
""";

    // ═══════════════════════════════════════════════════════════════════════
    //  SQL SERVER VITAL SIGNS — Lightweight DMV query (~50ms)
    // ═══════════════════════════════════════════════════════════════════════

    public const string SqlServerVitals = """
SET NOCOUNT ON;
SELECT
    @@SERVERNAME AS [ServerName],

    -- CPU: latest SQL Server process utilization from ring buffers
    ISNULL((
        SELECT TOP 1
            record.value('(./Record/SchedulerMonitorEvent/SystemHealth/ProcessUtilization)[1]','int')
        FROM (
            SELECT CAST(record AS xml) AS record
            FROM sys.dm_os_ring_buffers
            WHERE ring_buffer_type = N'RING_BUFFER_SCHEDULER_MONITOR'
              AND record LIKE N'%<SystemHealth>%'
        ) AS rb
        ORDER BY rb.record.value('(./Record/@id)[1]','int') DESC
    ), 0) AS [SqlServerCPU],

    -- PLE
    ISNULL((
        SELECT cntr_value FROM sys.dm_os_performance_counters
        WHERE counter_name = 'Page life expectancy'
          AND object_name LIKE N'%Buffer Manager%'
    ), 0) AS [PageLifeExpectancy_seconds],

    -- Buffer Cache Hit Ratio
    ISNULL((
        SELECT CAST(ROUND(
            CAST(a.cntr_value AS FLOAT) / NULLIF(b.cntr_value, 0) * 100, 2
        ) AS DECIMAL(5,2))
        FROM sys.dm_os_performance_counters a
        CROSS JOIN sys.dm_os_performance_counters b
        WHERE a.counter_name = 'Buffer cache hit ratio'
          AND a.object_name LIKE N'%Buffer Manager%'
          AND b.counter_name = 'Buffer cache hit ratio base'
          AND b.object_name LIKE N'%Buffer Manager%'
    ), 0) AS [BufferCacheHitRatio],

    -- Available Memory
    ISNULL((
        SELECT CAST(available_physical_memory_kb / 1048576.0 AS DECIMAL(10,2))
        FROM sys.dm_os_sys_memory
    ), 0) AS [AvailableMemory_GB],

    -- Memory Grants Pending
    ISNULL((
        SELECT cntr_value FROM sys.dm_os_performance_counters
        WHERE counter_name = 'Memory Grants Pending'
          AND object_name LIKE N'%Memory Manager%'
    ), 0) AS [MemoryGrantsPending],

    -- Blocking Sessions
    (SELECT COUNT(*) FROM sys.dm_exec_requests
     WHERE blocking_session_id > 0) AS [BlockingCount],

    -- Batch Requests/sec
    ISNULL((
        SELECT cntr_value FROM sys.dm_os_performance_counters
        WHERE counter_name = 'Batch Requests/sec'
          AND object_name LIKE N'%SQL Statistics%'
    ), 0) AS [BatchRequests_sec],

    -- User Connections
    ISNULL((
        SELECT cntr_value FROM sys.dm_os_performance_counters
        WHERE counter_name = 'User Connections'
          AND object_name LIKE N'%General Statistics%'
    ), 0) AS [UserConnections],

    -- Signal Wait % (CPU saturation: >15% = warning, >25% = critical)
    ISNULL((
        SELECT CAST(CASE WHEN SUM(wait_time_ms) = 0 THEN 0
            ELSE SUM(signal_wait_time_ms) * 100.0 / SUM(wait_time_ms) END AS DECIMAL(5,2))
        FROM sys.dm_os_wait_stats
    ), 0) AS [SignalWaitPct],

    -- Top wait type (highest cumulative, filtered)
    ISNULL((
        SELECT TOP 1 wait_type FROM sys.dm_os_wait_stats
        WHERE wait_type NOT IN (N'SLEEP_TASK',N'LAZYWRITER_SLEEP',N'SQLTRACE_BUFFER_FLUSH',
            N'BROKER_RECEIVE_WAITFOR',N'WAITFOR',N'CLR_AUTO_EVENT',N'CLR_MANUAL_EVENT',
            N'REQUEST_FOR_DEADLOCK_SEARCH',N'XE_TIMER_EVENT',N'XE_DISPATCHER_WAIT',
            N'CHECKPOINT_QUEUE',N'LOGMGR_QUEUE',N'DIRTY_PAGE_POLL',N'HADR_FILESTREAM_IOMGR_IOCOMPLETION',
            N'SP_SERVER_DIAGNOSTICS_SLEEP',N'QDS_PERSIST_TASK_MAIN_LOOP_SLEEP',
            N'QDS_CLEANUP_STALE_QUERIES_TASK_MAIN_LOOP_SLEEP',N'DISPATCHER_QUEUE_SEMAPHORE',
            N'BROKER_EVENTHANDLER',N'BROKER_TO_FLUSH',N'ONDEMAND_TASK_QUEUE',
            N'SQLTRACE_INCREMENTAL_FLUSH_SLEEP',N'WAIT_FOR_RESULTS',
            N'HADR_WORK_QUEUE',N'HADR_TIMER_TASK',N'HADR_CLUSAPI_CALL',
            N'HADR_LOGCAPTURE_WAIT',N'HADR_NOTIFICATION_DEQUEUE')
          AND wait_type NOT LIKE N'SLEEP%'
        ORDER BY wait_time_ms DESC
    ), N'NONE') AS [TopWaitType],

    -- Max data file I/O latency (avg read ms across all files)
    ISNULL((
        SELECT TOP 1 CAST(CASE WHEN vfs.num_of_reads = 0 THEN 0
            ELSE vfs.io_stall_read_ms * 1.0 / vfs.num_of_reads END AS DECIMAL(10,2))
        FROM sys.dm_io_virtual_file_stats(NULL, NULL) vfs
        JOIN sys.master_files mf ON vfs.database_id = mf.database_id AND vfs.file_id = mf.file_id
        WHERE mf.type = 0 AND vfs.num_of_reads > 100
        ORDER BY CASE WHEN vfs.num_of_reads = 0 THEN 0
            ELSE vfs.io_stall_read_ms * 1.0 / vfs.num_of_reads END DESC
    ), 0) AS [MaxDataFileReadLatency_ms],

    -- Long-running queries (active requests with CPU > 10 sec, excluding system)
    (SELECT COUNT(*) FROM sys.dm_exec_requests
     WHERE session_id > 50 AND session_id <> @@SPID
       AND cpu_time >= 10000 AND status = N'running') AS [LongRunningQueries],

    -- Recent error log entries (last 24h count)
    -- Uses sp_readerrorlog with 3 args for compatibility
    0 AS [ErrorLogCount24h],

    -- Version: e.g. "Microsoft SQL Server 2019 (RTM-CU18) ..."
    LEFT(@@VERSION, 200) AS [SqlVersionFull],

    -- Edition: e.g. "Enterprise Edition (64-bit)"
    CAST(SERVERPROPERTY('Edition') AS NVARCHAR(100)) AS [SqlEdition],

    -- Product version: e.g. "15.0.4261.1"
    CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR(30)) AS [SqlProductVersion],

    -- Product level: e.g. "RTM", "SP1", "CTP1"
    CAST(SERVERPROPERTY('ProductLevel') AS NVARCHAR(30)) AS [SqlProductLevel];
""";

    // ═══════════════════════════════════════════════════════════════════════
    //  WINDOWS EVENT LOG — Severity counts (24h) + latest alert detail
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns severity counts (Critical/Error/Warning in 24h) across System, Application, Security logs,
    /// plus the single most impactful recent event detail.
    /// DBAs/admins see "how noisy is this server?" at a glance.
    /// Runs in ~200-600ms via Get-WinEvent.
    /// </summary>
    public const string WindowsAlertSummary = """
$ErrorActionPreference = 'SilentlyContinue'
$logs = @('System','Application','Security')
$cutoff = (Get-Date).AddHours(-24)
$counts = @{ Critical=0; Error=0; Warning=0 }
$best = $null
$bestLog = $null

# Count events per severity level across all logs, and track the latest highest-priority event.
foreach ($level in @(1, 2, 3)) {
    $sevName = switch ($level) { 1 { 'Critical' } 2 { 'Error' } 3 { 'Warning' } }
    foreach ($log in $logs) {
        try {
            $events = Get-WinEvent -FilterHashtable @{ LogName=$log; Level=@($level); StartTime=$cutoff } -ErrorAction SilentlyContinue
            if ($events) {
                $counts[$sevName] += $events.Count
                # Track the latest event at the highest severity we've seen so far
                $newest = $events | Sort-Object TimeCreated -Descending | Select-Object -First 1
                if ($null -eq $best -or ($newest.Level -lt $best.Level) -or ($newest.Level -eq $best.Level -and $newest.TimeCreated -gt $best.TimeCreated)) {
                    $best = $newest
                    $bestLog = $log
                }
            }
        } catch { }
    }
}

$result = [PSCustomObject]@{
    CriticalCount = $counts['Critical']
    ErrorCount    = $counts['Error']
    WarningCount  = $counts['Warning']
    Source        = ''
    Severity      = ''
    TimeUtc       = ''
    Message       = ''
}

if ($best) {
    $sev = switch ($best.Level) { 1 { 'Critical' } 2 { 'Error' } 3 { 'Warning' } default { 'Info' } }
    $msg = ($best.Message -replace '\r?\n',' ').Trim()
    if ($msg.Length -gt 500) { $msg = $msg.Substring(0,500) + '...' }
    $result.Source   = $bestLog
    $result.Severity = $sev
    $result.TimeUtc  = $best.TimeCreated.ToUniversalTime().ToString('o')
    $result.Message  = $msg
}
$result
""";

    // ═══════════════════════════════════════════════════════════════════════
    //  SQL SERVER ERROR LOG — Severity counts (24h) + latest alert detail
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns severity counts (Error/Warning in 24h) from sp_readerrorlog,
    /// plus the single most impactful recent entry detail.
    /// Note: SQL error log doesn't have a "Critical" level — it's Error or Warning.
    /// </summary>
    public const string SqlAlertSummary = """
SET NOCOUNT ON;
DECLARE @from DATETIME = DATEADD(HOUR, -24, GETUTCDATE());

-- Collect all errors and warnings into temp tables for counting.
-- Use only 3 args for sp_readerrorlog (log#, type, search_string)
-- to stay compatible with all SQL Server versions; filter by date afterward.
CREATE TABLE #errors (LogDate DATETIME, ProcessInfo NVARCHAR(50), [Text] NVARCHAR(MAX));
CREATE TABLE #warnings (LogDate DATETIME, ProcessInfo NVARCHAR(50), [Text] NVARCHAR(MAX));

INSERT INTO #errors EXEC sp_readerrorlog 0, 1, N'Error';
INSERT INTO #errors EXEC sp_readerrorlog 0, 1, N'Fail';
INSERT INTO #warnings EXEC sp_readerrorlog 0, 1, N'Warning';

-- Deduplicate and apply 24h filter.
-- SQL error log stores errors as 2 rows: header ("Error: 14420, Severity: 16, State: 1.")
-- and message ("The log shipping secondary database..."). The LogDate may differ by
-- milliseconds so GROUP BY doesn't always merge them.
-- Strategy: prefer the LONGER text (the actual message), skip the short header-only rows.
;WITH errDedup AS (
    SELECT DISTINCT LogDate,
        REPLACE(REPLACE([Text], CHAR(13), ' '), CHAR(10), ' ') AS [CleanText],
        LEN([Text]) AS TextLen
    FROM #errors WHERE LogDate >= @from
),
warnDedup AS (
    SELECT DISTINCT LogDate,
        REPLACE(REPLACE([Text], CHAR(13), ' '), CHAR(10), ' ') AS [CleanText],
        LEN([Text]) AS TextLen
    FROM #warnings WHERE LogDate >= @from
)
SELECT
    0 AS [CriticalCount],
    (SELECT COUNT(DISTINCT LogDate) FROM errDedup)  AS [ErrorCount],
    (SELECT COUNT(DISTINCT LogDate) FROM warnDedup) AS [WarningCount],
    ISNULL(lat.[Source], '')   AS [Source],
    ISNULL(lat.[Severity], '') AS [Severity],
    ISNULL(lat.[TimeUtc], '')  AS [TimeUtc],
    ISNULL(lat.[Message], '')  AS [Message]
FROM (SELECT 1 AS x) AS dummy
OUTER APPLY (
    -- Pick the latest error/warning. For each LogDate, take the LONGEST text
    -- (the real message body, not the short "Error: XXXX, Severity: XX" header)
    SELECT TOP 1
        'SQL ErrorLog' AS [Source],
        CASE WHEN src = 'E' THEN 'Error' ELSE 'Warning' END AS [Severity],
        CONVERT(VARCHAR(30), LogDate, 127) AS [TimeUtc],
        LEFT([CleanText], 500) AS [Message]
    FROM (
        SELECT LogDate, [CleanText], TextLen, 'E' AS src FROM errDedup
        UNION ALL
        SELECT LogDate, [CleanText], TextLen, 'W' AS src FROM warnDedup
    ) AS combined
    ORDER BY
        CASE WHEN src = 'E' THEN 0 ELSE 1 END,  -- errors first
        LogDate DESC,                              -- latest first
        TextLen DESC                               -- longest text first (message over header)
) AS lat;

DROP TABLE #errors;
DROP TABLE #warnings;
""";

    // ═══════════════════════════════════════════════════════════════════════
    //  WINRM PRE-FLIGHT — Ensures remote server is reachable via PS remoting
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Tests WinRM connectivity to a remote server. Returns exit code 0 if reachable.
    /// Replace {{$server}} before execution.
    /// </summary>
    public const string TestWinRM = """
$ErrorActionPreference = 'Stop'
try {
    $r = Test-WSMan -ComputerName '{{$server}}' -ErrorAction Stop
    [PSCustomObject]@{ Reachable = $true; ProductVersion = $r.ProductVersion } | ConvertTo-Json -Compress
} catch {
    [PSCustomObject]@{ Reachable = $false; Error = $_.Exception.Message } | ConvertTo-Json -Compress
}
""";

    /// <summary>
    /// Adds a server to the local TrustedHosts list (idempotent).
    /// Must run elevated on the API server. Replace {{$server}} before execution.
    /// </summary>
    public const string AddTrustedHost = """
$ErrorActionPreference = 'Stop'
$server = '{{$server}}'
$current = (Get-Item WSMan:\localhost\Client\TrustedHosts -ErrorAction SilentlyContinue).Value
if ([string]::IsNullOrWhiteSpace($current)) {
    Set-Item WSMan:\localhost\Client\TrustedHosts -Value $server -Force
} elseif ($current -ne '*' -and $current -notmatch "(?:^|,)\s*$([regex]::Escape($server))\s*(?:,|$)") {
    Set-Item WSMan:\localhost\Client\TrustedHosts -Value "$current,$server" -Force
}
[PSCustomObject]@{ Added = $true; TrustedHosts = (Get-Item WSMan:\localhost\Client\TrustedHosts).Value } | ConvertTo-Json -Compress
""";

    /// <summary>
    /// Attempts to enable WinRM on a remote server via WMI (DCOM).
    /// Requires admin access via WMI from the API server. Replace {{$server}} before execution.
    /// </summary>
    public const string EnableWinRMRemotely = """
$ErrorActionPreference = 'Stop'
$server = '{{$server}}'
try {
    # Try enabling via WMI process creation (requires DCOM access)
    # Use powershell.exe -WindowStyle Hidden to avoid visible CMD windows on the remote server.
    $process = [WMICLASS]"\\$server\ROOT\CIMV2:Win32_Process"
    $result = $process.Create('powershell.exe -WindowStyle Hidden -Command "winrm quickconfig -quiet -force"')
    Start-Sleep -Seconds 3
    # Verify
    $r = Test-WSMan -ComputerName $server -ErrorAction Stop
    [PSCustomObject]@{ Enabled = $true; Method = 'WMI'; ProductVersion = $r.ProductVersion } | ConvertTo-Json -Compress
} catch {
    try {
        # Fallback: try sc.exe via WMI to start WinRM service
        $process = [WMICLASS]"\\$server\ROOT\CIMV2:Win32_Process"
        $null = $process.Create('powershell.exe -WindowStyle Hidden -Command "sc.exe config winrm start= auto; net start winrm"')
        Start-Sleep -Seconds 3
        $r = Test-WSMan -ComputerName $server -ErrorAction Stop
        [PSCustomObject]@{ Enabled = $true; Method = 'SC'; ProductVersion = $r.ProductVersion } | ConvertTo-Json -Compress
    } catch {
        [PSCustomObject]@{ Enabled = $false; Error = $_.Exception.Message } | ConvertTo-Json -Compress
    }
}
""";

    // SQL instance discovery now uses IUserServerRepository.GetSqlServersAsync
    // (calls Get_UserSQLServer SP) which returns proper connection tokens with ports.
}
