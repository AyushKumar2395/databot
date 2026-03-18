using Application.Common.Models;
using Infrastructure.Services;
using Xunit;

namespace Infrastructure.Tests;

public sealed class TopicContractCheckerTests
{
    // ════════════════════════════════════════════════════════════════════════
    //  SQL BlockingChains — must use dm_exec_requests + blocking_session_id
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void BlockingChains_Pass_With_Correct_Script()
    {
        var topic = new TopicClassification
        {
            TopicName = "BlockingChains",
            ScriptLanguage = "SQL",
            SqlTopic = SqlTopic.BlockingChains,
            Score = 20
        };

        var script = """
            SELECT
                @@SERVERNAME AS [ServerName],
                SYSUTCDATETIME() AS [CapturedAtUtc],
                r.session_id AS [VictimSessionId],
                r.blocking_session_id AS [HeadBlockerSessionId],
                r.wait_type AS [WaitType],
                r.wait_resource AS [WaitResource],
                DB_NAME(r.database_id) AS [DatabaseName],
                SUBSTRING(st.text, (r.statement_start_offset/2)+1, 100) AS [VictimStatementText],
                hb_st.text AS [HeadBlockerStatementText]
            FROM sys.dm_exec_requests r
            WHERE r.blocking_session_id > 0
            ORDER BY r.wait_time DESC
            """;

        Assert.Null(TopicContractChecker.Check(topic, script));
    }

    [Fact]
    public void BlockingChains_Fail_When_Using_WaitStats_DMV()
    {
        var topic = new TopicClassification
        {
            TopicName = "BlockingChains",
            ScriptLanguage = "SQL",
            SqlTopic = SqlTopic.BlockingChains,
            Score = 20
        };

        // Wrong: uses dm_os_wait_stats instead of dm_exec_requests blocking chain
        var script = """
            SELECT
                @@SERVERNAME AS [ServerName],
                SYSUTCDATETIME() AS [CapturedAtUtc],
                r.session_id AS [VictimSessionId],
                r.blocking_session_id AS [HeadBlockerSessionId],
                r.wait_type AS [WaitType],
                r.wait_resource AS [WaitResource],
                DB_NAME(r.database_id) AS [DatabaseName],
                st.text AS [VictimStatementText],
                hb_st.text AS [HeadBlockerStatementText]
            FROM sys.dm_os_wait_stats ws
            JOIN sys.dm_exec_requests r ON 1=1
            WHERE r.blocking_session_id > 0
            """;

        var violation = TopicContractChecker.Check(topic, script);
        Assert.NotNull(violation);
        Assert.Contains("dm_os_wait_stats", violation);
    }

    [Fact]
    public void BlockingChains_Fail_When_Missing_Required_DMV()
    {
        var topic = new TopicClassification
        {
            TopicName = "BlockingChains",
            ScriptLanguage = "SQL",
            SqlTopic = SqlTopic.BlockingChains,
            Score = 20
        };

        // Missing dm_exec_requests
        var script = """
            SELECT
                @@SERVERNAME AS [ServerName],
                SYSUTCDATETIME() AS [CapturedAtUtc],
                s.session_id AS [VictimSessionId],
                0 AS [HeadBlockerSessionId],
                'none' AS [WaitType],
                '' AS [WaitResource],
                '' AS [DatabaseName],
                '' AS [VictimStatementText],
                '' AS [HeadBlockerStatementText]
            FROM sys.dm_exec_sessions s
            WHERE s.blocking_session_id > 0
            """;

        var violation = TopicContractChecker.Check(topic, script);
        Assert.NotNull(violation);
        Assert.Contains("dm_exec_requests", violation);
    }

    [Fact]
    public void BlockingChains_Fail_When_Missing_Required_Columns()
    {
        var topic = new TopicClassification
        {
            TopicName = "BlockingChains",
            ScriptLanguage = "SQL",
            SqlTopic = SqlTopic.BlockingChains,
            Score = 20
        };

        // Has dm_exec_requests and blocking_session_id but missing VictimSessionId column alias
        var script = """
            SELECT
                @@SERVERNAME AS [ServerName],
                SYSUTCDATETIME() AS [CapturedAtUtc],
                r.blocking_session_id AS [HeadBlockerSessionId],
                r.wait_type AS [WaitType],
                r.wait_resource AS [WaitResource],
                DB_NAME(r.database_id) AS [DatabaseName],
                st.text AS [VictimStatementText],
                hb_st.text AS [HeadBlockerStatementText]
            FROM sys.dm_exec_requests r
            WHERE r.blocking_session_id > 0
            """;

        var violation = TopicContractChecker.Check(topic, script);
        Assert.NotNull(violation);
        Assert.Contains("VictimSessionId", violation);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  SQL WaitStats — must use dm_os_wait_stats
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void WaitStats_Pass_With_Correct_Script()
    {
        var topic = new TopicClassification
        {
            TopicName = "WaitStats",
            ScriptLanguage = "SQL",
            SqlTopic = SqlTopic.WaitStats,
            Score = 20
        };

        var script = """
            SELECT TOP (25)
                @@SERVERNAME AS [ServerName],
                SYSUTCDATETIME() AS [CapturedAtUtc],
                ws.wait_type AS [WaitType],
                ws.wait_time_ms AS [WaitTimeMs]
            FROM sys.dm_os_wait_stats ws
            ORDER BY ws.wait_time_ms DESC
            """;

        Assert.Null(TopicContractChecker.Check(topic, script));
    }

    [Fact]
    public void WaitStats_Fail_When_Missing_DMV()
    {
        var topic = new TopicClassification
        {
            TopicName = "WaitStats",
            ScriptLanguage = "SQL",
            SqlTopic = SqlTopic.WaitStats,
            Score = 20
        };

        var script = """
            SELECT
                @@SERVERNAME AS [ServerName],
                SYSUTCDATETIME() AS [CapturedAtUtc],
                r.wait_type
            FROM sys.dm_exec_requests r
            """;

        var violation = TopicContractChecker.Check(topic, script);
        Assert.NotNull(violation);
        Assert.Contains("dm_os_wait_stats", violation);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  SQL AgentJobs — must reference sysjobs
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void AgentJobs_Pass_With_Correct_Script()
    {
        var topic = new TopicClassification
        {
            TopicName = "AgentJobs",
            ScriptLanguage = "SQL",
            SqlTopic = SqlTopic.AgentJobs,
            Score = 20
        };

        var script = """
            SELECT
                @@SERVERNAME AS [ServerName],
                SYSUTCDATETIME() AS [CapturedAtUtc],
                sj.name AS [JobName]
            FROM msdb.dbo.sysjobs sj
            """;

        Assert.Null(TopicContractChecker.Check(topic, script));
    }

    // ════════════════════════════════════════════════════════════════════════
    //  SQL Global — missing @@SERVERNAME / GETDATE()
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Sql_Fail_When_Missing_ServerName()
    {
        var topic = new TopicClassification
        {
            TopicName = "Databases",
            ScriptLanguage = "SQL",
            SqlTopic = SqlTopic.Databases,
            Score = 10
        };

        var script = """
            SELECT
                SYSUTCDATETIME() AS [CapturedAtUtc],
                d.name AS [DatabaseName]
            FROM sys.databases d
            """;

        var violation = TopicContractChecker.Check(topic, script);
        Assert.NotNull(violation);
        Assert.Contains("@@SERVERNAME", violation);
    }

    [Fact]
    public void Sql_Fail_When_Missing_Timestamp()
    {
        var topic = new TopicClassification
        {
            TopicName = "Databases",
            ScriptLanguage = "SQL",
            SqlTopic = SqlTopic.Databases,
            Score = 10
        };

        var script = """
            SELECT
                @@SERVERNAME AS [ServerName],
                d.name AS [DatabaseName]
            FROM sys.databases d
            """;

        var violation = TopicContractChecker.Check(topic, script);
        Assert.NotNull(violation);
        Assert.Contains("SYSUTCDATETIME()", violation);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  SQL Other — no specific contract, just global checks
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void SqlOther_Pass_With_GlobalColumns()
    {
        var topic = new TopicClassification
        {
            TopicName = "Other",
            ScriptLanguage = "SQL",
            SqlTopic = SqlTopic.Other,
            Score = 0
        };

        var script = """
            SELECT @@SERVERNAME AS [ServerName], SYSUTCDATETIME() AS [CapturedAtUtc], 'hello' AS [Value]
            """;

        Assert.Null(TopicContractChecker.Check(topic, script));
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Windows DiskDrives — AnyOf: Win32_Volume OR Win32_LogicalDisk
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void WinDiskDrives_Pass_With_Volume()
    {
        var topic = new TopicClassification
        {
            TopicName = "DiskDrives",
            ScriptLanguage = "PS",
            WindowsTopic = WindowsTopic.DiskDrives,
            Score = 20
        };

        var script = """
            param([string]$TargetServer)
            $Result = @()
            $vols = Get-CimInstance Win32_Volume -Filter "DriveType=3"
            foreach ($v in $vols) {
                $Result += [pscustomobject]@{ ServerName=$TargetServer; CapturedAt=Get-Date; Status='OK'; ErrorMessage=$null; Drive=$v.Name }
            }
            $Result
            """;

        Assert.Null(TopicContractChecker.Check(topic, script));
    }

    [Fact]
    public void WinDiskDrives_Pass_With_LogicalDisk()
    {
        var topic = new TopicClassification
        {
            TopicName = "DiskDrives",
            ScriptLanguage = "PS",
            WindowsTopic = WindowsTopic.DiskDrives,
            Score = 20
        };

        var script = """
            param([string]$TargetServer)
            $Result = @()
            $disks = Get-CimInstance Win32_LogicalDisk
            foreach ($d in $disks) {
                $Result += [pscustomobject]@{ ServerName=$TargetServer; CapturedAt=Get-Date; Status='OK'; ErrorMessage=$null; Drive=$d.DeviceID }
            }
            $Result
            """;

        Assert.Null(TopicContractChecker.Check(topic, script));
    }

    [Fact]
    public void WinDiskDrives_Fail_When_Neither_Source()
    {
        var topic = new TopicClassification
        {
            TopicName = "DiskDrives",
            ScriptLanguage = "PS",
            WindowsTopic = WindowsTopic.DiskDrives,
            Score = 20
        };

        var script = """
            param([string]$TargetServer)
            $Result = @()
            $Result += [pscustomobject]@{ ServerName=$TargetServer; CapturedAt=Get-Date; Status='OK'; ErrorMessage=$null }
            $Result
            """;

        var violation = TopicContractChecker.Check(topic, script);
        Assert.NotNull(violation);
        Assert.Contains("Win32_Volume", violation);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Windows Services — AnyOf: Get-Service OR Win32_Service
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void WinServices_Pass_With_GetService()
    {
        var topic = new TopicClassification
        {
            TopicName = "Services",
            ScriptLanguage = "PS",
            WindowsTopic = WindowsTopic.Services,
            Score = 20
        };

        var script = """
            param([string]$TargetServer)
            $Result = @()
            $svcs = Get-Service | Where-Object { $_.Status -eq 'Stopped' }
            foreach ($s in $svcs) {
                $Result += [pscustomobject]@{ ServerName=$TargetServer; CapturedAt=Get-Date; Status='OK'; ErrorMessage=$null; Name=$s.Name }
            }
            $Result
            """;

        Assert.Null(TopicContractChecker.Check(topic, script));
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Windows Global — missing $Result or param
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Win_Fail_When_Missing_Result()
    {
        var topic = new TopicClassification
        {
            TopicName = "Services",
            ScriptLanguage = "PS",
            WindowsTopic = WindowsTopic.Services,
            Score = 20
        };

        var script = """
            param([string]$TargetServer)
            Get-Service | Format-Table
            """;

        var violation = TopicContractChecker.Check(topic, script);
        Assert.NotNull(violation);
        Assert.Contains("$Result", violation);
    }

    [Fact]
    public void Win_Fail_When_Missing_Param()
    {
        var topic = new TopicClassification
        {
            TopicName = "Services",
            ScriptLanguage = "PS",
            WindowsTopic = WindowsTopic.Services,
            Score = 20
        };

        var script = """
            $Result = @()
            $Result += [pscustomobject]@{ ServerName='x'; CapturedAt=Get-Date; Status='OK'; ErrorMessage=$null }
            $Result
            """;

        var violation = TopicContractChecker.Check(topic, script);
        Assert.NotNull(violation);
        Assert.Contains("param(", violation);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Empty script
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Empty_Script_Fails()
    {
        var topic = new TopicClassification
        {
            TopicName = "BlockingChains",
            ScriptLanguage = "SQL",
            SqlTopic = SqlTopic.BlockingChains,
            Score = 20
        };

        Assert.NotNull(TopicContractChecker.Check(topic, ""));
        Assert.NotNull(TopicContractChecker.Check(topic, "   "));
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Classifier + Contract integration: blocking question → correct topic
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void BlockingChain_Question_Never_Classified_As_WaitStats()
    {
        var questions = new[]
        {
            "Show blocking chains now (head blocker + victims with wait type and SQL text)",
            "who is blocking whom right now",
            "blocked queries with head blocker",
            "show blocked sessions and their victims",
            "blocking chain analysis"
        };

        foreach (var q in questions)
        {
            var result = TopicClassifier.Classify(q, "SqlServer_Live");
            Assert.Equal("BlockingChains", result.TopicName);
            Assert.NotEqual("WaitStats", result.TopicName);
        }
    }

    [Fact]
    public void TopWaits_Question_Classified_As_WaitStats_Not_Blocking()
    {
        var questions = new[]
        {
            "top waits on this server",
            "show wait stats analysis",
            "dm_os_wait_stats top 25"
        };

        foreach (var q in questions)
        {
            var result = TopicClassifier.Classify(q, "SqlServer_Live");
            Assert.Equal("WaitStats", result.TopicName);
        }
    }
}
