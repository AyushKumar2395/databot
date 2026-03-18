using Application.Common.Interfaces;
using Application.Common.Models;
using Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Infrastructure.Tests.TestHelpers;

namespace Infrastructure.Tests;

/// <summary>
/// 100 real-world question acceptance tests: 50 SQL Server (Senior DBA), 50 Windows (Windows Admin).
/// Each question must:
///   1. Not be blocked or mismatched
///   2. Generate a script (SQL or PS)
///   3. Produce a structured answer with title, summary, sections, keyMetrics, recommendations
///
/// These tests run through the full AskPipelineService pipeline using MockLlmBehavior.
/// </summary>
public class LiveQuestionAcceptanceTests
{
    // ═══════════════════════════════════════════════════════════════════════
    // SQL SERVER LIVE — 50 questions (Senior DBA perspective)
    // ═══════════════════════════════════════════════════════════════════════

    #region SQL Server Live — Agent Jobs

    [Theory]
    [InlineData("Show me all failed SQL Agent jobs in the last 24 hours")]
    [InlineData("Which jobs failed last night on this server?")]
    [InlineData("List job failure history with step details")]
    public async Task SqlLive_AgentJobs(string question) =>
        await AssertSqlLiveSuccess(question);

    #endregion

    #region SQL Server Live — Databases

    [Theory]
    [InlineData("List all databases and their sizes")]
    [InlineData("Show databases larger than 50 GB")]
    [InlineData("Which databases are in SUSPECT state?")]
    public async Task SqlLive_Databases(string question) =>
        await AssertSqlLiveSuccess(question);

    #endregion

    #region SQL Server Live — Backups

    [Theory]
    [InlineData("Show backup history for the last 7 days")]
    [InlineData("When was the last full backup taken for each database?")]
    [InlineData("List databases that have not been backed up in 24 hours")]
    public async Task SqlLive_Backups(string question) =>
        await AssertSqlLiveSuccess(question);

    #endregion

    #region SQL Server Live — Wait Stats

    [Theory]
    [InlineData("What are the top wait statistics on this server?")]
    [InlineData("Show me wait stats ordered by total wait time")]
    [InlineData("Which wait types are consuming the most time?")]
    public async Task SqlLive_WaitStats(string question) =>
        await AssertSqlLiveSuccess(question);

    #endregion

    #region SQL Server Live — Blocking & Active Sessions

    [Theory]
    [InlineData("Are there any blocking chains right now?")]
    [InlineData("Show active requests and who is blocking whom")]
    [InlineData("List all currently running queries on the server")]
    [InlineData("Which sessions are blocking other sessions?")]
    public async Task SqlLive_BlockingAndSessions(string question) =>
        await AssertSqlLiveSuccess(question);

    #endregion

    #region SQL Server Live — Index Health

    [Theory]
    [InlineData("Show index fragmentation above 30%")]
    [InlineData("Which indexes are heavily fragmented?")]
    [InlineData("List missing indexes with highest improvement measure")]
    [InlineData("Show the most impactful missing indexes")]
    public async Task SqlLive_IndexHealth(string question) =>
        await AssertSqlLiveSuccess(question);

    #endregion

    #region SQL Server Live — Performance

    [Theory]
    [InlineData("Show me the top CPU consuming queries")]
    [InlineData("Which are the most expensive queries by CPU?")]
    [InlineData("List long running queries taking more than 5 seconds")]
    [InlineData("Show slow queries currently executing")]
    public async Task SqlLive_Performance(string question) =>
        await AssertSqlLiveSuccess(question);

    #endregion

    #region SQL Server Live — TempDB

    [Theory]
    [InlineData("Which sessions are consuming the most tempdb space?")]
    [InlineData("Show tempdb usage by session")]
    [InlineData("Is tempdb under pressure?")]
    public async Task SqlLive_TempDB(string question) =>
        await AssertSqlLiveSuccess(question);

    #endregion

    #region SQL Server Live — Transaction Log

    [Theory]
    [InlineData("Show transaction log sizes for all databases")]
    [InlineData("Which databases have the largest log files?")]
    [InlineData("Is the transaction log full on any database?")]
    public async Task SqlLive_TransactionLog(string question) =>
        await AssertSqlLiveSuccess(question);

    #endregion

    #region SQL Server Live — Always On / AG

    [Theory]
    [InlineData("Show availability group health and replica status")]
    [InlineData("Are all AG replicas synchronized?")]
    [InlineData("What is the state of the Always On availability groups?")]
    public async Task SqlLive_AlwaysOn(string question) =>
        await AssertSqlLiveSuccess(question);

    #endregion

    #region SQL Server Live — Server Info

    [Theory]
    [InlineData("What SQL Server version and build are we running?")]
    [InlineData("Show the current cumulative update level")]
    [InlineData("Which edition of SQL Server is installed?")]
    public async Task SqlLive_ServerInfo(string question) =>
        await AssertSqlLiveSuccess(question);

    #endregion

    #region SQL Server Live — Table Sizes

    [Theory]
    [InlineData("Show the largest tables by size in the current database")]
    [InlineData("Which tables are consuming the most space?")]
    [InlineData("List top 20 tables by row count")]
    public async Task SqlLive_TableSizes(string question) =>
        await AssertSqlLiveSuccess(question);

    #endregion

    #region SQL Server Live — Logins / Security

    [Theory]
    [InlineData("List all SQL Server logins and their types")]
    [InlineData("Show disabled logins on this server")]
    [InlineData("Which server principals have sysadmin access?")]
    public async Task SqlLive_Logins(string question) =>
        await AssertSqlLiveSuccess(question);

    #endregion

    #region SQL Server Live — File Space

    [Theory]
    [InlineData("Show database file sizes and free space")]
    [InlineData("Which database files are almost full?")]
    [InlineData("List data and log file sizes for every database")]
    public async Task SqlLive_FileSpace(string question) =>
        await AssertSqlLiveSuccess(question);

    #endregion

    #region SQL Server Live — Error Log

    [Theory]
    [InlineData("Show recent SQL Server error log entries")]
    [InlineData("Are there any login failures in the error log?")]
    [InlineData("List errors and warnings from the SQL error log today")]
    public async Task SqlLive_ErrorLog(string question) =>
        await AssertSqlLiveSuccess(question);

    #endregion

    // ═══════════════════════════════════════════════════════════════════════
    // WINDOWS LIVE — 50 questions (Windows Admin perspective)
    // ═══════════════════════════════════════════════════════════════════════

    #region Windows Live — CPU / Processor

    [Theory]
    [InlineData("What is the current CPU usage on this server?")]
    [InlineData("Show processor utilization and core count")]
    [InlineData("Is the CPU overloaded right now?")]
    public async Task WinLive_CPU(string question) =>
        await AssertWindowsLiveSuccess(question);

    #endregion

    #region Windows Live — Memory / RAM

    [Theory]
    [InlineData("How much memory is available on this server?")]
    [InlineData("Show RAM usage and free memory")]
    [InlineData("Is this server running low on physical memory?")]
    public async Task WinLive_Memory(string question) =>
        await AssertWindowsLiveSuccess(question);

    #endregion

    #region Windows Live — Disk / Storage

    [Theory]
    [InlineData("Show disk space usage for all drives")]
    [InlineData("Which drives are running low on free space?")]
    [InlineData("List all volumes and their capacity")]
    [InlineData("How much storage is available on the C: drive?")]
    public async Task WinLive_Disk(string question) =>
        await AssertWindowsLiveSuccess(question);

    #endregion

    #region Windows Live — Processes

    [Theory]
    [InlineData("Show the top processes by CPU consumption")]
    [InlineData("Which processes are using the most memory?")]
    [InlineData("List all running processes sorted by CPU")]
    public async Task WinLive_Processes(string question) =>
        await AssertWindowsLiveSuccess(question);

    #endregion

    #region Windows Live — Services

    [Theory]
    [InlineData("Show all stopped Windows services")]
    [InlineData("Is the SQL Server service running?")]
    [InlineData("List services that are set to automatic but not running")]
    public async Task WinLive_Services(string question) =>
        await AssertWindowsLiveSuccess(question);

    #endregion

    #region Windows Live — Event Log

    [Theory]
    [InlineData("Show critical and error events from the last 24 hours")]
    [InlineData("Are there any application event log errors?")]
    [InlineData("List recent system event log warnings")]
    [InlineData("Show Windows event viewer errors for today")]
    public async Task WinLive_EventLog(string question) =>
        await AssertWindowsLiveSuccess(question);

    #endregion

    #region Windows Live — Updates / Patches

    [Theory]
    [InlineData("List recently installed Windows updates and hotfixes")]
    [InlineData("When was the last patch installed?")]
    [InlineData("Show all KB articles applied to this server")]
    public async Task WinLive_Updates(string question) =>
        await AssertWindowsLiveSuccess(question);

    #endregion

    #region Windows Live — Uptime / Reboot

    [Theory]
    [InlineData("When was this server last rebooted?")]
    [InlineData("Show server uptime")]
    [InlineData("How long has this server been running since last restart?")]
    public async Task WinLive_Uptime(string question) =>
        await AssertWindowsLiveSuccess(question);

    #endregion

    #region Windows Live — Network

    [Theory]
    [InlineData("Show network adapter configuration and IP addresses")]
    [InlineData("What is the IP address of this server?")]
    [InlineData("List all network interfaces with DNS settings")]
    public async Task WinLive_Network(string question) =>
        await AssertWindowsLiveSuccess(question);

    #endregion

    #region Windows Live — IIS / Web Server

    [Theory]
    [InlineData("Show IIS application pool status")]
    [InlineData("Are all IIS app pools running?")]
    [InlineData("List websites hosted on this web server")]
    public async Task WinLive_IIS(string question) =>
        await AssertWindowsLiveSuccess(question);

    #endregion

    #region Windows Live — Firewall

    [Theory]
    [InlineData("Show Windows firewall profile status")]
    [InlineData("Is the firewall enabled on all profiles?")]
    [InlineData("List active firewall rules")]
    public async Task WinLive_Firewall(string question) =>
        await AssertWindowsLiveSuccess(question);

    #endregion

    #region Windows Live — Certificates

    [Theory]
    [InlineData("Show certificates that are expiring within 30 days")]
    [InlineData("List all certificates in the local machine store")]
    [InlineData("Which SSL certificates have already expired?")]
    public async Task WinLive_Certificates(string question) =>
        await AssertWindowsLiveSuccess(question);

    #endregion

    #region Windows Live — Scheduled Tasks

    [Theory]
    [InlineData("List all scheduled tasks and their last run status")]
    [InlineData("Show failed scheduled tasks")]
    [InlineData("Which scheduled tasks ran in the last 24 hours?")]
    public async Task WinLive_ScheduledTasks(string question) =>
        await AssertWindowsLiveSuccess(question);

    #endregion

    #region Windows Live — OS Info

    [Theory]
    [InlineData("Show Windows operating system version and build")]
    [InlineData("What edition of Windows Server is installed?")]
    [InlineData("Show the hostname and domain membership of this server")]
    public async Task WinLive_OSInfo(string question) =>
        await AssertWindowsLiveSuccess(question);

    #endregion

    #region Windows Live — Shares / SMB

    [Theory]
    [InlineData("List all network shares on this server")]
    [InlineData("Show active SMB sessions")]
    public async Task WinLive_Shares(string question) =>
        await AssertWindowsLiveSuccess(question);

    #endregion

    #region Windows Live — Ports / Connections

    [Theory]
    [InlineData("Show listening ports on this server")]
    [InlineData("List active TCP connections")]
    public async Task WinLive_Ports(string question) =>
        await AssertWindowsLiveSuccess(question);

    #endregion

    #region Windows Live — Cluster

    [Theory]
    [InlineData("Show Windows cluster node status")]
    [InlineData("Is the cluster healthy?")]
    public async Task WinLive_Cluster(string question) =>
        await AssertWindowsLiveSuccess(question);

    #endregion

    #region Windows Live — Local Users

    [Theory]
    [InlineData("List local administrator accounts")]
    [InlineData("Show local users and their group memberships")]
    public async Task WinLive_LocalUsers(string question) =>
        await AssertWindowsLiveSuccess(question);

    #endregion

    // ═══════════════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════════════

    private static async Task AssertSqlLiveSuccess(string question)
    {
        var service = CreateService();
        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = $"sql-{Guid.NewGuid():N}",
                BearerToken = "test-user",
                Environment = "SqlServer_Live",
                Question = question,
                SelectedTargets = ["CTS03"]
            },
            CancellationToken.None);

        // Must not be blocked or mismatched
        Assert.NotEqual("STOPPED", response.Result.Status);
        Assert.DoesNotContain("BLOCKED", response.Tuning.StopReason ?? string.Empty);
        Assert.DoesNotContain("MISMATCH", response.Tuning.StopReason ?? string.Empty);

        // Must generate a SQL script
        Assert.NotNull(response.Script);
        Assert.NotNull(response.Script!.Final);
        Assert.Contains("@@SERVERNAME", response.Script.Final, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SELECT", response.Script.Final, StringComparison.OrdinalIgnoreCase);

        // Must produce structured answer
        Assert.NotNull(response.Answer);
        Assert.NotNull(response.Answer!.Title);
        Assert.NotNull(response.Answer.Explanation);
        Assert.NotNull(response.Answer.Summary);
        Assert.NotEmpty(response.Answer.Summary!);
        Assert.NotNull(response.Answer.Sections);
        Assert.InRange(response.Answer.Sections!.Count, 4, 8);
        Assert.NotNull(response.Answer.KeyMetrics);
        Assert.NotEmpty(response.Answer.KeyMetrics!);
        Assert.NotNull(response.Answer.Recommendations);
        Assert.NotEmpty(response.Answer.Recommendations!);
    }

    private static async Task AssertWindowsLiveSuccess(string question)
    {
        var service = CreateService();
        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = $"win-{Guid.NewGuid():N}",
                BearerToken = "test-user",
                Environment = "Windows_Live",
                Question = question,
                SelectedTargets = ["CTS03"]
            },
            CancellationToken.None);

        // Must not be blocked or mismatched
        Assert.NotEqual("STOPPED", response.Result.Status);
        Assert.DoesNotContain("BLOCKED", response.Tuning.StopReason ?? string.Empty);
        Assert.DoesNotContain("MISMATCH", response.Tuning.StopReason ?? string.Empty);

        // Must generate a PowerShell script
        Assert.NotNull(response.Script);
        Assert.NotNull(response.Script!.Final);
        Assert.Contains("$Result", response.Script.Final, StringComparison.Ordinal);

        // Must produce structured answer
        Assert.NotNull(response.Answer);
        Assert.NotNull(response.Answer!.Title);
        Assert.NotNull(response.Answer.Explanation);
        Assert.NotNull(response.Answer.Summary);
        Assert.NotEmpty(response.Answer.Summary!);
        Assert.NotNull(response.Answer.Sections);
        Assert.InRange(response.Answer.Sections!.Count, 4, 8);
        Assert.NotNull(response.Answer.KeyMetrics);
        Assert.NotEmpty(response.Answer.KeyMetrics!);
        Assert.NotNull(response.Answer.Recommendations);
        Assert.NotEmpty(response.Answer.Recommendations!);
    }

    private static AskPipelineService CreateService() => TestHelpers.CreateStandardService();
}
