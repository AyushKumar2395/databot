namespace Infrastructure.Services;

/// <summary>
/// Detects "health / slow / any issues" intent and provides hardcoded
/// read-only diagnostic scripts for Windows_Live and SqlServer_Live.
/// </summary>
internal static class HealthScriptGenerator
{
    private static readonly string[] HealthKeywords =
    [
        "health", "healthy", "all good", "overall", "status", "report",
        "slow", "performance", "lag", "issue", "problem",
        "warning", "critical", "cpu high", "memory high",
        "disk full", "blocked", "blocking", "waits",
        "tempdb", "log full", "failed job", "backup age",
        "reboot pending", "event log", "any issues", "anything wrong",
        "what's wrong", "whats wrong", "diagnose", "check server",
        "server ok", "instance ok", "is it ok", "is everything",
        "how is", "how's", "any errors", "any problems", "any warnings"
    ];

    public static bool IsHealthIntent(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var lower = text.ToLowerInvariant();
        return HealthKeywords.Any(k => lower.Contains(k, StringComparison.Ordinal));
    }

    public static string GetWindowsHealthScript() => _windowsHealthScript;
    public static string GetSqlHealthScript() => _sqlHealthScript;

    // ─── Windows PowerShell Health Script ─────────────────────────────────────

    private const string _windowsHealthScript = """
param([string]$TargetServer)
$Result = @()
if ([string]::IsNullOrWhiteSpace($TargetServer)) { $TargetServer = $env:COMPUTERNAME }

# --- OS / Uptime ---
try {
    $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
    $uptime = (Get-Date) - $os.LastBootUpTime
    $Result += [pscustomobject]@{
        ServerName = $TargetServer; CapturedAt = Get-Date; Status = 'OK'; ErrorMessage = $null
        Category = 'OS'; CheckName = 'OSInfo'
        CheckValue = $os.Caption
        CheckDetail = "Build:$($os.BuildNumber) Uptime:$([int]$uptime.TotalDays)d$($uptime.Hours)h LastBoot:$($os.LastBootUpTime.ToString('yyyy-MM-dd HH:mm'))"
    }
} catch {
    $Result += [pscustomobject]@{ ServerName=$TargetServer; CapturedAt=Get-Date; Status='ERROR'; ErrorMessage=$_.Exception.Message; Category='OS'; CheckName='OSInfo'; CheckValue=$null; CheckDetail=$null }
}

# --- CPU Load ---
try {
    $cpuPct = [int](Get-CimInstance Win32_Processor -ErrorAction Stop | Measure-Object -Property LoadPercentage -Average).Average
    $cpuStatus = if ($cpuPct -ge 90) { 'CRITICAL' } elseif ($cpuPct -ge 75) { 'WARNING' } else { 'OK' }
    $top3 = (Get-Process | Sort-Object CPU -Descending | Select-Object -First 3 | ForEach-Object { "$($_.Name):$([int]($_.CPU))s" }) -join ', '
    $Result += [pscustomobject]@{
        ServerName=$TargetServer; CapturedAt=Get-Date; Status=$cpuStatus; ErrorMessage=$null
        Category='CPU'; CheckName='CpuLoad'; CheckValue="$cpuPct%"; CheckDetail="Top3Proc: $top3"
    }
} catch {
    $Result += [pscustomobject]@{ ServerName=$TargetServer; CapturedAt=Get-Date; Status='ERROR'; ErrorMessage=$_.Exception.Message; Category='CPU'; CheckName='CpuLoad'; CheckValue=$null; CheckDetail=$null }
}

# --- RAM ---
try {
    $mem = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
    $totalGB = [math]::Round($mem.TotalVisibleMemorySize / 1MB, 1)
    $freeGB  = [math]::Round($mem.FreePhysicalMemory / 1MB, 1)
    $usedPct = [int](($mem.TotalVisibleMemorySize - $mem.FreePhysicalMemory) / $mem.TotalVisibleMemorySize * 100)
    $ramStatus = if ($usedPct -ge 95) { 'CRITICAL' } elseif ($usedPct -ge 85) { 'WARNING' } else { 'OK' }
    $Result += [pscustomobject]@{
        ServerName=$TargetServer; CapturedAt=Get-Date; Status=$ramStatus; ErrorMessage=$null
        Category='Memory'; CheckName='RAMUsage'; CheckValue="$usedPct%"; CheckDetail="TotalGB:$totalGB FreeGB:$freeGB"
    }
} catch {
    $Result += [pscustomobject]@{ ServerName=$TargetServer; CapturedAt=Get-Date; Status='ERROR'; ErrorMessage=$_.Exception.Message; Category='Memory'; CheckName='RAMUsage'; CheckValue=$null; CheckDetail=$null }
}

# --- Disk Usage (per drive) ---
try {
    foreach ($d in (Get-CimInstance Win32_LogicalDisk -Filter "DriveType=3" -ErrorAction Stop)) {
        if ($d.Size -gt 0) {
            $usedPct = [int](($d.Size - $d.FreeSpace) / $d.Size * 100)
            $freeGB  = [math]::Round($d.FreeSpace / 1GB, 2)
            $totalGB = [math]::Round($d.Size / 1GB, 2)
            $dStatus = if ($usedPct -ge 95) { 'CRITICAL' } elseif ($usedPct -ge 85) { 'WARNING' } else { 'OK' }
            $Result += [pscustomobject]@{
                ServerName=$TargetServer; CapturedAt=Get-Date; Status=$dStatus; ErrorMessage=$null
                Category='Disk'; CheckName="Disk-$($d.DeviceID)"; CheckValue="$usedPct% used"
                CheckDetail="TotalGB:$totalGB FreeGB:$freeGB Vol:$($d.VolumeName)"
            }
        }
    }
} catch {
    $Result += [pscustomobject]@{ ServerName=$TargetServer; CapturedAt=Get-Date; Status='ERROR'; ErrorMessage=$_.Exception.Message; Category='Disk'; CheckName='DiskUsage'; CheckValue=$null; CheckDetail=$null }
}

# --- EventLog Errors (last 24h) ---
try {
    $since   = (Get-Date).AddHours(-24)
    $sysErr  = @(Get-WinEvent -FilterHashtable @{LogName='System';Level=1,2;StartTime=$since} -ErrorAction SilentlyContinue).Count
    $appErr  = @(Get-WinEvent -FilterHashtable @{LogName='Application';Level=1,2;StartTime=$since} -ErrorAction SilentlyContinue).Count
    $evtSt   = if (($sysErr + $appErr) -ge 20) { 'WARNING' } else { 'OK' }
    $top3    = (Get-WinEvent -FilterHashtable @{LogName='System','Application';Level=1,2;StartTime=$since} -MaxEvents 3 -ErrorAction SilentlyContinue |
                    ForEach-Object { $msg = if ($_.Message) { $_.Message.Substring(0,[math]::Min(60,$_.Message.Length)).Replace("`n",' ') } else { '' }; "$($_.Id):$msg" }) -join ' | '
    $Result += [pscustomobject]@{
        ServerName=$TargetServer; CapturedAt=Get-Date; Status=$evtSt; ErrorMessage=$null
        Category='EventLog'; CheckName='Errors24h'; CheckValue="Sys:$sysErr App:$appErr"; CheckDetail=$top3
    }
} catch {
    $Result += [pscustomobject]@{ ServerName=$TargetServer; CapturedAt=Get-Date; Status='ERROR'; ErrorMessage=$_.Exception.Message; Category='EventLog'; CheckName='Errors24h'; CheckValue=$null; CheckDetail=$null }
}

# --- Reboot Pending ---
try {
    $pending = $false
    if (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending') { $pending = $true }
    if (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired') { $pending = $true }
    $pfn = Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -Name PendingFileRenameOperations -ErrorAction SilentlyContinue
    if ($pfn) { $pending = $true }
    $Result += [pscustomobject]@{
        ServerName=$TargetServer; CapturedAt=Get-Date; Status=if($pending){'WARNING'}else{'OK'}; ErrorMessage=$null
        Category='Reboot'; CheckName='RebootPending'; CheckValue=if($pending){'YES'}else{'NO'}; CheckDetail=$null
    }
} catch {
    $Result += [pscustomobject]@{ ServerName=$TargetServer; CapturedAt=Get-Date; Status='ERROR'; ErrorMessage=$_.Exception.Message; Category='Reboot'; CheckName='RebootPending'; CheckValue=$null; CheckDetail=$null }
}

# --- Stopped Auto-Start Services ---
try {
    $stopped  = @(Get-Service -ErrorAction Stop | Where-Object { $_.StartType -eq 'Automatic' -and $_.Status -eq 'Stopped' })
    $svcSt    = if ($stopped.Count -ge 1) { 'WARNING' } else { 'OK' }
    $svcNames = ($stopped | Select-Object -First 5 | ForEach-Object { $_.Name }) -join ', '
    $Result += [pscustomobject]@{
        ServerName=$TargetServer; CapturedAt=Get-Date; Status=$svcSt; ErrorMessage=$null
        Category='Services'; CheckName='AutoStartStopped'; CheckValue="$($stopped.Count) stopped"; CheckDetail=$svcNames
    }
} catch {
    $Result += [pscustomobject]@{ ServerName=$TargetServer; CapturedAt=Get-Date; Status='ERROR'; ErrorMessage=$_.Exception.Message; Category='Services'; CheckName='AutoStartStopped'; CheckValue=$null; CheckDetail=$null }
}

# --- Network Adapters ---
try {
    foreach ($a in (Get-CimInstance Win32_NetworkAdapterConfiguration -Filter 'IPEnabled=True' -ErrorAction Stop)) {
        $adpName = $a.Description.Substring(0, [math]::Min(30, $a.Description.Length))
        $Result += [pscustomobject]@{
            ServerName=$TargetServer; CapturedAt=Get-Date; Status='OK'; ErrorMessage=$null
            Category='Network'; CheckName="Net-$adpName"
            CheckValue=($a.IPAddress -join ',')
            CheckDetail="GW:$(($a.DefaultIPGateway)-join',') DNS:$(($a.DNSServerSearchOrder)-join',')"
        }
    }
} catch {
    $Result += [pscustomobject]@{ ServerName=$TargetServer; CapturedAt=Get-Date; Status='ERROR'; ErrorMessage=$_.Exception.Message; Category='Network'; CheckName='Network'; CheckValue=$null; CheckDetail=$null }
}

$Result
""";

    // ─── SQL Server T-SQL Health Script ───────────────────────────────────────
    // Uses UNION ALL to produce a single result set with consistent columns:
    // ServerName, CapturedAt, Category, CheckName, CheckValue, CheckDetail, CheckStatus

    private const string _sqlHealthScript = """
DECLARE @now  datetime      = GETDATE();
DECLARE @24h  datetime      = DATEADD(HOUR, -24, @now);
DECLARE @srv  nvarchar(128) = @@SERVERNAME COLLATE DATABASE_DEFAULT;

-- ===== INSTANCE INFO =====
-- COLLATE DATABASE_DEFAULT on SERVERPROPERTY() output avoids conflicts with master collation.
SELECT
    @srv AS [ServerName],
    @now AS [CapturedAt],
    N'Instance'    AS [Category],
    N'InstanceInfo' AS [CheckName],
    CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(50)) COLLATE DATABASE_DEFAULT AS [CheckValue],
    (CAST(SERVERPROPERTY('Edition') AS nvarchar(100)) COLLATE DATABASE_DEFAULT
        + N' | Uptime: ' + CAST(DATEDIFF(HOUR, sqlserver_start_time, @now) AS nvarchar(10)) + N'h'
        + N' | Start: ' + CONVERT(nvarchar(20), sqlserver_start_time, 120)) AS [CheckDetail],
    N'OK' AS [CheckStatus]
FROM sys.dm_os_sys_info

UNION ALL

-- ===== CPU RUNNABLE QUEUE =====
SELECT @srv, @now,
    N'CPU', N'RunnableTaskQueue',
    CAST(SUM(runnable_tasks_count) AS nvarchar(20)) + N' runnable tasks',
    N'Active workers: ' + CAST(SUM(active_workers_count) AS nvarchar(10))
        + N' | Queued: ' + CAST(SUM(work_queue_count) AS nvarchar(10)),
    CASE WHEN SUM(runnable_tasks_count) > 10 THEN N'WARNING' ELSE N'OK' END
FROM sys.dm_os_schedulers
WHERE status = N'VISIBLE ONLINE'

UNION ALL

-- ===== MEMORY PRESSURE =====
-- Uses sys.dm_os_performance_counters (valid on all SQL Server versions)
SELECT @srv, @now,
    N'Memory', N'MemoryPressure',
    MAX(CASE WHEN counter_name = N'Total Server Memory (KB)' THEN CAST(cntr_value/1024 AS nvarchar(20))+N' MB' END),
    N'Target: '
        + MAX(CASE WHEN counter_name = N'Target Server Memory (KB)' THEN CAST(cntr_value/1024 AS nvarchar(20)) END)
        + N' MB | MemGrantsWaiting: '
        + CAST((SELECT COUNT(*) FROM sys.dm_exec_query_memory_grants WHERE wait_time_ms > 0) AS nvarchar(10)),
    CASE WHEN MAX(CASE WHEN counter_name = N'Total Server Memory (KB)'  THEN cntr_value END) * 1.0
              / NULLIF(MAX(CASE WHEN counter_name = N'Target Server Memory (KB)' THEN cntr_value END), 0) > 0.95
         THEN N'WARNING' ELSE N'OK' END
FROM sys.dm_os_performance_counters
WHERE counter_name IN (N'Total Server Memory (KB)', N'Target Server Memory (KB)')
  AND instance_name = N''

UNION ALL

-- ===== BLOCKING (head blockers, top 5 — wrapped for ORDER BY inside UNION ALL) =====
SELECT b.[ServerName], b.[CapturedAt], b.[Category], b.[CheckName], b.[CheckValue], b.[CheckDetail], b.[CheckStatus]
FROM (
    SELECT TOP 5
        @srv AS [ServerName], @now AS [CapturedAt],
        N'Blocking' AS [Category], N'BlockingSession' AS [CheckName],
        N'SPID ' + CAST(r.blocking_session_id AS nvarchar(10))
            + N' blocks ' + CAST(COUNT(*) AS nvarchar(5)) + N' session(s)' AS [CheckValue],
        (N'WaitType: ' + ISNULL(MAX(r.wait_type) COLLATE DATABASE_DEFAULT, N'')
            + N' | WaitSec: ' + CAST(MAX(r.wait_time) / 1000 AS nvarchar(10))) AS [CheckDetail],
        N'WARNING' AS [CheckStatus]
    FROM sys.dm_exec_requests r
    WHERE r.blocking_session_id > 0
    GROUP BY r.blocking_session_id
    ORDER BY COUNT(*) DESC
) b

UNION ALL

-- ===== TOP WAIT STATS (wrapped for ORDER BY inside UNION ALL) =====
SELECT w.[ServerName], w.[CapturedAt], w.[Category], w.[CheckName], w.[CheckValue], w.[CheckDetail], w.[CheckStatus]
FROM (
    SELECT TOP 5
        @srv AS [ServerName], @now AS [CapturedAt],
        N'WaitStats' AS [Category], N'TopWait' AS [CheckName],
        wait_type COLLATE DATABASE_DEFAULT AS [CheckValue],
        N'TotalWaitSec: ' + CAST(wait_time_ms / 1000 AS nvarchar(15))
            + N' | Tasks: ' + CAST(waiting_tasks_count AS nvarchar(10)) AS [CheckDetail],
        CASE WHEN wait_time_ms / 1000 > 3600 THEN N'WARNING' ELSE N'OK' END AS [CheckStatus]
    FROM sys.dm_os_wait_stats
    WHERE wait_type NOT IN (
        N'SLEEP_TASK', N'BROKER_TO_FLUSH', N'BROKER_TASK_STOP', N'CLR_AUTO_EVENT',
        N'DISPATCHER_QUEUE_SEMAPHORE', N'FT_IFTS_SCHEDULER_IDLE_WAIT',
        N'HADR_WORK_QUEUE', N'HADR_FILESTREAM_IOMGR_IOCOMPLETION',
        N'HADR_TIMER_TASK', N'LAZYWRITER_SLEEP', N'LOGMGR_QUEUE',
        N'ONDEMAND_TASK_QUEUE', N'REQUEST_FOR_DEADLOCK_SEARCH', N'RESOURCE_QUEUE',
        N'SERVER_IDLE_CHECK', N'SLEEP_DBSTARTUP', N'SLEEP_DBRECOVER',
        N'SLEEP_MASTERDBREADY', N'SLEEP_MASTERMDREADY', N'SLEEP_MASTERUPGRADED',
        N'SLEEP_MSDBSTARTUP', N'SLEEP_SYSTEMTASK', N'SLEEP_TEMPDBSTARTUP',
        N'SNI_HTTP_ACCEPT', N'SP_SERVER_DIAGNOSTICS_SLEEP', N'SQLTRACE_BUFFER_FLUSH',
        N'SQLTRACE_INCREMENTAL_FLUSH_SLEEP', N'WAITFOR', N'XE_DISPATCHER_WAIT',
        N'XE_TIMER_EVENT', N'BROKER_EVENTHANDLER', N'CHECKPOINT_QUEUE',
        N'DBMIRROR_EVENTS_QUEUE', N'SQLTRACE_WAIT_ENTRIES',
        N'WAIT_XTP_OFFLINE_CKPT_NEW_LOG', N'WAIT_XTP_CKPT_CLOSE')
    ORDER BY wait_time_ms DESC
) w

UNION ALL

-- ===== TEMPDB SPACE (via perf counters — works from any DB context) =====
SELECT TOP 1
    @srv, @now,
    N'TempDB', N'TempDBFree',
    CAST(cntr_value / 1024 AS nvarchar(20)) + N' MB free',
    N'Source: dm_os_performance_counters Free Space in tempdb (KB)',
    CASE WHEN cntr_value / 1024 < 500 THEN N'WARNING' ELSE N'OK' END
FROM sys.dm_os_performance_counters
WHERE counter_name = N'Free Space in tempdb (KB)'

UNION ALL

-- ===== LOG REUSE WAIT per database =====
-- COLLATE DATABASE_DEFAULT on sys.databases columns (server collation) avoids conflict.
SELECT
    @srv, @now,
    N'LogUsage', N'LogReuseWait',
    d.name COLLATE DATABASE_DEFAULT,
    (N'LogReuseWait: ' + d.log_reuse_wait_desc COLLATE DATABASE_DEFAULT
        + N' | LogSizeMB: ' + CAST(CAST(SUM(f.size) * 8.0 / 1024 AS decimal(10,1)) AS nvarchar(20))),
    CASE WHEN d.log_reuse_wait_desc NOT IN (N'NOTHING', N'LOG_BACKUP')
         THEN N'WARNING' ELSE N'OK' END
FROM sys.databases d
JOIN sys.master_files f ON f.database_id = d.database_id AND f.type = 1
WHERE d.log_reuse_wait_desc <> N'NOTHING'
GROUP BY d.name, d.log_reuse_wait_desc

UNION ALL

-- ===== FAILED AGENT JOBS (last 24h — wrapped for ORDER BY) =====
-- COLLATE DATABASE_DEFAULT on msdb columns (SQL_Latin1_General_CP1_CI_AS) avoids conflict.
SELECT aj.[ServerName], aj.[CapturedAt], aj.[Category], aj.[CheckName], aj.[CheckValue], aj.[CheckDetail], aj.[CheckStatus]
FROM (
    SELECT TOP 10
        @srv AS [ServerName], @now AS [CapturedAt],
        N'AgentJobs' AS [Category], N'FailedJob' AS [CheckName],
        j.name COLLATE DATABASE_DEFAULT AS [CheckValue],
        (N'Step: ' + h.step_name COLLATE DATABASE_DEFAULT
            + N' | ' + LEFT(h.message COLLATE DATABASE_DEFAULT, 150)) AS [CheckDetail],
        N'WARNING' AS [CheckStatus]
    FROM msdb.dbo.sysjobhistory h
    JOIN msdb.dbo.sysjobs j ON h.job_id = j.job_id
    WHERE h.run_status = 0
      AND h.step_id > 0
      AND msdb.dbo.agent_datetime(h.run_date, h.run_time) >= @24h
    ORDER BY msdb.dbo.agent_datetime(h.run_date, h.run_time) DESC
) aj

UNION ALL

-- ===== BACKUP FRESHNESS (user databases — wrapped for ORDER BY) =====
SELECT bf.[ServerName], bf.[CapturedAt], bf.[Category], bf.[CheckName], bf.[CheckValue], bf.[CheckDetail], bf.[CheckStatus]
FROM (
    SELECT TOP 20
        @srv AS [ServerName], @now AS [CapturedAt],
        N'Backup' AS [Category], N'BackupAge' AS [CheckName],
        d.name COLLATE DATABASE_DEFAULT AS [CheckValue],
        N'LastFull: ' + ISNULL(CONVERT(nvarchar(20), MAX(bs.backup_finish_date), 120), N'NEVER')
            + N' | AgeHours: '
            + CAST(ISNULL(DATEDIFF(HOUR, MAX(bs.backup_finish_date), @now), 9999) AS nvarchar(10)) AS [CheckDetail],
        CASE WHEN MAX(bs.backup_finish_date) IS NULL                                   THEN N'CRITICAL'
             WHEN DATEDIFF(HOUR, MAX(bs.backup_finish_date), @now) > 48 THEN N'WARNING'
             ELSE N'OK' END AS [CheckStatus]
    FROM sys.databases d
    LEFT JOIN msdb.dbo.backupset bs
        ON bs.database_name = d.name AND bs.type = N'D'
    WHERE d.database_id > 4
    GROUP BY d.name
    ORDER BY MAX(bs.backup_finish_date) ASC
) bf;
""";
}
