-- ============================================================================
-- Upsert Root-Cause Diagnostic sample questions
-- Run against: SQLGig database on CTS03
-- GroupTitle: 'SQL Server Health' / 'Windows Health'
-- GroupKey:   'Diagnostic'
-- ============================================================================

-- ── SQL Server Live: Root Cause Diagnostic ────────────────────────────────
DELETE FROM [DataBOT].[QuestionSamples]
WHERE [Environment] = 'SqlServer_Live' AND [GroupKey] = 'Diagnostic';

INSERT INTO [DataBOT].[QuestionSamples]
    ([Environment], [GroupKey], [GroupTitle], [GroupOrder], [QuestionText], [QuestionOrder], [Tags], [Script], [IsActive])
VALUES
(
    'SqlServer_Live',
    'Diagnostic',
    'SQL Server Health',
    6,
    'Why is this SQL Server struggling right now?',
    1,
    'Diagnostic,RootCause,Health,CPU,Blocking,Waits,IO,Memory,Agent',
    N'SET NOCOUNT ON;

DECLARE @CapturedAtUtc datetime2(3) = SYSUTCDATETIME();

IF OBJECT_ID(''tempdb..#R'') IS NOT NULL DROP TABLE #R;
CREATE TABLE #R
(
    ServerName    nvarchar(256)  NOT NULL,
    CapturedAtUtc datetime2(3)   NOT NULL,
    Category      nvarchar(100)  NOT NULL,
    MetricName    nvarchar(256)  NOT NULL,
    MetricValue   nvarchar(4000) NULL,
    SeverityHint  nvarchar(50)   NULL,
    Detail        nvarchar(4000) NULL
);

DECLARE @ServerName nvarchar(256) = @@SERVERNAME;

-- ── Instance identity ──────────────────────────────────────────────────
INSERT INTO #R
SELECT @ServerName, @CapturedAtUtc, N''Instance'', N''ProductVersion'',
       CAST(SERVERPROPERTY(''ProductVersion'') AS nvarchar(4000)), N''INFO'', N''SQL version'';

INSERT INTO #R
SELECT @ServerName, @CapturedAtUtc, N''Instance'', N''Edition'',
       CAST(SERVERPROPERTY(''Edition'') AS nvarchar(4000)), N''INFO'', N''Edition'';

-- ── CPU / Memory / Start time ──────────────────────────────────────────
INSERT INTO #R
SELECT @ServerName, @CapturedAtUtc, N''CPU'', N''LogicalCPUCount'',
       CAST(cpu_count AS nvarchar(100)), N''INFO'', N''Visible CPU count''
FROM sys.dm_os_sys_info;

INSERT INTO #R
SELECT @ServerName, @CapturedAtUtc, N''Memory'', N''PhysicalMemoryGB'',
       CAST(CAST(physical_memory_kb / 1048576.0 AS decimal(18,2)) AS nvarchar(100)),
       N''INFO'', N''Host memory visible to SQL''
FROM sys.dm_os_sys_info;

INSERT INTO #R
SELECT @ServerName, @CapturedAtUtc, N''Runtime'', N''SQLServerStartTimeUtc'',
       CONVERT(nvarchar(33), CAST(sqlserver_start_time AS datetime2(3)), 126),
       N''INFO'', N''Instance start time''
FROM sys.dm_os_sys_info;

-- ── Key sp_configure settings ──────────────────────────────────────────
INSERT INTO #R
SELECT @ServerName, @CapturedAtUtc, N''Config'', c.name,
       CAST(c.value_in_use AS nvarchar(4000)),
       CASE
         WHEN c.name IN (N''max degree of parallelism'', N''max server memory (MB)'', N''cost threshold for parallelism'') THEN N''HIGH''
         ELSE N''INFO''
       END,
       c.description
FROM sys.configurations c
WHERE c.name IN
(
 N''max degree of parallelism'',
 N''cost threshold for parallelism'',
 N''max server memory (MB)'',
 N''min server memory (MB)'',
 N''backup compression default'',
 N''optimize for ad hoc workloads'',
 N''clr enabled'',
 N''xp_cmdshell'',
 N''blocked process threshold (s)''
);

-- ── Top 15 waits (filtered) ───────────────────────────────────────────
INSERT INTO #R
SELECT TOP (15)
    @ServerName,
    @CapturedAtUtc,
    N''Waits'',
    wait_type,
    CAST(wait_time_ms AS nvarchar(100)),
    CASE
      WHEN wait_type LIKE N''LCK[_]%'' THEN N''HIGH''
      WHEN wait_type LIKE N''PAGEIOLATCH[_]%'' THEN N''HIGH''
      WHEN wait_type IN (N''CXPACKET'', N''CXCONSUMER'', N''SOS_SCHEDULER_YIELD'') THEN N''MEDIUM''
      ELSE N''INFO''
    END,
    N''Waiting tasks='' + CAST(waiting_tasks_count AS nvarchar(100))
FROM sys.dm_os_wait_stats
WHERE wait_type NOT LIKE N''SLEEP%''
  AND wait_type NOT IN
  (
    N''BROKER_EVENTHANDLER'',N''BROKER_RECEIVE_WAITFOR'',N''BROKER_TASK_STOP'',
    N''BROKER_TO_FLUSH'',N''CHECKPOINT_QUEUE'',N''CHKPT'',N''CLR_AUTO_EVENT'',
    N''CLR_MANUAL_EVENT'',N''DBMIRROR_DBM_EVENT'',N''DBMIRROR_EVENTS_QUEUE'',
    N''DBMIRROR_WORKER_QUEUE'',N''DBMIRRORING_CMD'',N''DIRTY_PAGE_POLL'',
    N''DISPATCHER_QUEUE_SEMAPHORE'',N''EXECSYNC'',N''FSAGENT'',N''FT_IFTS_SCHEDULER_IDLE_WAIT'',
    N''FT_IFTSHC_MUTEX'',N''HADR_CLUSAPI_CALL'',N''HADR_FILESTREAM_IOMGR_IOCOMPLETION'',
    N''HADR_LOGCAPTURE_WAIT'',N''HADR_NOTIFICATION_DEQUEUE'',N''HADR_TIMER_TASK'',
    N''HADR_WORK_QUEUE'',N''KSOURCE_WAKEUP'',N''LAZYWRITER_SLEEP'',N''LOGMGR_QUEUE'',
    N''ONDEMAND_TASK_QUEUE'',N''PWAIT_ALL_COMPONENTS_INITIALIZED'',
    N''QDS_PERSIST_TASK_MAIN_LOOP_SLEEP'',
    N''QDS_CLEANUP_STALE_QUERIES_TASK_MAIN_LOOP_SLEEP'',
    N''REQUEST_FOR_DEADLOCK_SEARCH'',
    N''RESOURCE_QUEUE'',N''SERVER_IDLE_CHECK'',N''SLEEP_BPOOL_FLUSH'',N''SLEEP_DBSTARTUP'',
    N''SLEEP_DCOMSTARTUP'',N''SLEEP_MASTERDBREADY'',N''SLEEP_MASTERMDREADY'',
    N''SLEEP_MASTERUPGRADED'',N''SLEEP_MSDBSTARTUP'',N''SLEEP_SYSTEMTASK'',
    N''SLEEP_TASK'',N''SLEEP_TEMPDBSTARTUP'',N''SNI_HTTP_ACCEPT'',
    N''SP_SERVER_DIAGNOSTICS_SLEEP'',
    N''SQLTRACE_BUFFER_FLUSH'',N''SQLTRACE_INCREMENTAL_FLUSH_SLEEP'',
    N''SQLTRACE_WAIT_ENTRIES'',
    N''WAIT_FOR_RESULTS'',N''WAITFOR'',N''WAITFOR_TASKSHUTDOWN'',
    N''WAIT_XTP_RECOVERY'',N''WAIT_XTP_HOST_WAIT'',
    N''WAIT_XTP_OFFLINE_CKPT_NEW_LOG'',N''WAIT_XTP_CKPT_CLOSE'',
    N''XE_DISPATCHER_JOIN'',N''XE_DISPATCHER_WAIT'',N''XE_TIMER_EVENT''
  )
ORDER BY wait_time_ms DESC;

-- ── Active requests (top 20 by CPU) ───────────────────────────────────
;WITH running_reqs AS
(
    SELECT
        r.session_id,
        r.status,
        r.command,
        r.cpu_time,
        r.total_elapsed_time,
        r.logical_reads,
        r.reads,
        r.writes,
        r.blocking_session_id,
        DB_NAME(r.database_id) AS database_name,
        SUBSTRING(st.text,
                  (r.statement_start_offset/2)+1,
                  CASE WHEN r.statement_end_offset = -1
                       THEN LEN(CONVERT(nvarchar(max), st.text))
                       ELSE (r.statement_end_offset - r.statement_start_offset)/2 + 1 END) AS stmt_text
    FROM sys.dm_exec_requests r
    OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) st
    WHERE r.session_id <> @@SPID
)
INSERT INTO #R
SELECT TOP (20)
    @ServerName,
    @CapturedAtUtc,
    N''ActiveRequest'',
    N''SPID '' + CAST(session_id AS nvarchar(50)),
    CAST(cpu_time AS nvarchar(100)),
    CASE
      WHEN cpu_time >= 30000 THEN N''CRITICAL''
      WHEN cpu_time >= 10000 THEN N''HIGH''
      ELSE N''INFO''
    END,
    N''db='' + ISNULL(database_name, N''?'')
    + N''; status='' + ISNULL(status, N''?'')
    + N''; command='' + ISNULL(command, N''?'')
    + N''; blocker='' + CAST(blocking_session_id AS nvarchar(50))
    + N''; reads='' + CAST(reads AS nvarchar(100))
    + N''; writes='' + CAST(writes AS nvarchar(100))
    + N''; sql='' + LEFT(REPLACE(REPLACE(ISNULL(stmt_text, N''''), CHAR(13), N'' ''), CHAR(10), N'' ''), 1200)
FROM running_reqs
ORDER BY cpu_time DESC, logical_reads DESC;

-- ── Blocking tree ─────────────────────────────────────────────────────
;WITH blockers AS
(
    SELECT
        r.blocking_session_id,
        COUNT(*) AS blocked_count
    FROM sys.dm_exec_requests r
    WHERE r.blocking_session_id > 0
    GROUP BY r.blocking_session_id
)
INSERT INTO #R
SELECT
    @ServerName,
    @CapturedAtUtc,
    N''Blocking'',
    N''LeadBlocker SPID '' + CAST(b.blocking_session_id AS nvarchar(50)),
    CAST(b.blocked_count AS nvarchar(100)),
    CASE
      WHEN b.blocked_count >= 10 THEN N''CRITICAL''
      WHEN b.blocked_count >= 3  THEN N''HIGH''
      ELSE N''MEDIUM''
    END,
    N''Blocked sessions behind blocker''
FROM blockers b;

INSERT INTO #R
SELECT
    @ServerName,
    @CapturedAtUtc,
    N''Blocking'',
    N''TotalBlockedRequests'',
    CAST(COUNT(*) AS nvarchar(100)),
    CASE
      WHEN COUNT(*) >= 10 THEN N''CRITICAL''
      WHEN COUNT(*) >= 3  THEN N''HIGH''
      ELSE N''INFO''
    END,
    N''Current blocked requests''
FROM sys.dm_exec_requests
WHERE blocking_session_id > 0;

-- ── Memory grants ─────────────────────────────────────────────────────
INSERT INTO #R
SELECT TOP (10)
    @ServerName,
    @CapturedAtUtc,
    N''MemoryGrant'',
    N''SPID '' + CAST(session_id AS nvarchar(50)),
    CAST(requested_memory_kb AS nvarchar(100)),
    CASE
      WHEN wait_time_ms >= 10000 THEN N''HIGH''
      ELSE N''INFO''
    END,
    N''granted='' + CAST(granted_memory_kb AS nvarchar(100))
    + N''; required='' + CAST(required_memory_kb AS nvarchar(100))
    + N''; wait_ms='' + CAST(wait_time_ms AS nvarchar(100))
FROM sys.dm_exec_query_memory_grants
ORDER BY requested_memory_kb DESC;

INSERT INTO #R
SELECT
    @ServerName,
    @CapturedAtUtc,
    N''Memory'',
    N''PendingMemoryGrants'',
    CAST(COUNT(*) AS nvarchar(100)),
    CASE
      WHEN COUNT(*) >= 5 THEN N''CRITICAL''
      WHEN COUNT(*) >= 1 THEN N''HIGH''
      ELSE N''INFO''
    END,
    N''Pending query memory grants''
FROM sys.dm_exec_query_memory_grants
WHERE grant_time IS NULL;

-- ── File I/O latency ──────────────────────────────────────────────────
;WITH io AS
(
    SELECT
        DB_NAME(vfs.database_id) AS database_name,
        mf.physical_name,
        vfs.num_of_reads,
        vfs.num_of_writes,
        vfs.io_stall_read_ms,
        vfs.io_stall_write_ms,
        CASE WHEN vfs.num_of_reads = 0 THEN 0 ELSE vfs.io_stall_read_ms * 1.0 / vfs.num_of_reads END AS avg_read_ms,
        CASE WHEN vfs.num_of_writes = 0 THEN 0 ELSE vfs.io_stall_write_ms * 1.0 / vfs.num_of_writes END AS avg_write_ms
    FROM sys.dm_io_virtual_file_stats(NULL, NULL) vfs
    JOIN sys.master_files mf
      ON vfs.database_id = mf.database_id
     AND vfs.file_id = mf.file_id
)
INSERT INTO #R
SELECT TOP (20)
    @ServerName,
    @CapturedAtUtc,
    N''FileIO'',
    ISNULL(database_name, N''?'') + N'':'' + physical_name,
    CAST(CAST(avg_read_ms AS decimal(18,2)) AS nvarchar(100)),
    CASE
      WHEN avg_read_ms >= 20 OR avg_write_ms >= 20 THEN N''HIGH''
      ELSE N''INFO''
    END,
    N''avg_read_ms='' + CAST(CAST(avg_read_ms AS decimal(18,2)) AS nvarchar(100))
    + N''; avg_write_ms='' + CAST(CAST(avg_write_ms AS decimal(18,2)) AS nvarchar(100))
FROM io
ORDER BY CASE WHEN avg_read_ms > avg_write_ms THEN avg_read_ms ELSE avg_write_ms END DESC;

-- ── Running SQL Agent jobs ────────────────────────────────────────────
IF EXISTS (SELECT 1 FROM msdb.sys.objects WHERE name = N''sysjobactivity'')
BEGIN
    INSERT INTO #R
    SELECT
        @ServerName,
        @CapturedAtUtc,
        N''SQLAgent'',
        N''RunningJob:'' + j.name,
        CONVERT(nvarchar(33), ja.start_execution_date, 126),
        CASE
          WHEN j.name LIKE N''%index%'' OR j.name LIKE N''%rebuild%'' OR j.name LIKE N''%maintenance%'' THEN N''HIGH''
          ELSE N''INFO''
        END,
        N''Running SQL Agent job''
    FROM msdb.dbo.sysjobactivity ja
    JOIN msdb.dbo.sysjobs j
      ON ja.job_id = j.job_id
    WHERE ja.start_execution_date IS NOT NULL
      AND ja.stop_execution_date IS NULL
      AND ja.session_id = (SELECT MAX(session_id) FROM msdb.dbo.syssessions);
END;

-- ── Index maintenance operations in progress ──────────────────────────
;WITH idx AS
(
    SELECT
        r.session_id,
        r.command,
        r.percent_complete,
        DB_NAME(r.database_id) AS database_name,
        SUBSTRING(t.text,
                  (r.statement_start_offset/2)+1,
                  CASE WHEN r.statement_end_offset = -1
                       THEN LEN(CONVERT(nvarchar(max), t.text))
                       ELSE (r.statement_end_offset - r.statement_start_offset)/2 + 1 END) AS stmt_text
    FROM sys.dm_exec_requests r
    OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) t
    WHERE r.command LIKE N''%INDEX%''
       OR r.command LIKE N''%DBCC%''
)
INSERT INTO #R
SELECT
    @ServerName,
    @CapturedAtUtc,
    N''IndexMaintenance'',
    N''SPID '' + CAST(session_id AS nvarchar(50)),
    CAST(percent_complete AS nvarchar(100)),
    N''HIGH'',
    N''db='' + ISNULL(database_name, N''?'')
    + N''; command='' + ISNULL(command, N''?'')
    + N''; sql='' + LEFT(REPLACE(REPLACE(ISNULL(stmt_text, N''''), CHAR(13), N'' ''), CHAR(10), N'' ''), 1200)
FROM idx;

-- ── Final output ──────────────────────────────────────────────────────
SELECT
    ServerName,
    CapturedAtUtc,
    Category,
    MetricName,
    MetricValue,
    SeverityHint,
    Detail
FROM #R
ORDER BY
    CASE SeverityHint WHEN N''CRITICAL'' THEN 1 WHEN N''HIGH'' THEN 2 WHEN N''MEDIUM'' THEN 3 ELSE 4 END,
    Category,
    MetricName;',
    1
);

-- ── Windows Live: Root Cause Diagnostic ───────────────────────────────────
DELETE FROM [DataBOT].[QuestionSamples]
WHERE [Environment] = 'Windows_Live' AND [GroupKey] = 'Diagnostic';

INSERT INTO [DataBOT].[QuestionSamples]
    ([Environment], [GroupKey], [GroupTitle], [GroupOrder], [QuestionText], [QuestionOrder], [Tags], [Script], [IsActive])
VALUES
(
    'Windows_Live',
    'Diagnostic',
    'Windows Health',
    6,
    'Why is this Windows Server struggling right now?',
    1,
    'Diagnostic,RootCause,Health,CPU,Memory,Disk,Process,Service,IIS,SQLServer,ScheduledTask',
    N'$ErrorActionPreference = ''SilentlyContinue''

$CapturedAtUtc = [DateTime]::UtcNow.ToString(''o'')
$ServerName    = $env:COMPUTERNAME
$Result        = New-Object System.Collections.Generic.List[object]

function Add-Row {
    param(
        [string]$Category,
        [string]$MetricName,
        [string]$MetricValue,
        [string]$SeverityHint = ''INFO'',
        [string]$Detail = ''''
    )

    $Result.Add([pscustomobject]@{
        ServerName    = $ServerName
        CapturedAtUtc = $CapturedAtUtc
        Category      = $Category
        MetricName    = $MetricName
        MetricValue   = if ($null -eq $MetricValue -or $MetricValue -eq '''') { ''<null>'' } else { [string]$MetricValue }
        SeverityHint  = $SeverityHint
        Detail        = $Detail
    })
}

# ── 1. OS / Memory ─────────────────────────────────────────────────────
try {
    $os = Get-CimInstance Win32_OperatingSystem
    if ($os) {
        $memFreeGb  = [math]::Round($os.FreePhysicalMemory / 1MB, 2)
        $memTotalGb = [math]::Round($os.TotalVisibleMemorySize / 1MB, 2)
        $memUsedPct = if ($os.TotalVisibleMemorySize -gt 0) { [math]::Round((($os.TotalVisibleMemorySize - $os.FreePhysicalMemory) * 100.0) / $os.TotalVisibleMemorySize, 2) } else { 0 }

        Add-Row ''OS'' ''Caption'' $os.Caption ''INFO'' ''Operating system''
        Add-Row ''OS'' ''Version'' $os.Version ''INFO'' ''OS version''
        Add-Row ''Memory'' ''MemoryUsedPct'' $memUsedPct $(if($memUsedPct -ge 90){''CRITICAL''}elseif($memUsedPct -ge 80){''HIGH''}else{''INFO''}) ''Overall OS memory pressure''
        Add-Row ''Memory'' ''MemoryFreeGB'' $memFreeGb ''INFO'' ''Free physical memory''
        Add-Row ''Memory'' ''MemoryTotalGB'' $memTotalGb ''INFO'' ''Total visible memory''
    }
} catch {}

# ── 2. Host CPU ────────────────────────────────────────────────────────
try {
    $cpuCounter = Get-Counter ''\Processor(_Total)\% Processor Time'' -ErrorAction Stop
    $cpu = [math]::Round($cpuCounter.CounterSamples[0].CookedValue, 2)
    Add-Row ''CPU'' ''HostCpuPct'' $cpu $(if($cpu -ge 90){''CRITICAL''}elseif($cpu -ge 75){''HIGH''}else{''INFO''}) ''Overall host CPU''
} catch {}

# ── 3. Top processes by CPU ────────────────────────────────────────────
try {
    $procs = Get-CimInstance Win32_PerfFormattedData_PerfProc_Process |
        Where-Object { $_.Name -notmatch ''^(Idle|_Total)$'' } |
        Sort-Object PercentProcessorTime -Descending |
        Select-Object -First 15

    foreach ($p in $procs) {
        $sev = if ([double]$p.PercentProcessorTime -ge 70) { ''CRITICAL'' }
               elseif ([double]$p.PercentProcessorTime -ge 30) { ''HIGH'' }
               else { ''INFO'' }

        Add-Row ''TopProcessCpu'' $p.Name $p.PercentProcessorTime $sev ("PID=" + $p.IDProcess + "; WS_MB=" + [math]::Round(($p.WorkingSet / 1MB), 2))
    }
} catch {}

# ── 4. Key processes: sqlservr, w3wp, powershell, pwsh ─────────────────
try {
    $procTargets = Get-CimInstance Win32_PerfFormattedData_PerfProc_Process |
        Where-Object { $_.Name -match ''^(sqlservr|w3wp|powershell|pwsh)'' }

    foreach ($p in $procTargets) {
        $sev = if ([double]$p.PercentProcessorTime -ge 70) { ''CRITICAL'' }
               elseif ([double]$p.PercentProcessorTime -ge 25) { ''HIGH'' }
               else { ''INFO'' }

        Add-Row ''KeyProcess'' ($p.Name + '':CpuPct'') $p.PercentProcessorTime $sev ("PID=" + $p.IDProcess)
        Add-Row ''KeyProcess'' ($p.Name + '':WorkingSetMB'') ([math]::Round(($p.WorkingSet / 1MB), 2)) ''INFO'' ("PID=" + $p.IDProcess)
        Add-Row ''KeyProcess'' ($p.Name + '':IOReadOpsPerSec'') $p.IOReadOperationsPerSec ''INFO'' ("PID=" + $p.IDProcess)
        Add-Row ''KeyProcess'' ($p.Name + '':IOWriteOpsPerSec'') $p.IOWriteOperationsPerSec ''INFO'' ("PID=" + $p.IDProcess)
    }
} catch {}

# ── 5. SQL Server memory pressure analysis ─────────────────────────────
try {
    $sqlProcs = Get-Process -Name ''sqlservr'' -ErrorAction SilentlyContinue
    if ($sqlProcs) {
        $totalSqlMb = 0
        foreach ($sp in $sqlProcs) {
            $wsMb = [math]::Round($sp.WorkingSet64 / 1MB, 2)
            $totalSqlMb += $wsMb

            # Try to find the instance name via service association
            $instanceName = ''Default''
            try {
                $sqlSvc = Get-CimInstance Win32_Service | Where-Object {
                    $_.ProcessId -eq $sp.Id -and $_.Name -match ''MSSQL''
                } | Select-Object -First 1
                if ($sqlSvc) {
                    if ($sqlSvc.Name -match ''MSSQL\$(.+)'') { $instanceName = $Matches[1] }
                    elseif ($sqlSvc.Name -eq ''MSSQLSERVER'') { $instanceName = ''Default'' }
                }
            } catch {}

            $sev = if ($wsMb -ge 8000) { ''HIGH'' }
                   elseif ($wsMb -ge 4000) { ''MEDIUM'' }
                   else { ''INFO'' }

            Add-Row ''SQLMemory'' ("sqlservr:" + $instanceName + ":WorkingSetMB") $wsMb $sev ("PID=" + $sp.Id + "; Instance=" + $instanceName)
        }

        # Total SQL Server memory vs host memory
        if ($os) {
            $totalMemMb = [math]::Round($os.TotalVisibleMemorySize / 1KB, 0)
            $sqlPctOfHost = if ($totalMemMb -gt 0) { [math]::Round(($totalSqlMb * 100.0) / $totalMemMb, 2) } else { 0 }
            $sev = if ($sqlPctOfHost -ge 85) { ''CRITICAL'' }
                   elseif ($sqlPctOfHost -ge 60) { ''HIGH'' }
                   else { ''INFO'' }
            Add-Row ''SQLMemory'' ''TotalSqlServerMemoryMB'' ([math]::Round($totalSqlMb, 0)) $sev ("SqlPctOfHost=" + $sqlPctOfHost + "%; Instances=" + $sqlProcs.Count)
        }
    }
} catch {}

# ── 6. SQL Server services (all instances) ─────────────────────────────
try {
    $sqlServices = Get-Service | Where-Object { $_.Name -match ''^MSSQL(\$|SERVER)'' -or $_.Name -match ''^SQLAgent'' -or $_.Name -match ''^SQLSERVERAGENT'' }
    foreach ($svc in $sqlServices) {
        $sev = if ($svc.Status -ne ''Running'' -and $svc.StartType -eq ''Automatic'') { ''HIGH'' } else { ''INFO'' }
        Add-Row ''SQLService'' ($svc.Name + '':Status'') ([string]$svc.Status) $sev ("StartType=" + $svc.StartType + "; DisplayName=" + $svc.DisplayName)
    }
} catch {}

# ── 7. Disk latency and queue ──────────────────────────────────────────
try {
    $diskCounters = Get-Counter ''\PhysicalDisk(_Total)\Avg. Disk sec/Read'',''\PhysicalDisk(_Total)\Avg. Disk sec/Write'',''\PhysicalDisk(_Total)\Current Disk Queue Length'' -ErrorAction Stop
    $readMs  = [math]::Round(($diskCounters.CounterSamples | Where-Object { $_.Path -like ''*Avg. Disk sec/Read*'' }  | Select-Object -First 1).CookedValue * 1000, 2)
    $writeMs = [math]::Round(($diskCounters.CounterSamples | Where-Object { $_.Path -like ''*Avg. Disk sec/Write*'' } | Select-Object -First 1).CookedValue * 1000, 2)
    $queue   = [math]::Round(($diskCounters.CounterSamples | Where-Object { $_.Path -like ''*Current Disk Queue Length*'' } | Select-Object -First 1).CookedValue, 2)

    $sev = if ($readMs -ge 30 -or $writeMs -ge 30 -or $queue -ge 10) { ''CRITICAL'' }
           elseif ($readMs -ge 15 -or $writeMs -ge 15 -or $queue -ge 3) { ''HIGH'' }
           else { ''INFO'' }

    Add-Row ''Disk'' ''AvgDiskReadMs''   $readMs  $sev ''Average disk read latency''
    Add-Row ''Disk'' ''AvgDiskWriteMs''  $writeMs $sev ''Average disk write latency''
    Add-Row ''Disk'' ''DiskQueueLength'' $queue   $sev ''Current disk queue length''
} catch {}

# ── 8. Volume free space ──────────────────────────────────────────────
try {
    $vols = Get-CimInstance Win32_LogicalDisk -Filter "DriveType=3"
    foreach ($v in $vols) {
        $freePct = if ($v.Size -gt 0) { [math]::Round(($v.FreeSpace * 100.0) / $v.Size, 2) } else { 0 }
        $sev = if ($freePct -le 5) { ''CRITICAL'' }
               elseif ($freePct -le 15) { ''HIGH'' }
               else { ''INFO'' }

        Add-Row ''Volume'' ($v.DeviceID + '':FreePct'') $freePct $sev ''Volume free percentage''
        Add-Row ''Volume'' ($v.DeviceID + '':FreeGB'')  ([math]::Round(($v.FreeSpace / 1GB), 2)) ''INFO'' ''Volume free GB''
    }
} catch {}

# ── 9. Key services ───────────────────────────────────────────────────
try {
    $svcNames = @(''W3SVC'',''WinRM'',''MpsSvc'',''WinDefend'',''EventLog'',''Schedule'')
    foreach ($svcName in $svcNames) {
        $svc = Get-Service -Name $svcName -ErrorAction SilentlyContinue
        if ($svc) {
            $sev = if ($svc.Status -ne ''Running'') { ''HIGH'' } else { ''INFO'' }
            Add-Row ''Service'' ($svc.Name + '':Status'')    ([string]$svc.Status)    $sev ''Service state''
            Add-Row ''Service'' ($svc.Name + '':StartType'') ([string]$svc.StartType) ''INFO'' ''Service startup type''
        }
    }
} catch {}

# ── 10. IIS application pools (using WebAdministration module) ─────────
try {
    Import-Module WebAdministration -ErrorAction Stop
    $pools = Get-ChildItem IIS:\AppPools -ErrorAction Stop
    foreach ($pool in $pools) {
        $state = $pool.State
        $sev = if ($state -ne ''Started'') { ''HIGH'' } else { ''INFO'' }
        Add-Row ''IISAppPool'' $pool.Name ([string]$state) $sev ''IIS application pool state''
    }
} catch {
    # Fallback: use appcmd with clean output
    try {
        $appcmdPath = "$env:windir\system32\inetsrv\appcmd.exe"
        if (Test-Path $appcmdPath) {
            $poolList = & $appcmdPath list apppool /xml 2>$null
            if ($poolList) {
                [xml]$xmlPools = ($poolList -join "`n")
                foreach ($ap in $xmlPools.appcmd.APPPOOL) {
                    $name  = $ap.''APPPOOL.NAME''
                    $state = $ap.state
                    $sev   = if ($state -ne ''Started'') { ''HIGH'' } else { ''INFO'' }
                    Add-Row ''IISAppPool'' $name $state $sev ''IIS application pool state''
                }
            }
        }
    } catch {}
}

# ── 11. Running scheduled tasks ───────────────────────────────────────
try {
    $runningTasks = Get-ScheduledTask | Where-Object { $_.State -eq ''Running'' } |
        Select-Object -First 15
    foreach ($t in $runningTasks) {
        $taskPath = $t.TaskPath + $t.TaskName
        $sev = ''MEDIUM''
        # Elevate severity for tasks that spawn processes eating CPU
        if ($t.TaskName -match ''powershell|script|scheduler|backup|maintenance|index|rebuild'') {
            $sev = ''HIGH''
        }
        Add-Row ''ScheduledTask'' $t.TaskName ''Running'' $sev ("Path=" + $taskPath)
    }
    # Count total running tasks
    $runCount = ($runningTasks | Measure-Object).Count
    if ($runCount -ge 5) {
        Add-Row ''ScheduledTask'' ''TotalRunningTasks'' $runCount ''HIGH'' ''Many scheduled tasks running concurrently''
    }
} catch {}

# ── 12. PowerShell / wsmprovhost sessions ──────────────────────────────
try {
    $psProcs = Get-Process | Where-Object {
        $_.ProcessName -match ''^(powershell|pwsh|wsmprovhost)$''
    }
    $psCpuTotal = 0
    $psMemTotal = 0
    $psCount = 0
    foreach ($p in $psProcs) {
        $wsMb = [math]::Round($p.WorkingSet64 / 1MB, 2)
        $cpuSec = [math]::Round($p.CPU, 0)
        $psCpuTotal += $cpuSec
        $psMemTotal += $wsMb
        $psCount++

        $sev = if ($wsMb -ge 500) { ''HIGH'' }
               elseif ($wsMb -ge 200) { ''MEDIUM'' }
               else { ''INFO'' }

        Add-Row ''PSSession'' ($p.ProcessName + '':'' + $p.Id) $wsMb $sev ("CpuSec=" + $cpuSec + "; WS_MB=" + $wsMb + "; CommandLine=check via Get-CimInstance Win32_Process")
    }
    if ($psCount -ge 3) {
        $sev = if ($psCount -ge 10) { ''HIGH'' }
               elseif ($psCount -ge 5) { ''MEDIUM'' }
               else { ''INFO'' }
        Add-Row ''PSSession'' ''TotalPSSessions'' $psCount $sev ("TotalMemMB=" + [math]::Round($psMemTotal, 0) + "; TotalCpuSec=" + $psCpuTotal)
    }
} catch {}

# ── 13. Background / backup / scanner processes ────────────────────────
try {
    $bgProcs = Get-Process | Where-Object {
        $_.ProcessName -match ''backup|veeam|av|defender|mcshield|sqlwriter|wbengine''
    } | Select-Object -First 20
    foreach ($p in $bgProcs) {
        $wsMb = [math]::Round($p.WorkingSet64 / 1MB, 2)
        Add-Row ''BackgroundProcess'' $p.ProcessName $p.Id ''MEDIUM'' ("WS_MB=" + $wsMb)
    }
} catch {}

# ── 14. Network throughput ────────────────────────────────────────────
try {
    $net = Get-Counter ''\Network Interface(*)\Bytes Total/sec'' -ErrorAction Stop
    $samples = $net.CounterSamples | Where-Object { $_.InstanceName -notmatch ''isatap|loopback|teredo'' } |
        Sort-Object CookedValue -Descending | Select-Object -First 5
    foreach ($s in $samples) {
        $mbps = [math]::Round($s.CookedValue / 1MB, 2)
        Add-Row ''Network'' $s.InstanceName $mbps ''INFO'' ''MB/sec throughput''
    }
} catch {}

# ── 15. Pending reboot ────────────────────────────────────────────────
try {
    $pendingReboot = $false
    if (Test-Path ''HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending'') { $pendingReboot = $true }
    if (Test-Path ''HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired'') { $pendingReboot = $true }
    $sev = if ($pendingReboot) { ''MEDIUM'' } else { ''INFO'' }
    Add-Row ''Maintenance'' ''PendingReboot'' $(if ($pendingReboot) { ''YES'' } else { ''NO'' }) $sev ''Pending reboot indicator''
} catch {}

$Result | Sort-Object @{Expression={switch($_.SeverityHint){''CRITICAL''{1}''HIGH''{2}''MEDIUM''{3}default{4}}}}, Category, MetricName',
    1
);

PRINT 'Root-cause diagnostic samples upserted successfully.';
