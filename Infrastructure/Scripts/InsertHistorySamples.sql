-- =============================================================================
-- HISTORY QUESTION SAMPLES: SqlServer_History + Windows_History
-- Run against: CTS03 / SQLGig  ([SQLGig].[DataBOT].[QuestionSamples])
--
-- All sample scripts run centrally on CTS03/SQLGig.
-- Pipeline binds: @FromUtc, @ToUtc, @Top, /*__*_FILTER__*/ tokens.
-- Output format: ServerName, CapturedAtUtc, MetricGroup, MetricName, MetricValue, Detail
--
-- DESIGN:
--   1. ADAPTIVE BUCKETING: @BucketMin auto-scales with time range
--        <= 2h  -> 1 min   (raw)
--        <= 24h -> 5 min   (~288 pts/server)
--        <= 7d  -> 30 min  (~336 pts/server)
--        <= 30d -> 1 hour  (~720 pts/server)
--        > 30d  -> 1 day   (~90 pts/3mo)
--   2. CROSS APPLY metric splitting: each metric = separate MetricName row
--   3. ALL data is included (AVG/MAX) — bucketing compresses, never skips
--   4. @Top is safety cap only (5000), never the primary limit
--   5. Snapshot/inventory scripts use ROW_NUMBER() rn=1 for latest-per-server
-- =============================================================================

SET NOCOUNT ON;
BEGIN TRANSACTION;

-- ---------------------------------------------------------------------------
-- Remove old History samples (including Metrics — we re-insert below)
-- ---------------------------------------------------------------------------
DELETE FROM [SQLGig].[DataBOT].[QuestionSamples]
WHERE [Environment] IN (N'SqlServer_History', N'Windows_History')
  AND [IsActive] = 1;

PRINT 'Deleted old History samples: ' + CAST(@@ROWCOUNT AS varchar(10));

-- =============================================================================
-- ###  SqlServer_History                                                    ###
-- =============================================================================

-- --- Group 1: Instance Health ------------------------------------------------

INSERT INTO [SQLGig].[DataBOT].[QuestionSamples]
    ([Environment],[GroupKey],[GroupTitle],[GroupOrder],[QuestionText],[QuestionOrder],[Tags],[Script],[IsActive],[UpdatedAt],[UpdatedBy])
VALUES (N'SqlServer_History', N'InstanceHealth', N'Instance Health', 1,
    N'Show SQL Server health dashboard - key metrics over time', 1,
    N'Health,Dashboard,PLE,Blocking,Memory,Workload',
    N'DECLARE @FromUtc datetime2(0) = DATEADD(HOUR, -24, SYSUTCDATETIME());
DECLARE @ToUtc   datetime2(0) = SYSUTCDATETIME();
DECLARE @Top     int          = 5000;

DECLARE @SpanMin int = DATEDIFF(MINUTE, @FromUtc, @ToUtc);
DECLARE @BucketMin int = CASE
    WHEN @SpanMin <= 120   THEN 1
    WHEN @SpanMin <= 1440  THEN 5
    WHEN @SpanMin <= 10080 THEN 30
    WHEN @SpanMin <= 43200 THEN 60
    ELSE 1440 END;

;WITH Bucketed AS (
    SELECT
        h.SQLServer,
        DATEADD(MINUTE, (DATEDIFF(MINUTE, 0, h.DateTime) / @BucketMin) * @BucketMin, 0) AS TimeBucket,
        AVG(CAST(h.PageLifeExpectancy_seconds AS float))  AS AvgPLE,
        AVG(CAST(h.BatchRequests_sec AS float))           AS AvgBatchReq,
        MAX(h.BlockingCount)                              AS MaxBlocking,
        MAX(h.DeadlockCount)                              AS MaxDeadlocks,
        AVG(CAST(h.UserConnections AS float))             AS AvgUsers,
        MAX(h.FailedJobCount)                             AS MaxFailedJobs,
        AVG(CAST(h.MemoryGrantsPending AS float))         AS AvgGrantsPending
    FROM [SQLGig].[Monitor].[SQLServer_Details_History] h
    WHERE h.DateTime >= @FromUtc AND h.DateTime < @ToUtc
        /*__SQLSERVER_FILTER__*/
    GROUP BY h.SQLServer,
             DATEADD(MINUTE, (DATEDIFF(MINUTE, 0, h.DateTime) / @BucketMin) * @BucketMin, 0)
)
SELECT TOP (@Top)
    v.ServerName COLLATE DATABASE_DEFAULT AS [ServerName],
    v.CapturedAtUtc,
    v.MetricGroup COLLATE DATABASE_DEFAULT AS [MetricGroup],
    v.MetricName COLLATE DATABASE_DEFAULT AS [MetricName],
    v.MetricValue,
    v.Detail COLLATE DATABASE_DEFAULT AS [Detail]
FROM Bucketed b
CROSS APPLY (
    SELECT b.SQLServer, b.TimeBucket, N''InstanceHealth'', N''PLE'',
           CAST(b.AvgPLE AS decimal(18,2)),
           N''PLE='' + CAST(CAST(b.AvgPLE AS int) AS nvarchar(20)) + N''; GrantsPending='' + CAST(CAST(b.AvgGrantsPending AS int) AS nvarchar(20))
    UNION ALL
    SELECT b.SQLServer, b.TimeBucket, N''InstanceHealth'', N''BatchRequests'',
           CAST(b.AvgBatchReq AS decimal(18,2)),
           N''BatchReq/s='' + CAST(CAST(b.AvgBatchReq AS int) AS nvarchar(20)) + N''; Users='' + CAST(CAST(b.AvgUsers AS int) AS nvarchar(20))
    UNION ALL
    SELECT b.SQLServer, b.TimeBucket, N''InstanceHealth'', N''BlockingCount'',
           CAST(b.MaxBlocking AS decimal(18,2)),
           N''Blocking='' + CAST(b.MaxBlocking AS nvarchar(20)) + N''; Deadlocks='' + CAST(b.MaxDeadlocks AS nvarchar(20))
    UNION ALL
    SELECT b.SQLServer, b.TimeBucket, N''InstanceHealth'', N''UserConnections'',
           CAST(b.AvgUsers AS decimal(18,2)),
           N''Users='' + CAST(CAST(b.AvgUsers AS int) AS nvarchar(20)) + N''; FailedJobs='' + CAST(b.MaxFailedJobs AS nvarchar(20))
) v(ServerName, CapturedAtUtc, MetricGroup, MetricName, MetricValue, Detail)
ORDER BY v.ServerName, v.CapturedAtUtc, v.MetricName;',
    1, SYSUTCDATETIME(), N'InsertHistorySamples');

INSERT INTO [SQLGig].[DataBOT].[QuestionSamples]
    ([Environment],[GroupKey],[GroupTitle],[GroupOrder],[QuestionText],[QuestionOrder],[Tags],[Script],[IsActive],[UpdatedAt],[UpdatedBy])
VALUES (N'SqlServer_History', N'InstanceHealth', N'Instance Health', 1,
    N'When did SQL Server restart? Show uptime history', 2,
    N'Restart,Uptime,Availability,StartTime',
    N'DECLARE @FromUtc datetime2(0) = DATEADD(DAY, -7, SYSUTCDATETIME());
DECLARE @ToUtc   datetime2(0) = SYSUTCDATETIME();
DECLARE @Top     int          = 5000;

;WITH Restarts AS (
    SELECT
        h.SQLServer,
        h.DateTime,
        h.SQLServerStartTime,
        h.UptimeMinutes,
        LAG(h.SQLServerStartTime) OVER (PARTITION BY h.SQLServer ORDER BY h.DateTime) AS PrevStartTime
    FROM [SQLGig].[Monitor].[SQLServer_Details_History] h
    WHERE h.DateTime >= @FromUtc AND h.DateTime < @ToUtc
        /*__SQLSERVER_FILTER__*/
)
SELECT TOP (@Top)
    r.SQLServer COLLATE DATABASE_DEFAULT                       AS [ServerName],
    r.DateTime                                                 AS [CapturedAtUtc],
    N''Availability'' COLLATE DATABASE_DEFAULT                  AS [MetricGroup],
    CASE WHEN r.PrevStartTime IS NOT NULL AND r.SQLServerStartTime <> r.PrevStartTime
         THEN N''RESTART_DETECTED'' ELSE N''Running'' END
    COLLATE DATABASE_DEFAULT                                    AS [MetricName],
    CAST(r.UptimeMinutes AS decimal(18,2))                     AS [MetricValue],
    N''StartTime='' + ISNULL(CONVERT(nvarchar(30), r.SQLServerStartTime, 126), N''?'')
    + N''; UptimeMin='' + CAST(ISNULL(r.UptimeMinutes,0) AS nvarchar(20))
    COLLATE DATABASE_DEFAULT                                    AS [Detail]
FROM Restarts r
WHERE r.PrevStartTime IS NULL
   OR r.SQLServerStartTime <> r.PrevStartTime
   OR r.DateTime = (SELECT MAX(DateTime) FROM Restarts r2 WHERE r2.SQLServer = r.SQLServer)
ORDER BY r.SQLServer, r.DateTime DESC;',
    1, SYSUTCDATETIME(), N'InsertHistorySamples');

-- --- Group 2: Memory & Buffer Pool ------------------------------------------

INSERT INTO [SQLGig].[DataBOT].[QuestionSamples]
    ([Environment],[GroupKey],[GroupTitle],[GroupOrder],[QuestionText],[QuestionOrder],[Tags],[Script],[IsActive],[UpdatedAt],[UpdatedBy])
VALUES (N'SqlServer_History', N'MemoryBuffer', N'Memory & Buffer Pool', 2,
    N'Is there memory pressure? Show PLE, grants, and memory trend', 1,
    N'Memory,PLE,MemoryGrants,BufferCache,Pressure',
    N'DECLARE @FromUtc datetime2(0) = DATEADD(HOUR, -24, SYSUTCDATETIME());
DECLARE @ToUtc   datetime2(0) = SYSUTCDATETIME();
DECLARE @Top     int          = 5000;

DECLARE @SpanMin int = DATEDIFF(MINUTE, @FromUtc, @ToUtc);
DECLARE @BucketMin int = CASE
    WHEN @SpanMin <= 120   THEN 1
    WHEN @SpanMin <= 1440  THEN 5
    WHEN @SpanMin <= 10080 THEN 30
    WHEN @SpanMin <= 43200 THEN 60
    ELSE 1440 END;

;WITH Bucketed AS (
    SELECT
        h.SQLServer,
        DATEADD(MINUTE, (DATEDIFF(MINUTE, 0, h.DateTime) / @BucketMin) * @BucketMin, 0) AS TimeBucket,
        AVG(CAST(h.PageLifeExpectancy_seconds AS float))  AS AvgPLE,
        AVG(CAST(h.AvailableMemory_GB AS float))          AS AvgAvailGB,
        AVG(CAST(h.UsedMemory_GB AS float))               AS AvgUsedGB,
        MAX(h.MemoryGrantsPending)                        AS MaxGrantsPending,
        AVG(CAST(h.BufferCacheHitRatio AS float))         AS AvgCacheHit
    FROM [SQLGig].[Monitor].[SQLServer_Details_History] h
    WHERE h.DateTime >= @FromUtc AND h.DateTime < @ToUtc
        /*__SQLSERVER_FILTER__*/
    GROUP BY h.SQLServer,
             DATEADD(MINUTE, (DATEDIFF(MINUTE, 0, h.DateTime) / @BucketMin) * @BucketMin, 0)
)
SELECT TOP (@Top)
    v.ServerName COLLATE DATABASE_DEFAULT AS [ServerName],
    v.CapturedAtUtc,
    v.MetricGroup COLLATE DATABASE_DEFAULT AS [MetricGroup],
    v.MetricName COLLATE DATABASE_DEFAULT AS [MetricName],
    v.MetricValue,
    v.Detail COLLATE DATABASE_DEFAULT AS [Detail]
FROM Bucketed b
CROSS APPLY (
    SELECT b.SQLServer, b.TimeBucket, N''Memory'', N''PLE'',
           CAST(b.AvgPLE AS decimal(18,2)),
           N''PLE='' + CAST(CAST(b.AvgPLE AS int) AS nvarchar(20)) + N''; CacheHit='' + CAST(CAST(b.AvgCacheHit AS decimal(5,1)) AS nvarchar(20)) + N''%''
    UNION ALL
    SELECT b.SQLServer, b.TimeBucket, N''Memory'', N''AvailableMemoryGB'',
           CAST(b.AvgAvailGB AS decimal(18,2)),
           N''AvailGB='' + CAST(CAST(b.AvgAvailGB AS decimal(10,1)) AS nvarchar(20)) + N''; UsedGB='' + CAST(CAST(b.AvgUsedGB AS decimal(10,1)) AS nvarchar(20))
    UNION ALL
    SELECT b.SQLServer, b.TimeBucket, N''Memory'', N''MemoryGrantsPending'',
           CAST(b.MaxGrantsPending AS decimal(18,2)),
           N''GrantsPending='' + CAST(b.MaxGrantsPending AS nvarchar(20))
) v(ServerName, CapturedAtUtc, MetricGroup, MetricName, MetricValue, Detail)
ORDER BY v.ServerName, v.CapturedAtUtc, v.MetricName;',
    1, SYSUTCDATETIME(), N'InsertHistorySamples');

-- --- Group 3: Blocking & Deadlocks ------------------------------------------

INSERT INTO [SQLGig].[DataBOT].[QuestionSamples]
    ([Environment],[GroupKey],[GroupTitle],[GroupOrder],[QuestionText],[QuestionOrder],[Tags],[Script],[IsActive],[UpdatedAt],[UpdatedBy])
VALUES (N'SqlServer_History', N'BlockingLocking', N'Blocking & Deadlocks', 3,
    N'Show blocking and deadlock incidents over time', 1,
    N'Blocking,Deadlock,Locking,Contention',
    N'DECLARE @FromUtc datetime2(0) = DATEADD(HOUR, -24, SYSUTCDATETIME());
DECLARE @ToUtc   datetime2(0) = SYSUTCDATETIME();
DECLARE @Top     int          = 5000;

DECLARE @SpanMin int = DATEDIFF(MINUTE, @FromUtc, @ToUtc);
DECLARE @BucketMin int = CASE
    WHEN @SpanMin <= 120   THEN 1
    WHEN @SpanMin <= 1440  THEN 5
    WHEN @SpanMin <= 10080 THEN 30
    WHEN @SpanMin <= 43200 THEN 60
    ELSE 1440 END;

;WITH Bucketed AS (
    SELECT
        h.SQLServer,
        DATEADD(MINUTE, (DATEDIFF(MINUTE, 0, h.DateTime) / @BucketMin) * @BucketMin, 0) AS TimeBucket,
        MAX(h.BlockingCount)                              AS MaxBlocking,
        MAX(h.DeadlockCount)                              AS MaxDeadlocks,
        MAX(h.LockCount)                                  AS MaxLocks,
        AVG(CAST(h.LockRequests_sec AS float))            AS AvgLockReq
    FROM [SQLGig].[Monitor].[SQLServer_Details_History] h
    WHERE h.DateTime >= @FromUtc AND h.DateTime < @ToUtc
        /*__SQLSERVER_FILTER__*/
    GROUP BY h.SQLServer,
             DATEADD(MINUTE, (DATEDIFF(MINUTE, 0, h.DateTime) / @BucketMin) * @BucketMin, 0)
    HAVING MAX(h.BlockingCount) > 0 OR MAX(h.DeadlockCount) > 0
)
SELECT TOP (@Top)
    v.ServerName COLLATE DATABASE_DEFAULT AS [ServerName],
    v.CapturedAtUtc,
    v.MetricGroup COLLATE DATABASE_DEFAULT AS [MetricGroup],
    v.MetricName COLLATE DATABASE_DEFAULT AS [MetricName],
    v.MetricValue,
    v.Detail COLLATE DATABASE_DEFAULT AS [Detail]
FROM Bucketed b
CROSS APPLY (
    SELECT b.SQLServer, b.TimeBucket, N''Blocking'', N''BlockingCount'',
           CAST(b.MaxBlocking AS decimal(18,2)),
           N''Blocking='' + CAST(b.MaxBlocking AS nvarchar(20)) + N''; LockCount='' + CAST(b.MaxLocks AS nvarchar(20)) + N''; LockReq/s='' + CAST(CAST(b.AvgLockReq AS int) AS nvarchar(20))
    UNION ALL
    SELECT b.SQLServer, b.TimeBucket, N''Blocking'', N''DeadlockCount'',
           CAST(b.MaxDeadlocks AS decimal(18,2)),
           N''Deadlocks='' + CAST(b.MaxDeadlocks AS nvarchar(20))
) v(ServerName, CapturedAtUtc, MetricGroup, MetricName, MetricValue, Detail)
ORDER BY v.MetricValue DESC, v.CapturedAtUtc DESC;',
    1, SYSUTCDATETIME(), N'InsertHistorySamples');

INSERT INTO [SQLGig].[DataBOT].[QuestionSamples]
    ([Environment],[GroupKey],[GroupTitle],[GroupOrder],[QuestionText],[QuestionOrder],[Tags],[Script],[IsActive],[UpdatedAt],[UpdatedBy])
VALUES (N'SqlServer_History', N'BlockingLocking', N'Blocking & Deadlocks', 3,
    N'Who was blocking whom? Show the blocking chain details', 2,
    N'Blocking,BlockingChain,SPID,WhoIsThere',
    N'DECLARE @FromUtc datetime2(0) = DATEADD(HOUR, -24, SYSUTCDATETIME());
DECLARE @ToUtc   datetime2(0) = SYSUTCDATETIME();
DECLARE @Top     int          = 5000;

SELECT TOP (@Top)
    wh.SQLServer COLLATE DATABASE_DEFAULT                     AS [ServerName],
    wh.DateTime                                               AS [CapturedAtUtc],
    N''BlockingChain'' COLLATE DATABASE_DEFAULT                AS [MetricGroup],
    N''SPID '' + CAST(wh.SPID AS nvarchar(20)) + N'' blocked by '' + CAST(wh.BlkBy AS nvarchar(20))
    COLLATE DATABASE_DEFAULT                                   AS [MetricName],
    CAST(wh.ElapsedMS AS decimal(18,2))                       AS [MetricValue],
    N''CPU='' + CAST(wh.CPU AS nvarchar(20))
    + N''; Reads='' + CAST(wh.IOReads AS nvarchar(20))
    + N''; Writes='' + CAST(wh.IOWrites AS nvarchar(20))
    + N''; Wait='' + ISNULL(wh.LastWaitType, N''?'')
    + N''; DB='' + ISNULL(wh.DBName, N''?'')
    + N''; Login='' + ISNULL(wh.Login, N''?'')
    + N''; SQL='' + LEFT(ISNULL(REPLACE(REPLACE(wh.SQLStatement_900, CHAR(13), N'' ''), CHAR(10), N'' ''), N''''), 300)
    COLLATE DATABASE_DEFAULT                                   AS [Detail]
FROM [SQLGig].[Monitor].[SQLServer_WhoIsThere_History] wh
WHERE wh.DateTime >= @FromUtc AND wh.DateTime < @ToUtc
    /*__SQLSERVER_FILTER_WHO__*/
    AND wh.BlkBy > 0
ORDER BY wh.ElapsedMS DESC, wh.DateTime DESC;',
    1, SYSUTCDATETIME(), N'InsertHistorySamples');

-- --- Group 4: Wait Statistics ------------------------------------------------

INSERT INTO [SQLGig].[DataBOT].[QuestionSamples]
    ([Environment],[GroupKey],[GroupTitle],[GroupOrder],[QuestionText],[QuestionOrder],[Tags],[Script],[IsActive],[UpdatedAt],[UpdatedBy])
VALUES (N'SqlServer_History', N'WaitAnalysis', N'Wait Statistics', 4,
    N'What are the top wait types? Show wait trends', 1,
    N'Waits,WaitType,Performance,Bottleneck',
    N'DECLARE @FromUtc datetime2(0) = DATEADD(HOUR, -24, SYSUTCDATETIME());
DECLARE @ToUtc   datetime2(0) = SYSUTCDATETIME();
DECLARE @Top     int          = 5000;

DECLARE @SpanMin int = DATEDIFF(MINUTE, @FromUtc, @ToUtc);
DECLARE @BucketMin int = CASE
    WHEN @SpanMin <= 120   THEN 1
    WHEN @SpanMin <= 1440  THEN 5
    WHEN @SpanMin <= 10080 THEN 30
    WHEN @SpanMin <= 43200 THEN 60
    ELSE 1440 END;

;WITH Bucketed AS (
    SELECT
        w.SQLServer,
        DATEADD(MINUTE, (DATEDIFF(MINUTE, 0, w.DateTime) / @BucketMin) * @BucketMin, 0) AS TimeBucket,
        w.WaitType,
        SUM(CAST(w.WaitTime_Secs AS float))                AS TotalWaitSecs,
        AVG(CAST(w.Percentage AS float))                    AS AvgPct,
        SUM(CAST(w.ResourceWaitTime_Secs AS float))         AS TotalResourceSecs,
        SUM(CAST(w.SignalWaitTime_Secs AS float))           AS TotalSignalSecs,
        SUM(w.Count)                                        AS TotalCount,
        MAX(w.WaitsGroup)                                   AS WaitsGroup
    FROM [SQLGig].[Monitor].[SQLServer_WaitStats_History] w
    WHERE w.DateTime >= @FromUtc AND w.DateTime < @ToUtc
        /*__SQLSERVER_FILTER_WAITS__*/
    GROUP BY w.SQLServer, w.WaitType,
             DATEADD(MINUTE, (DATEDIFF(MINUTE, 0, w.DateTime) / @BucketMin) * @BucketMin, 0)
)
SELECT TOP (@Top)
    b.SQLServer COLLATE DATABASE_DEFAULT                      AS [ServerName],
    b.TimeBucket                                              AS [CapturedAtUtc],
    N''Waits'' COLLATE DATABASE_DEFAULT                        AS [MetricGroup],
    b.WaitType COLLATE DATABASE_DEFAULT                        AS [MetricName],
    CAST(b.TotalWaitSecs AS decimal(18,2))                    AS [MetricValue],
    N''Pct='' + CAST(CAST(b.AvgPct AS decimal(5,1)) AS nvarchar(20))
    + N''%; Resource='' + CAST(CAST(b.TotalResourceSecs AS decimal(18,1)) AS nvarchar(20)) + N''s''
    + N''; Signal='' + CAST(CAST(b.TotalSignalSecs AS decimal(18,1)) AS nvarchar(20)) + N''s''
    + N''; Count='' + CAST(b.TotalCount AS nvarchar(20))
    + N''; Group='' + ISNULL(b.WaitsGroup, N''?'')
    COLLATE DATABASE_DEFAULT                                   AS [Detail]
FROM Bucketed b
ORDER BY b.TotalWaitSecs DESC, b.AvgPct DESC;',
    1, SYSUTCDATETIME(), N'InsertHistorySamples');

-- --- Group 5: Database Health ------------------------------------------------

INSERT INTO [SQLGig].[DataBOT].[QuestionSamples]
    ([Environment],[GroupKey],[GroupTitle],[GroupOrder],[QuestionText],[QuestionOrder],[Tags],[Script],[IsActive],[UpdatedAt],[UpdatedBy])
VALUES (N'SqlServer_History', N'DatabaseHealth', N'Database Health', 5,
    N'Any databases offline, suspect, or with high log usage?', 1,
    N'Database,Offline,Suspect,LogUsage,Health',
    N'DECLARE @FromUtc datetime2(0) = DATEADD(HOUR, -24, SYSUTCDATETIME());
DECLARE @ToUtc   datetime2(0) = SYSUTCDATETIME();
DECLARE @Top     int          = 5000;

SELECT TOP (@Top)
    h.SQLServer COLLATE DATABASE_DEFAULT                      AS [ServerName],
    h.DateTime                                                AS [CapturedAtUtc],
    N''DatabaseHealth'' COLLATE DATABASE_DEFAULT               AS [MetricGroup],
    h.[Database] COLLATE DATABASE_DEFAULT                      AS [MetricName],
    CAST(h.LogFileUsagePercent AS decimal(18,2))              AS [MetricValue],
    N''Status='' + ISNULL(h.Status, N''?'')
    + N''; Recovery='' + ISNULL(h.Recovery, N''?'')
    + N''; SizeMB='' + CAST(h.DatabaseSizeMB AS nvarchar(20))
    + N''; LogMB='' + CAST(h.LogFileUsageMB AS nvarchar(20))
    + N''; LogPct='' + CAST(h.LogFileUsagePercent AS nvarchar(20)) + N''%''
    + N''; Connections='' + CAST(h.ActiveConnections AS nvarchar(20))
    + N''; TxnSec='' + CAST(h.TransactionsPerSec AS nvarchar(20))
    COLLATE DATABASE_DEFAULT                                   AS [Detail]
FROM [SQLGig].[Monitor].[SQLServer_DatabaseHealth_History] h
WHERE h.DateTime >= @FromUtc AND h.DateTime < @ToUtc
    /*__SQLSERVER_FILTER__*/
    AND (h.Status NOT IN (N''ONLINE'', N''1'')
         OR h.LogFileUsagePercent > 80
         OR h.DatabaseSizeMB > 10240)
ORDER BY
    CASE WHEN h.Status NOT IN (N''ONLINE'', N''1'') THEN 0 ELSE 1 END,
    h.LogFileUsagePercent DESC, h.DateTime DESC;',
    1, SYSUTCDATETIME(), N'InsertHistorySamples');

INSERT INTO [SQLGig].[DataBOT].[QuestionSamples]
    ([Environment],[GroupKey],[GroupTitle],[GroupOrder],[QuestionText],[QuestionOrder],[Tags],[Script],[IsActive],[UpdatedAt],[UpdatedBy])
VALUES (N'SqlServer_History', N'DatabaseHealth', N'Database Health', 5,
    N'Show database size growth trend over the last 7 days', 2,
    N'Database,Size,Growth,Trend,Capacity',
    N'DECLARE @FromUtc datetime2(0) = DATEADD(DAY, -7, SYSUTCDATETIME());
DECLARE @ToUtc   datetime2(0) = SYSUTCDATETIME();
DECLARE @Top     int          = 5000;

DECLARE @SpanMin int = DATEDIFF(MINUTE, @FromUtc, @ToUtc);
DECLARE @BucketMin int = CASE
    WHEN @SpanMin <= 1440  THEN 60
    WHEN @SpanMin <= 10080 THEN 360
    WHEN @SpanMin <= 43200 THEN 1440
    ELSE 1440 END;

;WITH Bucketed AS (
    SELECT
        h.SQLServer,
        h.[Database],
        DATEADD(MINUTE, (DATEDIFF(MINUTE, 0, h.DateTime) / @BucketMin) * @BucketMin, 0) AS TimeBucket,
        MAX(h.DatabaseSizeMB)                             AS MaxSizeMB,
        MAX(h.LogFileUsageMB)                             AS MaxLogMB
    FROM [SQLGig].[Monitor].[SQLServer_DatabaseHealth_History] h
    WHERE h.DateTime >= @FromUtc AND h.DateTime < @ToUtc
        /*__SQLSERVER_FILTER__*/
    GROUP BY h.SQLServer, h.[Database],
             DATEADD(MINUTE, (DATEDIFF(MINUTE, 0, h.DateTime) / @BucketMin) * @BucketMin, 0)
)
SELECT TOP (@Top)
    SQLServer COLLATE DATABASE_DEFAULT                        AS [ServerName],
    TimeBucket                                                AS [CapturedAtUtc],
    N''DatabaseGrowth'' COLLATE DATABASE_DEFAULT               AS [MetricGroup],
    [Database] COLLATE DATABASE_DEFAULT                        AS [MetricName],
    CAST(MaxSizeMB AS decimal(18,2))                          AS [MetricValue],
    N''SizeMB='' + CAST(MaxSizeMB AS nvarchar(20))
    + N''; LogMB='' + CAST(MaxLogMB AS nvarchar(20))
    COLLATE DATABASE_DEFAULT                                   AS [Detail]
FROM Bucketed
ORDER BY SQLServer, [Database], TimeBucket;',
    1, SYSUTCDATETIME(), N'InsertHistorySamples');

-- --- Group 6: Backup & Job Failures -----------------------------------------

INSERT INTO [SQLGig].[DataBOT].[QuestionSamples]
    ([Environment],[GroupKey],[GroupTitle],[GroupOrder],[QuestionText],[QuestionOrder],[Tags],[Script],[IsActive],[UpdatedAt],[UpdatedBy])
VALUES (N'SqlServer_History', N'BackupJobs', N'Backup & Job Failures', 6,
    N'Show failed backup and SQL Agent job history', 1,
    N'Backup,FailedJob,Agent,Recovery',
    N'DECLARE @FromUtc datetime2(0) = DATEADD(HOUR, -24, SYSUTCDATETIME());
DECLARE @ToUtc   datetime2(0) = SYSUTCDATETIME();
DECLARE @Top     int          = 5000;

DECLARE @SpanMin int = DATEDIFF(MINUTE, @FromUtc, @ToUtc);
DECLARE @BucketMin int = CASE
    WHEN @SpanMin <= 120   THEN 1
    WHEN @SpanMin <= 1440  THEN 5
    WHEN @SpanMin <= 10080 THEN 30
    WHEN @SpanMin <= 43200 THEN 60
    ELSE 1440 END;

;WITH Bucketed AS (
    SELECT
        h.SQLServer,
        DATEADD(MINUTE, (DATEDIFF(MINUTE, 0, h.DateTime) / @BucketMin) * @BucketMin, 0) AS TimeBucket,
        MAX(h.FailedJobCount)           AS MaxFailedJobs,
        MAX(h.FailedFullBackupCount)    AS MaxFailedFull,
        MAX(h.FailedDiffBackupCount)    AS MaxFailedDiff,
        MAX(h.FailedLogBackupCount)     AS MaxFailedLog
    FROM [SQLGig].[Monitor].[SQLServer_Details_History] h
    WHERE h.DateTime >= @FromUtc AND h.DateTime < @ToUtc
        /*__SQLSERVER_FILTER__*/
    GROUP BY h.SQLServer,
             DATEADD(MINUTE, (DATEDIFF(MINUTE, 0, h.DateTime) / @BucketMin) * @BucketMin, 0)
    HAVING MAX(h.FailedJobCount) > 0 OR MAX(h.FailedFullBackupCount) > 0
        OR MAX(h.FailedDiffBackupCount) > 0 OR MAX(h.FailedLogBackupCount) > 0
)
SELECT TOP (@Top)
    b.SQLServer COLLATE DATABASE_DEFAULT                     AS [ServerName],
    b.TimeBucket                                             AS [CapturedAtUtc],
    N''BackupJobs'' COLLATE DATABASE_DEFAULT                   AS [MetricGroup],
    CASE
        WHEN b.MaxFailedFull > 0 THEN N''FailedFullBackup''
        WHEN b.MaxFailedDiff > 0 THEN N''FailedDiffBackup''
        WHEN b.MaxFailedLog > 0  THEN N''FailedLogBackup''
        ELSE N''FailedJob''
    END COLLATE DATABASE_DEFAULT                              AS [MetricName],
    CAST(b.MaxFailedJobs + b.MaxFailedFull
         + b.MaxFailedDiff + b.MaxFailedLog
         AS decimal(18,2))                                    AS [MetricValue],
    N''Jobs='' + CAST(b.MaxFailedJobs AS nvarchar(20))
    + N''; Full='' + CAST(b.MaxFailedFull AS nvarchar(20))
    + N''; Diff='' + CAST(b.MaxFailedDiff AS nvarchar(20))
    + N''; Log='' + CAST(b.MaxFailedLog AS nvarchar(20))
    COLLATE DATABASE_DEFAULT                                   AS [Detail]
FROM Bucketed b
ORDER BY b.TimeBucket DESC;',
    1, SYSUTCDATETIME(), N'InsertHistorySamples');

-- --- Group 7: Session Activity -----------------------------------------------

INSERT INTO [SQLGig].[DataBOT].[QuestionSamples]
    ([Environment],[GroupKey],[GroupTitle],[GroupOrder],[QuestionText],[QuestionOrder],[Tags],[Script],[IsActive],[UpdatedAt],[UpdatedBy])
VALUES (N'SqlServer_History', N'SessionActivity', N'Session Activity', 7,
    N'Show heavy queries - top sessions by CPU and elapsed time', 1,
    N'Sessions,CPU,LongRunning,Queries,WhoIsThere',
    N'DECLARE @FromUtc datetime2(0) = DATEADD(HOUR, -24, SYSUTCDATETIME());
DECLARE @ToUtc   datetime2(0) = SYSUTCDATETIME();
DECLARE @Top     int          = 5000;

SELECT TOP (@Top)
    wh.SQLServer COLLATE DATABASE_DEFAULT                     AS [ServerName],
    wh.DateTime                                               AS [CapturedAtUtc],
    N''Sessions'' COLLATE DATABASE_DEFAULT                     AS [MetricGroup],
    N''SPID '' + CAST(wh.SPID AS nvarchar(20))
    COLLATE DATABASE_DEFAULT                                   AS [MetricName],
    CAST(wh.CPU AS decimal(18,2))                             AS [MetricValue],
    N''ElapsedMs='' + CAST(wh.ElapsedMS AS nvarchar(20))
    + N''; CPU='' + CAST(wh.CPU AS nvarchar(20))
    + N''; Reads='' + CAST(wh.IOReads AS nvarchar(20))
    + N''; Writes='' + CAST(wh.IOWrites AS nvarchar(20))
    + N''; DB='' + ISNULL(wh.DBName, N''?'')
    + N''; Login='' + ISNULL(wh.Login, N''?'')
    + N''; Host='' + ISNULL(wh.Host, N''?'')
    + N''; Status='' + ISNULL(wh.Status, N''?'')
    + N''; Wait='' + ISNULL(wh.LastWaitType, N''?'')
    + N''; SQL='' + LEFT(ISNULL(REPLACE(REPLACE(wh.SQLStatement_900, CHAR(13), N'' ''), CHAR(10), N'' ''), N''''), 300)
    COLLATE DATABASE_DEFAULT                                   AS [Detail]
FROM [SQLGig].[Monitor].[SQLServer_WhoIsThere_History] wh
WHERE wh.DateTime >= @FromUtc AND wh.DateTime < @ToUtc
    /*__SQLSERVER_FILTER_WHO__*/
    AND wh.Status NOT IN (N''sleeping'', N''background'')
ORDER BY wh.CPU DESC, wh.ElapsedMS DESC;',
    1, SYSUTCDATETIME(), N'InsertHistorySamples');

INSERT INTO [SQLGig].[DataBOT].[QuestionSamples]
    ([Environment],[GroupKey],[GroupTitle],[GroupOrder],[QuestionText],[QuestionOrder],[Tags],[Script],[IsActive],[UpdatedAt],[UpdatedBy])
VALUES (N'SqlServer_History', N'SessionActivity', N'Session Activity', 7,
    N'Show CPU and workload trend - batch requests, connections, transactions', 2,
    N'CPU,Workload,BatchRequests,Connections,Trend',
    N'DECLARE @FromUtc datetime2(0) = DATEADD(HOUR, -24, SYSUTCDATETIME());
DECLARE @ToUtc   datetime2(0) = SYSUTCDATETIME();
DECLARE @Top     int          = 5000;

DECLARE @SpanMin int = DATEDIFF(MINUTE, @FromUtc, @ToUtc);
DECLARE @BucketMin int = CASE
    WHEN @SpanMin <= 120   THEN 1
    WHEN @SpanMin <= 1440  THEN 5
    WHEN @SpanMin <= 10080 THEN 30
    WHEN @SpanMin <= 43200 THEN 60
    ELSE 1440 END;

;WITH Bucketed AS (
    SELECT
        h.SQLServer,
        DATEADD(MINUTE, (DATEDIFF(MINUTE, 0, h.DateTime) / @BucketMin) * @BucketMin, 0) AS TimeBucket,
        AVG(CAST(h.BatchRequests_sec AS float))    AS AvgBatchReq,
        AVG(CAST(h.Transactions_sec AS float))     AS AvgTxn,
        AVG(CAST(h.UserConnections AS float))      AS AvgUsers
    FROM [SQLGig].[Monitor].[SQLServer_Details_History] h
    WHERE h.DateTime >= @FromUtc AND h.DateTime < @ToUtc
        /*__SQLSERVER_FILTER__*/
    GROUP BY h.SQLServer,
             DATEADD(MINUTE, (DATEDIFF(MINUTE, 0, h.DateTime) / @BucketMin) * @BucketMin, 0)
)
SELECT TOP (@Top)
    v.ServerName COLLATE DATABASE_DEFAULT AS [ServerName],
    v.CapturedAtUtc,
    v.MetricGroup COLLATE DATABASE_DEFAULT AS [MetricGroup],
    v.MetricName COLLATE DATABASE_DEFAULT AS [MetricName],
    v.MetricValue,
    v.Detail COLLATE DATABASE_DEFAULT AS [Detail]
FROM Bucketed b
CROSS APPLY (
    SELECT b.SQLServer, b.TimeBucket, N''Workload'', N''BatchRequests'',
           CAST(b.AvgBatchReq AS decimal(18,2)),
           N''BatchReq/s='' + CAST(CAST(b.AvgBatchReq AS int) AS nvarchar(20))
    UNION ALL
    SELECT b.SQLServer, b.TimeBucket, N''Workload'', N''Transactions'',
           CAST(b.AvgTxn AS decimal(18,2)),
           N''Txn/s='' + CAST(CAST(b.AvgTxn AS int) AS nvarchar(20))
    UNION ALL
    SELECT b.SQLServer, b.TimeBucket, N''Workload'', N''UserConnections'',
           CAST(b.AvgUsers AS decimal(18,2)),
           N''Users='' + CAST(CAST(b.AvgUsers AS int) AS nvarchar(20))
) v(ServerName, CapturedAtUtc, MetricGroup, MetricName, MetricValue, Detail)
ORDER BY v.ServerName, v.CapturedAtUtc, v.MetricName;',
    1, SYSUTCDATETIME(), N'InsertHistorySamples');

-- --- Group 8: Compilation & Execution ----------------------------------------

INSERT INTO [SQLGig].[DataBOT].[QuestionSamples]
    ([Environment],[GroupKey],[GroupTitle],[GroupOrder],[QuestionText],[QuestionOrder],[Tags],[Script],[IsActive],[UpdatedAt],[UpdatedBy])
VALUES (N'SqlServer_History', N'CompilationExec', N'Compilation & Execution', 8,
    N'Show SQL compilations and recompilations trend - plan cache efficiency', 1,
    N'Compilations,Recompilations,PlanCache,Performance',
    N'DECLARE @FromUtc datetime2(0) = DATEADD(HOUR, -24, SYSUTCDATETIME());
DECLARE @ToUtc   datetime2(0) = SYSUTCDATETIME();
DECLARE @Top     int          = 5000;

DECLARE @SpanMin int = DATEDIFF(MINUTE, @FromUtc, @ToUtc);
DECLARE @BucketMin int = CASE
    WHEN @SpanMin <= 120   THEN 1
    WHEN @SpanMin <= 1440  THEN 5
    WHEN @SpanMin <= 10080 THEN 30
    WHEN @SpanMin <= 43200 THEN 60
    ELSE 1440 END;

;WITH Bucketed AS (
    SELECT
        h.SQLServer,
        DATEADD(MINUTE, (DATEDIFF(MINUTE, 0, h.DateTime) / @BucketMin) * @BucketMin, 0) AS TimeBucket,
        AVG(CAST(h.SQLCompilations_sec AS float))      AS AvgCompilations,
        AVG(CAST(h.SQLReCompilations_sec AS float))    AS AvgRecompilations,
        AVG(CAST(h.BatchRequests_sec AS float))        AS AvgBatchReq
    FROM [SQLGig].[Monitor].[SQLServer_Details_History] h
    WHERE h.DateTime >= @FromUtc AND h.DateTime < @ToUtc
        /*__SQLSERVER_FILTER__*/
    GROUP BY h.SQLServer,
             DATEADD(MINUTE, (DATEDIFF(MINUTE, 0, h.DateTime) / @BucketMin) * @BucketMin, 0)
)
SELECT TOP (@Top)
    v.ServerName COLLATE DATABASE_DEFAULT AS [ServerName],
    v.CapturedAtUtc,
    v.MetricGroup COLLATE DATABASE_DEFAULT AS [MetricGroup],
    v.MetricName COLLATE DATABASE_DEFAULT AS [MetricName],
    v.MetricValue,
    v.Detail COLLATE DATABASE_DEFAULT AS [Detail]
FROM Bucketed b
CROSS APPLY (
    SELECT b.SQLServer, b.TimeBucket, N''Compilation'', N''Compilations'',
           CAST(b.AvgCompilations AS decimal(18,2)),
           N''Compilations/s='' + CAST(CAST(b.AvgCompilations AS int) AS nvarchar(20)) + N''; BatchReq/s='' + CAST(CAST(b.AvgBatchReq AS int) AS nvarchar(20))
    UNION ALL
    SELECT b.SQLServer, b.TimeBucket, N''Compilation'', N''Recompilations'',
           CAST(b.AvgRecompilations AS decimal(18,2)),
           N''Recompilations/s='' + CAST(CAST(b.AvgRecompilations AS int) AS nvarchar(20))
) v(ServerName, CapturedAtUtc, MetricGroup, MetricName, MetricValue, Detail)
ORDER BY v.ServerName, v.CapturedAtUtc, v.MetricName;',
    1, SYSUTCDATETIME(), N'InsertHistorySamples');

INSERT INTO [SQLGig].[DataBOT].[QuestionSamples]
    ([Environment],[GroupKey],[GroupTitle],[GroupOrder],[QuestionText],[QuestionOrder],[Tags],[Script],[IsActive],[UpdatedAt],[UpdatedBy])
VALUES (N'SqlServer_History', N'CompilationExec', N'Compilation & Execution', 8,
    N'Show buffer cache page reads, writes and checkpoint trend', 2,
    N'PageReads,PageWrites,Checkpoint,BufferCache,IO',
    N'DECLARE @FromUtc datetime2(0) = DATEADD(HOUR, -24, SYSUTCDATETIME());
DECLARE @ToUtc   datetime2(0) = SYSUTCDATETIME();
DECLARE @Top     int          = 5000;

DECLARE @SpanMin int = DATEDIFF(MINUTE, @FromUtc, @ToUtc);
DECLARE @BucketMin int = CASE
    WHEN @SpanMin <= 120   THEN 1
    WHEN @SpanMin <= 1440  THEN 5
    WHEN @SpanMin <= 10080 THEN 30
    WHEN @SpanMin <= 43200 THEN 60
    ELSE 1440 END;

;WITH Bucketed AS (
    SELECT
        h.SQLServer,
        DATEADD(MINUTE, (DATEDIFF(MINUTE, 0, h.DateTime) / @BucketMin) * @BucketMin, 0) AS TimeBucket,
        AVG(CAST(h.PageReads_sec AS float))        AS AvgPageReads,
        AVG(CAST(h.PageWrites_sec AS float))       AS AvgPageWrites,
        AVG(CAST(h.CheckpointPages_sec AS float))  AS AvgCheckpoint,
        AVG(CAST(h.BufferCacheHitRatio AS float))  AS AvgCacheHit
    FROM [SQLGig].[Monitor].[SQLServer_Details_History] h
    WHERE h.DateTime >= @FromUtc AND h.DateTime < @ToUtc
        /*__SQLSERVER_FILTER__*/
    GROUP BY h.SQLServer,
             DATEADD(MINUTE, (DATEDIFF(MINUTE, 0, h.DateTime) / @BucketMin) * @BucketMin, 0)
)
SELECT TOP (@Top)
    v.ServerName COLLATE DATABASE_DEFAULT AS [ServerName],
    v.CapturedAtUtc,
    v.MetricGroup COLLATE DATABASE_DEFAULT AS [MetricGroup],
    v.MetricName COLLATE DATABASE_DEFAULT AS [MetricName],
    v.MetricValue,
    v.Detail COLLATE DATABASE_DEFAULT AS [Detail]
FROM Bucketed b
CROSS APPLY (
    SELECT b.SQLServer, b.TimeBucket, N''BufferIO'', N''PageReads'',
           CAST(b.AvgPageReads AS decimal(18,2)),
           N''PageReads/s='' + CAST(CAST(b.AvgPageReads AS int) AS nvarchar(20)) + N''; CacheHit='' + CAST(CAST(b.AvgCacheHit AS decimal(5,1)) AS nvarchar(20)) + N''%''
    UNION ALL
    SELECT b.SQLServer, b.TimeBucket, N''BufferIO'', N''PageWrites'',
           CAST(b.AvgPageWrites AS decimal(18,2)),
           N''PageWrites/s='' + CAST(CAST(b.AvgPageWrites AS int) AS nvarchar(20))
    UNION ALL
    SELECT b.SQLServer, b.TimeBucket, N''BufferIO'', N''CheckpointPages'',
           CAST(b.AvgCheckpoint AS decimal(18,2)),
           N''Checkpoint/s='' + CAST(CAST(b.AvgCheckpoint AS int) AS nvarchar(20))
) v(ServerName, CapturedAtUtc, MetricGroup, MetricName, MetricValue, Detail)
ORDER BY v.ServerName, v.CapturedAtUtc, v.MetricName;',
    1, SYSUTCDATETIME(), N'InsertHistorySamples');

-- --- Group 9: Alerts & Errors ------------------------------------------------

INSERT INTO [SQLGig].[DataBOT].[QuestionSamples]
    ([Environment],[GroupKey],[GroupTitle],[GroupOrder],[QuestionText],[QuestionOrder],[Tags],[Script],[IsActive],[UpdatedAt],[UpdatedBy])
VALUES (N'SqlServer_History', N'AlertsErrors', N'Alerts & Errors', 9,
    N'Show recent critical alerts and error events', 1,
    N'Alerts,Errors,Events,Critical',
    N'DECLARE @FromUtc datetime2(0) = DATEADD(HOUR, -24, SYSUTCDATETIME());
DECLARE @ToUtc   datetime2(0) = SYSUTCDATETIME();
DECLARE @Top     int          = 5000;

SELECT TOP (@Top)
    a.Server COLLATE DATABASE_DEFAULT                         AS [ServerName],
    a.DateTime                                                AS [CapturedAtUtc],
    N''Alerts'' COLLATE DATABASE_DEFAULT                       AS [MetricGroup],
    CAST(a.EventID AS nvarchar(20)) + N'': '' + ISNULL(a.Source, N''?'')
    COLLATE DATABASE_DEFAULT                                   AS [MetricName],
    CAST(a.Count AS decimal(18,2))                            AS [MetricValue],
    N''Type='' + ISNULL(a.Type, N''?'')
    + N''; Level='' + ISNULL(a.Level, N''?'')
    + N''; Log='' + ISNULL(a.LogName, N''?'')
    + N''; Count='' + CAST(a.Count AS nvarchar(20))
    + N''; Msg='' + LEFT(ISNULL(a.MessageLeft200, a.Message), 300)
    COLLATE DATABASE_DEFAULT                                   AS [Detail]
FROM [SQLGig].[Monitor].[Alerts] a
WHERE a.DateTime >= @FromUtc AND a.DateTime < @ToUtc
    /*__ALERT_SERVER_FILTER__*/
    AND a.Type = N''SQL''
ORDER BY a.Count DESC, a.DateTime DESC;',
    1, SYSUTCDATETIME(), N'InsertHistorySamples');


-- =============================================================================
-- ###  Windows_History                                                      ###
-- =============================================================================

-- --- Group 1: CPU & Memory ---------------------------------------------------

INSERT INTO [SQLGig].[DataBOT].[QuestionSamples]
    ([Environment],[GroupKey],[GroupTitle],[GroupOrder],[QuestionText],[QuestionOrder],[Tags],[Script],[IsActive],[UpdatedAt],[UpdatedBy])
VALUES (N'Windows_History', N'OSPerformance', N'CPU & Memory', 1,
    N'Show CPU and memory usage trend over time', 1,
    N'CPU,Memory,Performance,Trend,Overview',
    N'DECLARE @FromUtc datetime2(0) = DATEADD(HOUR, -24, SYSUTCDATETIME());
DECLARE @ToUtc   datetime2(0) = SYSUTCDATETIME();
DECLARE @Top     int          = 5000;

DECLARE @SpanMin int = DATEDIFF(MINUTE, @FromUtc, @ToUtc);
DECLARE @BucketMin int = CASE
    WHEN @SpanMin <= 120   THEN 1
    WHEN @SpanMin <= 1440  THEN 5
    WHEN @SpanMin <= 10080 THEN 30
    WHEN @SpanMin <= 43200 THEN 60
    ELSE 1440 END;

;WITH Bucketed AS (
    SELECT
        h.WinServer,
        DATEADD(MINUTE, (DATEDIFF(MINUTE, 0, h.DateTime) / @BucketMin) * @BucketMin, 0) AS TimeBucket,
        AVG(CAST(h.PercentProcessorTime AS float))  AS AvgCPU,
        AVG(CAST(h.AvailableMemory AS float))        AS AvgAvailMem,
        AVG(CAST(h.MemoryUsage AS float))            AS AvgMemUsage,
        AVG(CAST(ISNULL(h.ProcessorQueueLength,0) AS float)) AS AvgQueue,
        AVG(CAST(ISNULL(h.ProcessCount,0) AS float)) AS AvgProcesses
    FROM [SQLGig].[Monitor].[WINServer_Details_History] h
    WHERE h.DateTime >= @FromUtc AND h.DateTime < @ToUtc
        /*__WINSERVER_FILTER__*/
    GROUP BY h.WinServer,
             DATEADD(MINUTE, (DATEDIFF(MINUTE, 0, h.DateTime) / @BucketMin) * @BucketMin, 0)
)
SELECT TOP (@Top)
    v.ServerName COLLATE DATABASE_DEFAULT AS [ServerName],
    v.CapturedAtUtc,
    v.MetricGroup COLLATE DATABASE_DEFAULT AS [MetricGroup],
    v.MetricName COLLATE DATABASE_DEFAULT AS [MetricName],
    v.MetricValue,
    v.Detail COLLATE DATABASE_DEFAULT AS [Detail]
FROM Bucketed b
CROSS APPLY (
    SELECT b.WinServer, b.TimeBucket, N''OSPerformance'', N''CPU'',
           CAST(b.AvgCPU AS decimal(18,2)),
           N''CPU='' + CAST(CAST(b.AvgCPU AS decimal(5,1)) AS nvarchar(20)) + N''%; Queue='' + CAST(CAST(b.AvgQueue AS decimal(5,1)) AS nvarchar(20))
    UNION ALL
    SELECT b.WinServer, b.TimeBucket, N''OSPerformance'', N''MemoryUsage'',
           CAST(b.AvgMemUsage AS decimal(18,2)),
           N''MemUsage='' + CAST(CAST(b.AvgMemUsage AS decimal(5,1)) AS nvarchar(20)) + N''%; AvailGB='' + CAST(CAST(b.AvgAvailMem AS decimal(10,1)) AS nvarchar(20))
) v(ServerName, CapturedAtUtc, MetricGroup, MetricName, MetricValue, Detail)
ORDER BY v.ServerName, v.CapturedAtUtc, v.MetricName;',
    1, SYSUTCDATETIME(), N'InsertHistorySamples');

INSERT INTO [SQLGig].[DataBOT].[QuestionSamples]
    ([Environment],[GroupKey],[GroupTitle],[GroupOrder],[QuestionText],[QuestionOrder],[Tags],[Script],[IsActive],[UpdatedAt],[UpdatedBy])
VALUES (N'Windows_History', N'OSPerformance', N'CPU & Memory', 1,
    N'Is the server running out of memory? Show memory pressure trend', 2,
    N'Memory,Pressure,PageFaults,Available,Committed',
    N'DECLARE @FromUtc datetime2(0) = DATEADD(HOUR, -24, SYSUTCDATETIME());
DECLARE @ToUtc   datetime2(0) = SYSUTCDATETIME();
DECLARE @Top     int          = 5000;

DECLARE @SpanMin int = DATEDIFF(MINUTE, @FromUtc, @ToUtc);
DECLARE @BucketMin int = CASE
    WHEN @SpanMin <= 120   THEN 1
    WHEN @SpanMin <= 1440  THEN 5
    WHEN @SpanMin <= 10080 THEN 30
    WHEN @SpanMin <= 43200 THEN 60
    ELSE 1440 END;

;WITH Bucketed AS (
    SELECT
        h.WinServer,
        DATEADD(MINUTE, (DATEDIFF(MINUTE, 0, h.DateTime) / @BucketMin) * @BucketMin, 0) AS TimeBucket,
        AVG(CAST(h.AvailableMemory AS float))       AS AvgAvailGB,
        AVG(CAST(h.MemoryUsage AS float))            AS AvgMemUsage,
        AVG(CAST(ISNULL(h.PageFaultsPerSec,0) AS float)) AS AvgPageFaults,
        AVG(CAST(ISNULL(h.Committed,0) AS float))   AS AvgCommitted,
        AVG(CAST(ISNULL(h.Cached,0) AS float))      AS AvgCached
    FROM [SQLGig].[Monitor].[WINServer_Details_History] h
    WHERE h.DateTime >= @FromUtc AND h.DateTime < @ToUtc
        /*__WINSERVER_FILTER__*/
    GROUP BY h.WinServer,
             DATEADD(MINUTE, (DATEDIFF(MINUTE, 0, h.DateTime) / @BucketMin) * @BucketMin, 0)
)
SELECT TOP (@Top)
    v.ServerName COLLATE DATABASE_DEFAULT AS [ServerName],
    v.CapturedAtUtc,
    v.MetricGroup COLLATE DATABASE_DEFAULT AS [MetricGroup],
    v.MetricName COLLATE DATABASE_DEFAULT AS [MetricName],
    v.MetricValue,
    v.Detail COLLATE DATABASE_DEFAULT AS [Detail]
FROM Bucketed b
CROSS APPLY (
    SELECT b.WinServer, b.TimeBucket, N''Memory'', N''AvailableMemoryGB'',
           CAST(b.AvgAvailGB AS decimal(18,2)),
           N''AvailGB='' + CAST(CAST(b.AvgAvailGB AS decimal(10,1)) AS nvarchar(20)) + N''; Committed='' + CAST(CAST(b.AvgCommitted AS decimal(10,1)) AS nvarchar(20)) + N'' GB''
    UNION ALL
    SELECT b.WinServer, b.TimeBucket, N''Memory'', N''MemoryUsage'',
           CAST(b.AvgMemUsage AS decimal(18,2)),
           N''MemUsage='' + CAST(CAST(b.AvgMemUsage AS decimal(5,1)) AS nvarchar(20)) + N''%; Cached='' + CAST(CAST(b.AvgCached AS decimal(10,1)) AS nvarchar(20)) + N'' GB''
    UNION ALL
    SELECT b.WinServer, b.TimeBucket, N''Memory'', N''PageFaultsPerSec'',
           CAST(b.AvgPageFaults AS decimal(18,2)),
           N''PageFaults/s='' + CAST(CAST(b.AvgPageFaults AS int) AS nvarchar(20))
) v(ServerName, CapturedAtUtc, MetricGroup, MetricName, MetricValue, Detail)
ORDER BY v.ServerName, v.CapturedAtUtc, v.MetricName;',
    1, SYSUTCDATETIME(), N'InsertHistorySamples');

-- --- Group 2: Disk & Storage -------------------------------------------------

INSERT INTO [SQLGig].[DataBOT].[QuestionSamples]
    ([Environment],[GroupKey],[GroupTitle],[GroupOrder],[QuestionText],[QuestionOrder],[Tags],[Script],[IsActive],[UpdatedAt],[UpdatedBy])
VALUES (N'Windows_History', N'DiskStorage', N'Disk & Storage', 2,
    N'Is the disk running slow? Show I/O latency and queue length trend', 1,
    N'Disk,Latency,IO,Queue,Performance',
    N'DECLARE @FromUtc datetime2(0) = DATEADD(HOUR, -24, SYSUTCDATETIME());
DECLARE @ToUtc   datetime2(0) = SYSUTCDATETIME();
DECLARE @Top     int          = 5000;

DECLARE @SpanMin int = DATEDIFF(MINUTE, @FromUtc, @ToUtc);
DECLARE @BucketMin int = CASE
    WHEN @SpanMin <= 120   THEN 1
    WHEN @SpanMin <= 1440  THEN 5
    WHEN @SpanMin <= 10080 THEN 30
    WHEN @SpanMin <= 43200 THEN 60
    ELSE 1440 END;

;WITH Bucketed AS (
    SELECT
        h.WinServer,
        DATEADD(MINUTE, (DATEDIFF(MINUTE, 0, h.DateTime) / @BucketMin) * @BucketMin, 0) AS TimeBucket,
        AVG(CAST(ISNULL(h.PhysicalDiskAvgDiskSecReadMS,0) AS float))  AS AvgReadMs,
        AVG(CAST(ISNULL(h.PhysicalDiskAvgDiskSecWriteMS,0) AS float)) AS AvgWriteMs,
        AVG(CAST(ISNULL(h.AvgDiskQueueLengthTotal,0) AS float))      AS AvgQueueLen
    FROM [SQLGig].[Monitor].[WINServer_Details_History] h
    WHERE h.DateTime >= @FromUtc AND h.DateTime < @ToUtc
        /*__WINSERVER_FILTER__*/
    GROUP BY h.WinServer,
             DATEADD(MINUTE, (DATEDIFF(MINUTE, 0, h.DateTime) / @BucketMin) * @BucketMin, 0)
)
SELECT TOP (@Top)
    v.ServerName COLLATE DATABASE_DEFAULT AS [ServerName],
    v.CapturedAtUtc,
    v.MetricGroup COLLATE DATABASE_DEFAULT AS [MetricGroup],
    v.MetricName COLLATE DATABASE_DEFAULT AS [MetricName],
    v.MetricValue,
    v.Detail COLLATE DATABASE_DEFAULT AS [Detail]
FROM Bucketed b
CROSS APPLY (
    SELECT b.WinServer, b.TimeBucket, N''Disk'', N''DiskReadLatencyMs'',
           CAST(b.AvgReadMs AS decimal(18,2)),
           N''ReadMs='' + CAST(CAST(b.AvgReadMs AS decimal(10,2)) AS nvarchar(20))
    UNION ALL
    SELECT b.WinServer, b.TimeBucket, N''Disk'', N''DiskWriteLatencyMs'',
           CAST(b.AvgWriteMs AS decimal(18,2)),
           N''WriteMs='' + CAST(CAST(b.AvgWriteMs AS decimal(10,2)) AS nvarchar(20))
    UNION ALL
    SELECT b.WinServer, b.TimeBucket, N''Disk'', N''DiskQueueLength'',
           CAST(b.AvgQueueLen AS decimal(18,2)),
           N''QueueLen='' + CAST(CAST(b.AvgQueueLen AS decimal(10,2)) AS nvarchar(20))
) v(ServerName, CapturedAtUtc, MetricGroup, MetricName, MetricValue, Detail)
ORDER BY v.ServerName, v.CapturedAtUtc, v.MetricName;',
    1, SYSUTCDATETIME(), N'InsertHistorySamples');

INSERT INTO [SQLGig].[DataBOT].[QuestionSamples]
    ([Environment],[GroupKey],[GroupTitle],[GroupOrder],[QuestionText],[QuestionOrder],[Tags],[Script],[IsActive],[UpdatedAt],[UpdatedBy])
VALUES (N'Windows_History', N'DiskStorage', N'Disk & Storage', 2,
    N'Show disk throughput - IOPS reads, writes and total bytes trend', 2,
    N'Disk,IOPS,Throughput,Reads,Writes,Bytes',
    N'DECLARE @FromUtc datetime2(0) = DATEADD(HOUR, -24, SYSUTCDATETIME());
DECLARE @ToUtc   datetime2(0) = SYSUTCDATETIME();
DECLARE @Top     int          = 5000;

DECLARE @SpanMin int = DATEDIFF(MINUTE, @FromUtc, @ToUtc);
DECLARE @BucketMin int = CASE
    WHEN @SpanMin <= 120   THEN 1
    WHEN @SpanMin <= 1440  THEN 5
    WHEN @SpanMin <= 10080 THEN 30
    WHEN @SpanMin <= 43200 THEN 60
    ELSE 1440 END;

;WITH Bucketed AS (
    SELECT
        h.WinServer,
        DATEADD(MINUTE, (DATEDIFF(MINUTE, 0, h.DateTime) / @BucketMin) * @BucketMin, 0) AS TimeBucket,
        AVG(CAST(ISNULL(h.DiskReadsPerSecTotal,0) AS float))   AS AvgReadsPS,
        AVG(CAST(ISNULL(h.DiskWritesPerSecTotal,0) AS float))  AS AvgWritesPS,
        AVG(CAST(ISNULL(h.DiskBytesPerSecTotal,0) AS float))   AS AvgBytesPS,
        AVG(CAST(ISNULL(h.PercentageDiskTimeTotal,0) AS float)) AS AvgDiskTimePct
    FROM [SQLGig].[Monitor].[WINServer_Details_History] h
    WHERE h.DateTime >= @FromUtc AND h.DateTime < @ToUtc
        /*__WINSERVER_FILTER__*/
    GROUP BY h.WinServer,
             DATEADD(MINUTE, (DATEDIFF(MINUTE, 0, h.DateTime) / @BucketMin) * @BucketMin, 0)
)
SELECT TOP (@Top)
    v.ServerName COLLATE DATABASE_DEFAULT AS [ServerName],
    v.CapturedAtUtc,
    v.MetricGroup COLLATE DATABASE_DEFAULT AS [MetricGroup],
    v.MetricName COLLATE DATABASE_DEFAULT AS [MetricName],
    v.MetricValue,
    v.Detail COLLATE DATABASE_DEFAULT AS [Detail]
FROM Bucketed b
CROSS APPLY (
    SELECT b.WinServer, b.TimeBucket, N''DiskThroughput'', N''DiskReadsPerSec'',
           CAST(b.AvgReadsPS AS decimal(18,2)),
           N''Reads/s='' + CAST(CAST(b.AvgReadsPS AS int) AS nvarchar(20)) + N''; DiskTime='' + CAST(CAST(b.AvgDiskTimePct AS decimal(5,1)) AS nvarchar(20)) + N''%''
    UNION ALL
    SELECT b.WinServer, b.TimeBucket, N''DiskThroughput'', N''DiskWritesPerSec'',
           CAST(b.AvgWritesPS AS decimal(18,2)),
           N''Writes/s='' + CAST(CAST(b.AvgWritesPS AS int) AS nvarchar(20))
    UNION ALL
    SELECT b.WinServer, b.TimeBucket, N''DiskThroughput'', N''DiskBytesPerSec'',
           CAST(b.AvgBytesPS AS decimal(18,2)),
           N''Bytes/s='' + CAST(CAST(b.AvgBytesPS AS bigint) AS nvarchar(20))
) v(ServerName, CapturedAtUtc, MetricGroup, MetricName, MetricValue, Detail)
ORDER BY v.ServerName, v.CapturedAtUtc, v.MetricName;',
    1, SYSUTCDATETIME(), N'InsertHistorySamples');

-- --- Group 3: Network -------------------------------------------------------

INSERT INTO [SQLGig].[DataBOT].[QuestionSamples]
    ([Environment],[GroupKey],[GroupTitle],[GroupOrder],[QuestionText],[QuestionOrder],[Tags],[Script],[IsActive],[UpdatedAt],[UpdatedBy])
VALUES (N'Windows_History', N'Network', N'Network', 3,
    N'Network throughput and error trend', 1,
    N'Network,Throughput,Bandwidth,Errors',
    N'DECLARE @FromUtc datetime2(0) = DATEADD(HOUR, -24, SYSUTCDATETIME());
DECLARE @ToUtc   datetime2(0) = SYSUTCDATETIME();
DECLARE @Top     int          = 5000;

DECLARE @SpanMin int = DATEDIFF(MINUTE, @FromUtc, @ToUtc);
DECLARE @BucketMin int = CASE
    WHEN @SpanMin <= 120   THEN 1
    WHEN @SpanMin <= 1440  THEN 5
    WHEN @SpanMin <= 10080 THEN 30
    WHEN @SpanMin <= 43200 THEN 60
    ELSE 1440 END;

;WITH Bucketed AS (
    SELECT
        h.WinServer,
        DATEADD(MINUTE, (DATEDIFF(MINUTE, 0, h.DateTime) / @BucketMin) * @BucketMin, 0) AS TimeBucket,
        AVG(CAST(ISNULL(h.NetworkBytesSent_KBPS,0) AS float))     AS AvgSentKBPS,
        AVG(CAST(ISNULL(h.NetworkBytesReceived_KBPS,0) AS float)) AS AvgRecvKBPS,
        MAX(ISNULL(h.PacketsOutboundErrors,0))                     AS MaxOutErrors,
        MAX(ISNULL(h.PacketsReceivedErrors,0))                     AS MaxInErrors
    FROM [SQLGig].[Monitor].[WINServer_Details_History] h
    WHERE h.DateTime >= @FromUtc AND h.DateTime < @ToUtc
        /*__WINSERVER_FILTER__*/
    GROUP BY h.WinServer,
             DATEADD(MINUTE, (DATEDIFF(MINUTE, 0, h.DateTime) / @BucketMin) * @BucketMin, 0)
)
SELECT TOP (@Top)
    v.ServerName COLLATE DATABASE_DEFAULT AS [ServerName],
    v.CapturedAtUtc,
    v.MetricGroup COLLATE DATABASE_DEFAULT AS [MetricGroup],
    v.MetricName COLLATE DATABASE_DEFAULT AS [MetricName],
    v.MetricValue,
    v.Detail COLLATE DATABASE_DEFAULT AS [Detail]
FROM Bucketed b
CROSS APPLY (
    SELECT b.WinServer, b.TimeBucket, N''Network'', N''NetworkSentKBPS'',
           CAST(b.AvgSentKBPS AS decimal(18,2)),
           N''SentKBPS='' + CAST(CAST(b.AvgSentKBPS AS int) AS nvarchar(20))
    UNION ALL
    SELECT b.WinServer, b.TimeBucket, N''Network'', N''NetworkRecvKBPS'',
           CAST(b.AvgRecvKBPS AS decimal(18,2)),
           N''RecvKBPS='' + CAST(CAST(b.AvgRecvKBPS AS int) AS nvarchar(20))
           + N''; OutErrors='' + CAST(b.MaxOutErrors AS nvarchar(20))
           + N''; InErrors='' + CAST(b.MaxInErrors AS nvarchar(20))
) v(ServerName, CapturedAtUtc, MetricGroup, MetricName, MetricValue, Detail)
ORDER BY v.ServerName, v.CapturedAtUtc, v.MetricName;',
    1, SYSUTCDATETIME(), N'InsertHistorySamples');

-- --- Group 4: SQL Server Impact ----------------------------------------------

INSERT INTO [SQLGig].[DataBOT].[QuestionSamples]
    ([Environment],[GroupKey],[GroupTitle],[GroupOrder],[QuestionText],[QuestionOrder],[Tags],[Script],[IsActive],[UpdatedAt],[UpdatedBy])
VALUES (N'Windows_History', N'SQLOnWindows', N'SQL Server Impact', 4,
    N'How much CPU and memory is SQL Server consuming on this Windows host?', 1,
    N'SQL,CPU,Memory,Impact,Resource',
    N'DECLARE @FromUtc datetime2(0) = DATEADD(HOUR, -24, SYSUTCDATETIME());
DECLARE @ToUtc   datetime2(0) = SYSUTCDATETIME();
DECLARE @Top     int          = 5000;

DECLARE @SpanMin int = DATEDIFF(MINUTE, @FromUtc, @ToUtc);
DECLARE @BucketMin int = CASE
    WHEN @SpanMin <= 120   THEN 1
    WHEN @SpanMin <= 1440  THEN 5
    WHEN @SpanMin <= 10080 THEN 30
    WHEN @SpanMin <= 43200 THEN 60
    ELSE 1440 END;

;WITH Bucketed AS (
    SELECT
        h.WinServer,
        DATEADD(MINUTE, (DATEDIFF(MINUTE, 0, h.DateTime) / @BucketMin) * @BucketMin, 0) AS TimeBucket,
        AVG(CAST(ISNULL(h.SqlServerCPU,0) AS float))          AS AvgSqlCPU,
        AVG(CAST(ISNULL(h.PercentProcessorTime,0) AS float))  AS AvgTotalCPU,
        AVG(CAST(ISNULL(h.SQLMemoryUsage,0) AS float))        AS AvgSqlMemGB,
        AVG(CAST(ISNULL(h.AvailableMemory,0) AS float))       AS AvgAvailGB,
        AVG(CAST(ISNULL(h.MemoryUsage,0) AS float))           AS AvgMemUsage
    FROM [SQLGig].[Monitor].[WINServer_Details_History] h
    WHERE h.DateTime >= @FromUtc AND h.DateTime < @ToUtc
        /*__WINSERVER_FILTER__*/
    GROUP BY h.WinServer,
             DATEADD(MINUTE, (DATEDIFF(MINUTE, 0, h.DateTime) / @BucketMin) * @BucketMin, 0)
)
SELECT TOP (@Top)
    v.ServerName COLLATE DATABASE_DEFAULT AS [ServerName],
    v.CapturedAtUtc,
    v.MetricGroup COLLATE DATABASE_DEFAULT AS [MetricGroup],
    v.MetricName COLLATE DATABASE_DEFAULT AS [MetricName],
    v.MetricValue,
    v.Detail COLLATE DATABASE_DEFAULT AS [Detail]
FROM Bucketed b
CROSS APPLY (
    SELECT b.WinServer, b.TimeBucket, N''SQLOnWindows'', N''SqlServerCPU'',
           CAST(b.AvgSqlCPU AS decimal(18,2)),
           N''SqlCPU='' + CAST(CAST(b.AvgSqlCPU AS decimal(5,1)) AS nvarchar(20)) + N''%; TotalCPU='' + CAST(CAST(b.AvgTotalCPU AS decimal(5,1)) AS nvarchar(20)) + N''%''
    UNION ALL
    SELECT b.WinServer, b.TimeBucket, N''SQLOnWindows'', N''TotalCPU'',
           CAST(b.AvgTotalCPU AS decimal(18,2)),
           N''TotalCPU='' + CAST(CAST(b.AvgTotalCPU AS decimal(5,1)) AS nvarchar(20)) + N''%''
    UNION ALL
    SELECT b.WinServer, b.TimeBucket, N''SQLOnWindows'', N''SQLMemoryUsageGB'',
           CAST(b.AvgSqlMemGB AS decimal(18,2)),
           N''SQLMemGB='' + CAST(CAST(b.AvgSqlMemGB AS decimal(10,1)) AS nvarchar(20)) + N''; AvailGB='' + CAST(CAST(b.AvgAvailGB AS decimal(10,1)) AS nvarchar(20)) + N''; MemUsage='' + CAST(CAST(b.AvgMemUsage AS decimal(5,1)) AS nvarchar(20)) + N''%''
) v(ServerName, CapturedAtUtc, MetricGroup, MetricName, MetricValue, Detail)
ORDER BY v.ServerName, v.CapturedAtUtc, v.MetricName;',
    1, SYSUTCDATETIME(), N'InsertHistorySamples');

-- --- Group 5: Process & Handle Health ----------------------------------------

INSERT INTO [SQLGig].[DataBOT].[QuestionSamples]
    ([Environment],[GroupKey],[GroupTitle],[GroupOrder],[QuestionText],[QuestionOrder],[Tags],[Script],[IsActive],[UpdatedAt],[UpdatedBy])
VALUES (N'Windows_History', N'ProcessHealth', N'Process & Handle Health', 5,
    N'Show process count, thread count and handle usage trend - any leaks?', 1,
    N'Process,Thread,Handle,Leak,Health',
    N'DECLARE @FromUtc datetime2(0) = DATEADD(HOUR, -24, SYSUTCDATETIME());
DECLARE @ToUtc   datetime2(0) = SYSUTCDATETIME();
DECLARE @Top     int          = 5000;

DECLARE @SpanMin int = DATEDIFF(MINUTE, @FromUtc, @ToUtc);
DECLARE @BucketMin int = CASE
    WHEN @SpanMin <= 120   THEN 1
    WHEN @SpanMin <= 1440  THEN 5
    WHEN @SpanMin <= 10080 THEN 30
    WHEN @SpanMin <= 43200 THEN 60
    ELSE 1440 END;

;WITH Bucketed AS (
    SELECT
        h.WinServer,
        DATEADD(MINUTE, (DATEDIFF(MINUTE, 0, h.DateTime) / @BucketMin) * @BucketMin, 0) AS TimeBucket,
        AVG(CAST(ISNULL(h.ProcessCount,0) AS float))      AS AvgProcesses,
        AVG(CAST(ISNULL(h.ThreadCount,0) AS float))       AS AvgThreads,
        AVG(CAST(ISNULL(h.TotalHandles,0) AS float))      AS AvgHandles,
        AVG(CAST(ISNULL(h.ProcessorQueueLength,0) AS float)) AS AvgQueue
    FROM [SQLGig].[Monitor].[WINServer_Details_History] h
    WHERE h.DateTime >= @FromUtc AND h.DateTime < @ToUtc
        /*__WINSERVER_FILTER__*/
    GROUP BY h.WinServer,
             DATEADD(MINUTE, (DATEDIFF(MINUTE, 0, h.DateTime) / @BucketMin) * @BucketMin, 0)
)
SELECT TOP (@Top)
    v.ServerName COLLATE DATABASE_DEFAULT AS [ServerName],
    v.CapturedAtUtc,
    v.MetricGroup COLLATE DATABASE_DEFAULT AS [MetricGroup],
    v.MetricName COLLATE DATABASE_DEFAULT AS [MetricName],
    v.MetricValue,
    v.Detail COLLATE DATABASE_DEFAULT AS [Detail]
FROM Bucketed b
CROSS APPLY (
    SELECT b.WinServer, b.TimeBucket, N''ProcessHealth'', N''ProcessCount'',
           CAST(b.AvgProcesses AS decimal(18,2)),
           N''Processes='' + CAST(CAST(b.AvgProcesses AS int) AS nvarchar(20)) + N''; Queue='' + CAST(CAST(b.AvgQueue AS decimal(5,1)) AS nvarchar(20))
    UNION ALL
    SELECT b.WinServer, b.TimeBucket, N''ProcessHealth'', N''ThreadCount'',
           CAST(b.AvgThreads AS decimal(18,2)),
           N''Threads='' + CAST(CAST(b.AvgThreads AS int) AS nvarchar(20))
    UNION ALL
    SELECT b.WinServer, b.TimeBucket, N''ProcessHealth'', N''HandleCount'',
           CAST(b.AvgHandles AS decimal(18,2)),
           N''Handles='' + CAST(CAST(b.AvgHandles AS int) AS nvarchar(20))
) v(ServerName, CapturedAtUtc, MetricGroup, MetricName, MetricValue, Detail)
ORDER BY v.ServerName, v.CapturedAtUtc, v.MetricName;',
    1, SYSUTCDATETIME(), N'InsertHistorySamples');

INSERT INTO [SQLGig].[DataBOT].[QuestionSamples]
    ([Environment],[GroupKey],[GroupTitle],[GroupOrder],[QuestionText],[QuestionOrder],[Tags],[Script],[IsActive],[UpdatedAt],[UpdatedBy])
VALUES (N'Windows_History', N'ProcessHealth', N'Process & Handle Health', 5,
    N'Show page file usage and memory paging trend over time', 2,
    N'PageFile,Paging,VirtualMemory,Swap,Memory',
    N'DECLARE @FromUtc datetime2(0) = DATEADD(HOUR, -24, SYSUTCDATETIME());
DECLARE @ToUtc   datetime2(0) = SYSUTCDATETIME();
DECLARE @Top     int          = 5000;

DECLARE @SpanMin int = DATEDIFF(MINUTE, @FromUtc, @ToUtc);
DECLARE @BucketMin int = CASE
    WHEN @SpanMin <= 120   THEN 1
    WHEN @SpanMin <= 1440  THEN 5
    WHEN @SpanMin <= 10080 THEN 30
    WHEN @SpanMin <= 43200 THEN 60
    ELSE 1440 END;

;WITH Bucketed AS (
    SELECT
        h.WinServer,
        DATEADD(MINUTE, (DATEDIFF(MINUTE, 0, h.DateTime) / @BucketMin) * @BucketMin, 0) AS TimeBucket,
        AVG(CAST(ISNULL(h.PageWritesPerSec,0) AS float))   AS AvgPageWrites,
        AVG(CAST(ISNULL(h.PageReadsPerSec,0) AS float))    AS AvgPageReads,
        AVG(CAST(ISNULL(h.PageFaultsPerSec,0) AS float))   AS AvgPageFaults,
        AVG(CAST(ISNULL(h.PagedPool,0) AS float))          AS AvgPagedPool,
        AVG(CAST(ISNULL(h.NonPagedPool,0) AS float))       AS AvgNonPagedPool
    FROM [SQLGig].[Monitor].[WINServer_Details_History] h
    WHERE h.DateTime >= @FromUtc AND h.DateTime < @ToUtc
        /*__WINSERVER_FILTER__*/
    GROUP BY h.WinServer,
             DATEADD(MINUTE, (DATEDIFF(MINUTE, 0, h.DateTime) / @BucketMin) * @BucketMin, 0)
)
SELECT TOP (@Top)
    v.ServerName COLLATE DATABASE_DEFAULT AS [ServerName],
    v.CapturedAtUtc,
    v.MetricGroup COLLATE DATABASE_DEFAULT AS [MetricGroup],
    v.MetricName COLLATE DATABASE_DEFAULT AS [MetricName],
    v.MetricValue,
    v.Detail COLLATE DATABASE_DEFAULT AS [Detail]
FROM Bucketed b
CROSS APPLY (
    SELECT b.WinServer, b.TimeBucket, N''Paging'', N''PageReadsPerSec'',
           CAST(b.AvgPageReads AS decimal(18,2)),
           N''PageReads/s='' + CAST(CAST(b.AvgPageReads AS int) AS nvarchar(20)) + N''; PagedPool='' + CAST(CAST(b.AvgPagedPool AS bigint) AS nvarchar(20))
    UNION ALL
    SELECT b.WinServer, b.TimeBucket, N''Paging'', N''PageWritesPerSec'',
           CAST(b.AvgPageWrites AS decimal(18,2)),
           N''PageWrites/s='' + CAST(CAST(b.AvgPageWrites AS int) AS nvarchar(20)) + N''; NonPagedPool='' + CAST(CAST(b.AvgNonPagedPool AS bigint) AS nvarchar(20))
    UNION ALL
    SELECT b.WinServer, b.TimeBucket, N''Paging'', N''PageFaultsPerSec'',
           CAST(b.AvgPageFaults AS decimal(18,2)),
           N''PageFaults/s='' + CAST(CAST(b.AvgPageFaults AS int) AS nvarchar(20))
) v(ServerName, CapturedAtUtc, MetricGroup, MetricName, MetricValue, Detail)
ORDER BY v.ServerName, v.CapturedAtUtc, v.MetricName;',
    1, SYSUTCDATETIME(), N'InsertHistorySamples');

-- --- Group 6: Alerts & Events ------------------------------------------------

INSERT INTO [SQLGig].[DataBOT].[QuestionSamples]
    ([Environment],[GroupKey],[GroupTitle],[GroupOrder],[QuestionText],[QuestionOrder],[Tags],[Script],[IsActive],[UpdatedAt],[UpdatedBy])
VALUES (N'Windows_History', N'WinAlerts', N'Alerts & Events', 6,
    N'Show recent Windows event alerts and errors', 1,
    N'Alerts,Events,EventLog,Errors,Windows',
    N'DECLARE @FromUtc datetime2(0) = DATEADD(HOUR, -24, SYSUTCDATETIME());
DECLARE @ToUtc   datetime2(0) = SYSUTCDATETIME();
DECLARE @Top     int          = 5000;

SELECT TOP (@Top)
    a.Server COLLATE DATABASE_DEFAULT                         AS [ServerName],
    a.DateTime                                                AS [CapturedAtUtc],
    N''WindowsAlerts'' COLLATE DATABASE_DEFAULT                AS [MetricGroup],
    CAST(a.EventID AS nvarchar(20)) + N'': '' + ISNULL(a.Source, N''?'')
    COLLATE DATABASE_DEFAULT                                   AS [MetricName],
    CAST(a.Count AS decimal(18,2))                            AS [MetricValue],
    N''Type='' + ISNULL(a.Type, N''?'')
    + N''; Level='' + ISNULL(a.Level, N''?'')
    + N''; Log='' + ISNULL(a.LogName, N''?'')
    + N''; Count='' + CAST(a.Count AS nvarchar(20))
    + N''; Msg='' + LEFT(ISNULL(a.MessageLeft200, a.Message), 300)
    COLLATE DATABASE_DEFAULT                                   AS [Detail]
FROM [SQLGig].[Monitor].[Alerts] a
WHERE a.DateTime >= @FromUtc AND a.DateTime < @ToUtc
    /*__ALERT_SERVER_FILTER__*/
    AND a.Type = N''WIN''
ORDER BY a.Count DESC, a.DateTime DESC;',
    1, SYSUTCDATETIME(), N'InsertHistorySamples');


-- =============================================================================
-- METRICS TAB TEMPLATES (generic samples with token placeholders)
-- Pipeline replaces: /*__METRIC_SELECT__*/, /*__DETAIL_SELECT__*/,
--                    /*__METRIC_NAME__*/, /*__METRIC_GROUP__*/,
--                    /*__SQLSERVER_FILTER__*/, /*__WINSERVER_FILTER__*/
-- =============================================================================

-- ── SqlServer_History Metrics template (with adaptive bucketing) ─────────────
-- Uses subquery + AVG aggregation to compress time-series data.
-- Token MetricSelectSql produces "TRY_CONVERT(decimal(18,2),h.[Col]) AS MetricValue"
-- so the inner query gets raw values, the outer CTE buckets with AVG.
INSERT INTO [SQLGig].[DataBOT].[QuestionSamples]
    ([Environment],[GroupKey],[GroupTitle],[GroupOrder],[QuestionText],[QuestionOrder],[Tags],[Script],[IsActive],[UpdatedAt],[UpdatedBy])
VALUES (N'SqlServer_History', N'Metrics', N'Metrics', 99,
    N'Show selected metric trend for selected servers in selected period', 1,
    N'Metric,Trend,Chart',
    N'DECLARE @FromUtc datetime2(0) = DATEADD(HOUR, -24, SYSUTCDATETIME());
DECLARE @ToUtc   datetime2(0) = SYSUTCDATETIME();
DECLARE @Top     int          = 5000;

DECLARE @SpanMin int = DATEDIFF(MINUTE, @FromUtc, @ToUtc);
DECLARE @BucketMin int = CASE
    WHEN @SpanMin <= 120   THEN 1
    WHEN @SpanMin <= 1440  THEN 5
    WHEN @SpanMin <= 10080 THEN 30
    WHEN @SpanMin <= 43200 THEN 60
    ELSE 1440 END;

;WITH Raw AS (
    SELECT
        h.SQLServer,
        h.DateTime,
        DATEADD(MINUTE, (DATEDIFF(MINUTE, 0, h.DateTime) / @BucketMin) * @BucketMin, 0) AS TimeBucket,
        /*__METRIC_SELECT__*/,
        /*__DETAIL_SELECT__*/
    FROM [SQLGig].[Monitor].[SQLServer_Details_History] h
    WHERE h.DateTime >= @FromUtc AND h.DateTime < @ToUtc
        /*__SQLSERVER_FILTER__*/
),
Agg AS (
    SELECT
        r.SQLServer,
        r.TimeBucket,
        CAST(AVG(CAST(r.MetricValue AS float)) AS decimal(18,2)) AS MetricValue
    FROM Raw r
    GROUP BY r.SQLServer, r.TimeBucket
)
SELECT TOP (@Top)
    a.SQLServer COLLATE DATABASE_DEFAULT                AS [ServerName],
    a.TimeBucket                                        AS [CapturedAtUtc],
    CAST(/*__METRIC_GROUP__*/ AS nvarchar(100))         AS [MetricGroup],
    CAST(/*__METRIC_NAME__*/ AS nvarchar(256))          AS [MetricName],
    a.MetricValue,
    d.Detail
FROM Agg a
OUTER APPLY (
    SELECT TOP 1 r2.Detail
    FROM Raw r2
    WHERE r2.SQLServer = a.SQLServer AND r2.TimeBucket = a.TimeBucket
    ORDER BY r2.DateTime DESC
) d
ORDER BY a.TimeBucket, a.SQLServer;',
    1, SYSUTCDATETIME(), N'InsertHistorySamples');

-- ── Windows_History Metrics template (with adaptive bucketing) ───────────────
INSERT INTO [SQLGig].[DataBOT].[QuestionSamples]
    ([Environment],[GroupKey],[GroupTitle],[GroupOrder],[QuestionText],[QuestionOrder],[Tags],[Script],[IsActive],[UpdatedAt],[UpdatedBy])
VALUES (N'Windows_History', N'Metrics', N'Metrics', 99,
    N'Show selected metric trend for selected servers in selected period', 1,
    N'Metric,Trend,Chart',
    N'DECLARE @FromUtc datetime2(0) = DATEADD(HOUR, -24, SYSUTCDATETIME());
DECLARE @ToUtc   datetime2(0) = SYSUTCDATETIME();
DECLARE @Top     int          = 5000;

DECLARE @SpanMin int = DATEDIFF(MINUTE, @FromUtc, @ToUtc);
DECLARE @BucketMin int = CASE
    WHEN @SpanMin <= 120   THEN 1
    WHEN @SpanMin <= 1440  THEN 5
    WHEN @SpanMin <= 10080 THEN 30
    WHEN @SpanMin <= 43200 THEN 60
    ELSE 1440 END;

;WITH Raw AS (
    SELECT
        h.WinServer,
        h.DateTime,
        DATEADD(MINUTE, (DATEDIFF(MINUTE, 0, h.DateTime) / @BucketMin) * @BucketMin, 0) AS TimeBucket,
        /*__METRIC_SELECT__*/,
        /*__DETAIL_SELECT__*/
    FROM [SQLGig].[Monitor].[WINServer_Details_History] h
    WHERE h.DateTime >= @FromUtc AND h.DateTime < @ToUtc
        /*__WINSERVER_FILTER__*/
),
Agg AS (
    SELECT
        r.WinServer,
        r.TimeBucket,
        CAST(AVG(CAST(r.MetricValue AS float)) AS decimal(18,2)) AS MetricValue
    FROM Raw r
    GROUP BY r.WinServer, r.TimeBucket
)
SELECT TOP (@Top)
    a.WinServer COLLATE DATABASE_DEFAULT                AS [ServerName],
    a.TimeBucket                                        AS [CapturedAtUtc],
    CAST(/*__METRIC_GROUP__*/ AS nvarchar(100))         AS [MetricGroup],
    CAST(/*__METRIC_NAME__*/ AS nvarchar(256))          AS [MetricName],
    a.MetricValue,
    d.Detail
FROM Agg a
OUTER APPLY (
    SELECT TOP 1 r2.Detail
    FROM Raw r2
    WHERE r2.WinServer = a.WinServer AND r2.TimeBucket = a.TimeBucket
    ORDER BY r2.DateTime DESC
) d
ORDER BY a.TimeBucket, a.WinServer;',
    1, SYSUTCDATETIME(), N'InsertHistorySamples');


-- =============================================================================
-- VERIFICATION
-- =============================================================================

PRINT '';
PRINT '=== INSERTED HISTORY SAMPLES ===';

SELECT [Environment], [GroupKey], [GroupTitle], COUNT(*) AS [Questions]
FROM [SQLGig].[DataBOT].[QuestionSamples]
WHERE [Environment] IN (N'SqlServer_History', N'Windows_History')
  AND [IsActive] = 1
GROUP BY [Environment], [GroupKey], [GroupTitle]
ORDER BY [Environment], [GroupKey];

PRINT '';
PRINT 'Review complete. COMMIT or ROLLBACK as needed.';

COMMIT TRANSACTION;
PRINT 'Transaction committed successfully.';
