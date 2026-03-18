using Application.Common.Models;
using Infrastructure.Services;
using Xunit;

namespace Infrastructure.Tests;

public sealed class TopicClassifierTests
{
    // ════════════════════════════════════════════════════════════════════════
    //  SQL Server Topics
    // ════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("list all failed jobs", "AgentJobs")]
    [InlineData("show SQL Agent job history last 24 hours", "AgentJobs")]
    [InlineData("which jobs are currently running", "AgentJobs")]
    [InlineData("sysjobhistory failures this week", "AgentJobs")]
    [InlineData("job schedule for nightly backup job", "AgentJobs")]
    public void Sql_AgentJobs_Classified(string question, string expectedTopic)
    {
        var result = TopicClassifier.Classify(question, "SqlServer_Live");
        Assert.Equal(expectedTopic, result.TopicName);
        Assert.Equal("SQL", result.ScriptLanguage);
        Assert.True(result.Score >= 10);
    }

    [Theory]
    [InlineData("list all databases", "Databases")]
    [InlineData("databases larger than 10GB", "Databases")]
    [InlineData("show recovery model for all databases", "Databases")]
    [InlineData("offline databases", "Databases")]
    [InlineData("database compatibility level", "Databases")]
    public void Sql_Databases_Classified(string question, string expectedTopic)
    {
        var result = TopicClassifier.Classify(question, "SqlServer_Live");
        Assert.Equal(expectedTopic, result.TopicName);
        Assert.Equal("SQL", result.ScriptLanguage);
    }

    [Theory]
    [InlineData("show open transactions", "Transactions")]
    [InlineData("oldest active transaction", "Transactions")]
    [InlineData("dm_tran_active_transactions", "Transactions")]
    [InlineData("long running transaction age", "Transactions")]
    public void Sql_Transactions_Classified(string question, string expectedTopic)
    {
        var result = TopicClassifier.Classify(question, "SqlServer_Live");
        Assert.Equal(expectedTopic, result.TopicName);
        Assert.Equal("SQL", result.ScriptLanguage);
    }

    [Theory]
    [InlineData("show blocking sessions", "BlockingChains")]
    [InlineData("who is the head blocker", "BlockingChains")]
    [InlineData("blocked queries right now", "BlockingChains")]
    [InlineData("check for blocking and locks", "BlockingChains")]
    public void Sql_BlockingChains_Classified(string question, string expectedTopic)
    {
        var result = TopicClassifier.Classify(question, "SqlServer_Live");
        Assert.Equal(expectedTopic, result.TopicName);
        Assert.Equal("SQL", result.ScriptLanguage);
    }

    [Theory]
    [InlineData("show top waits", "WaitStats")]
    [InlineData("dm_os_wait_stats top 10", "WaitStats")]
    [InlineData("wait stats analysis", "WaitStats")]
    [InlineData("cxpacket waits percentage", "WaitStats")]
    public void Sql_WaitStats_Classified(string question, string expectedTopic)
    {
        var result = TopicClassifier.Classify(question, "SqlServer_Live");
        Assert.Equal(expectedTopic, result.TopicName);
        Assert.Equal("SQL", result.ScriptLanguage);
    }

    [Theory]
    [InlineData("tempdb space usage", "TempDB")]
    [InlineData("tempdb file sizes", "TempDB")]
    [InlineData("version store size in tempdb", "TempDB")]
    [InlineData("dm_db_session_space_usage for tempdb", "TempDB")]
    public void Sql_TempDB_Classified(string question, string expectedTopic)
    {
        var result = TopicClassifier.Classify(question, "SqlServer_Live");
        Assert.Equal(expectedTopic, result.TopicName);
        Assert.Equal("SQL", result.ScriptLanguage);
    }

    [Theory]
    [InlineData("show backup history last 7 days", "Backups")]
    [InlineData("databases with no backup", "Backups")]
    [InlineData("last full backup for each database", "Backups")]
    [InlineData("backup failures this week", "Backups")]
    [InlineData("msdb backupset entries", "Backups")]
    public void Sql_Backups_Classified(string question, string expectedTopic)
    {
        var result = TopicClassifier.Classify(question, "SqlServer_Live");
        Assert.Equal(expectedTopic, result.TopicName);
        Assert.Equal("SQL", result.ScriptLanguage);
    }

    [Theory]
    [InlineData("list all logins", "Logins")]
    [InlineData("disabled logins on the server", "Logins")]
    [InlineData("who has sysadmin role", "Logins")]
    [InlineData("locked out SQL logins", "Logins")]
    public void Sql_Logins_Classified(string question, string expectedTopic)
    {
        var result = TopicClassifier.Classify(question, "SqlServer_Live");
        Assert.Equal(expectedTopic, result.TopicName);
        Assert.Equal("SQL", result.ScriptLanguage);
    }

    [Theory]
    [InlineData("check SQL errorlog for errors", "ErrorLog")]
    [InlineData("xp_readerrorlog recent entries", "ErrorLog")]
    [InlineData("login failed events in errorlog", "ErrorLog")]
    public void Sql_ErrorLog_Classified(string question, string expectedTopic)
    {
        var result = TopicClassifier.Classify(question, "SqlServer_Live");
        Assert.Equal(expectedTopic, result.TopicName);
        Assert.Equal("SQL", result.ScriptLanguage);
    }

    [Theory]
    [InlineData("index fragmentation over 30%", "IndexHealth")]
    [InlineData("missing indexes", "IndexHealth")]
    [InlineData("unused indexes", "IndexHealth")]
    [InlineData("dm_db_index_physical_stats scan", "IndexHealth")]
    public void Sql_IndexHealth_Classified(string question, string expectedTopic)
    {
        var result = TopicClassifier.Classify(question, "SqlServer_Live");
        Assert.Equal(expectedTopic, result.TopicName);
        Assert.Equal("SQL", result.ScriptLanguage);
    }

    [Theory]
    [InlineData("data file and log file sizes", "FileSpace")]
    [InlineData("autogrowth settings for all databases", "FileSpace")]
    [InlineData("log space usage per database", "FileSpace")]
    [InlineData("vlf count for all databases", "FileSpace")]
    public void Sql_FileSpace_Classified(string question, string expectedTopic)
    {
        var result = TopicClassifier.Classify(question, "SqlServer_Live");
        Assert.Equal(expectedTopic, result.TopicName);
        Assert.Equal("SQL", result.ScriptLanguage);
    }

    [Theory]
    [InlineData("SQL Server version and edition", "InventoryConfig")]
    [InlineData("show maxdop and ctfp settings", "InventoryConfig")]
    [InlineData("max server memory configuration", "InventoryConfig")]
    [InlineData("serverproperty values", "InventoryConfig")]
    public void Sql_InventoryConfig_Classified(string question, string expectedTopic)
    {
        var result = TopicClassifier.Classify(question, "SqlServer_Live");
        Assert.Equal(expectedTopic, result.TopicName);
        Assert.Equal("SQL", result.ScriptLanguage);
    }

    [Theory]
    [InlineData("AlwaysOn availability group health", "AlwaysOn")]
    [InlineData("replica synchronization state", "AlwaysOn")]
    [InlineData("redo queue and log send queue size", "AlwaysOn")]
    [InlineData("HADR status check", "AlwaysOn")]
    public void Sql_AlwaysOn_Classified(string question, string expectedTopic)
    {
        var result = TopicClassifier.Classify(question, "SqlServer_Live");
        Assert.Equal(expectedTopic, result.TopicName);
        Assert.Equal("SQL", result.ScriptLanguage);
    }

    [Theory]
    [InlineData("who is active right now", "SessionsActivity")]
    [InlineData("long running queries", "SessionsActivity")]
    [InlineData("active sessions with high CPU", "SessionsActivity")]
    [InlineData("dm_exec_requests running", "SessionsActivity")]
    public void Sql_SessionsActivity_Classified(string question, string expectedTopic)
    {
        var result = TopicClassifier.Classify(question, "SqlServer_Live");
        Assert.Equal(expectedTopic, result.TopicName);
        Assert.Equal("SQL", result.ScriptLanguage);
    }

    [Theory]
    [InlineData("what happened yesterday")]
    [InlineData("hello")]
    [InlineData("tell me something interesting")]
    public void Sql_Other_When_NoKeywordMatch(string question)
    {
        var result = TopicClassifier.Classify(question, "SqlServer_Live");
        Assert.Equal("Other", result.TopicName);
        Assert.Equal("SQL", result.ScriptLanguage);
        Assert.Equal(0, result.Score);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Windows Topics
    // ════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("check disk space on all drives", "DiskDrives")]
    [InlineData("C: drive free space", "DiskDrives")]
    [InlineData("low disk warning", "DiskDrives")]
    [InlineData("volume info for all disks", "DiskDrives")]
    public void Win_DiskDrives_Classified(string question, string expectedTopic)
    {
        var result = TopicClassifier.Classify(question, "Windows_Live");
        Assert.Equal(expectedTopic, result.TopicName);
        Assert.Equal("PS", result.ScriptLanguage);
        Assert.True(result.Score >= 10);
    }

    [Theory]
    [InlineData("show stopped services", "Services")]
    [InlineData("automatic services not running", "Services")]
    [InlineData("get-service status", "Services")]
    [InlineData("disabled services list", "Services")]
    public void Win_Services_Classified(string question, string expectedTopic)
    {
        var result = TopicClassifier.Classify(question, "Windows_Live");
        Assert.Equal(expectedTopic, result.TopicName);
        Assert.Equal("PS", result.ScriptLanguage);
    }

    [Theory]
    [InlineData("top processes by CPU", "Processes")]
    [InlineData("high memory processes", "Processes")]
    [InlineData("running processes list", "Processes")]
    [InlineData("get-process top 10 by memory", "Processes")]
    public void Win_Processes_Classified(string question, string expectedTopic)
    {
        var result = TopicClassifier.Classify(question, "Windows_Live");
        Assert.Equal(expectedTopic, result.TopicName);
        Assert.Equal("PS", result.ScriptLanguage);
    }

    [Theory]
    [InlineData("event log errors last 24 hours", "EventLogErrors")]
    [InlineData("critical events in system log", "EventLogErrors")]
    [InlineData("application errors today", "EventLogErrors")]
    public void Win_EventLogErrors_Classified(string question, string expectedTopic)
    {
        var result = TopicClassifier.Classify(question, "Windows_Live");
        Assert.Equal(expectedTopic, result.TopicName);
        Assert.Equal("PS", result.ScriptLanguage);
    }

    [Theory]
    [InlineData("event log warnings last 2 hours", "EventLogWarnings")]
    [InlineData("warning events in application log", "EventLogWarnings")]
    public void Win_EventLogWarnings_Classified(string question, string expectedTopic)
    {
        var result = TopicClassifier.Classify(question, "Windows_Live");
        Assert.Equal(expectedTopic, result.TopicName);
        Assert.Equal("PS", result.ScriptLanguage);
    }

    [Theory]
    [InlineData("when did the server last reboot", "RebootHistory")]
    [InlineData("reboot history last 30 days", "RebootHistory")]
    [InlineData("unexpected shutdown events", "RebootHistory")]
    [InlineData("last boot time and uptime", "RebootHistory")]
    public void Win_RebootHistory_Classified(string question, string expectedTopic)
    {
        var result = TopicClassifier.Classify(question, "Windows_Live");
        Assert.Equal(expectedTopic, result.TopicName);
        Assert.Equal("PS", result.ScriptLanguage);
    }

    [Theory]
    [InlineData("is a reboot pending", "RebootPending")]
    [InlineData("check pending reboot status", "RebootPending")]
    [InlineData("restart required check", "RebootPending")]
    public void Win_RebootPending_Classified(string question, string expectedTopic)
    {
        var result = TopicClassifier.Classify(question, "Windows_Live");
        Assert.Equal(expectedTopic, result.TopicName);
        Assert.Equal("PS", result.ScriptLanguage);
    }

    [Theory]
    [InlineData("list installed updates", "UpdatesHotfixes")]
    [InlineData("get-hotfix last 30 days", "UpdatesHotfixes")]
    [InlineData("installed patches this month", "UpdatesHotfixes")]
    [InlineData("KB installed recently", "UpdatesHotfixes")]
    public void Win_UpdatesHotfixes_Classified(string question, string expectedTopic)
    {
        var result = TopicClassifier.Classify(question, "Windows_Live");
        Assert.Equal(expectedTopic, result.TopicName);
        Assert.Equal("PS", result.ScriptLanguage);
    }

    [Theory]
    [InlineData("show network adapters and IP addresses", "NetworkAdapters")]
    [InlineData("DNS servers configured", "NetworkAdapters")]
    [InlineData("list NICs and MAC addresses", "NetworkAdapters")]
    public void Win_NetworkAdapters_Classified(string question, string expectedTopic)
    {
        var result = TopicClassifier.Classify(question, "Windows_Live");
        Assert.Equal(expectedTopic, result.TopicName);
        Assert.Equal("PS", result.ScriptLanguage);
    }

    [Theory]
    [InlineData("listening ports", "PortsListening")]
    [InlineData("open ports on this server", "PortsListening")]
    [InlineData("tcp listening connections", "PortsListening")]
    public void Win_PortsListening_Classified(string question, string expectedTopic)
    {
        var result = TopicClassifier.Classify(question, "Windows_Live");
        Assert.Equal(expectedTopic, result.TopicName);
        Assert.Equal("PS", result.ScriptLanguage);
    }

    [Theory]
    [InlineData("established TCP connections", "TcpConnections")]
    [InlineData("active connections to remote servers", "TcpConnections")]
    public void Win_TcpConnections_Classified(string question, string expectedTopic)
    {
        var result = TopicClassifier.Classify(question, "Windows_Live");
        Assert.Equal(expectedTopic, result.TopicName);
        Assert.Equal("PS", result.ScriptLanguage);
    }

    [Theory]
    [InlineData("firewall profile status", "FirewallProfiles")]
    [InlineData("is firewall enabled for domain profile", "FirewallProfiles")]
    public void Win_FirewallProfiles_Classified(string question, string expectedTopic)
    {
        var result = TopicClassifier.Classify(question, "Windows_Live");
        Assert.Equal(expectedTopic, result.TopicName);
        Assert.Equal("PS", result.ScriptLanguage);
    }

    [Theory]
    [InlineData("list inbound firewall rules", "FirewallRules")]
    [InlineData("firewall rules for port 443", "FirewallRules")]
    public void Win_FirewallRules_Classified(string question, string expectedTopic)
    {
        var result = TopicClassifier.Classify(question, "Windows_Live");
        Assert.Equal(expectedTopic, result.TopicName);
        Assert.Equal("PS", result.ScriptLanguage);
    }

    [Theory]
    [InlineData("IIS sites and bindings", "IIS")]
    [InlineData("app pool status", "IIS")]
    [InlineData("IIS application pools running", "IIS")]
    public void Win_IIS_Classified(string question, string expectedTopic)
    {
        var result = TopicClassifier.Classify(question, "Windows_Live");
        Assert.Equal(expectedTopic, result.TopicName);
        Assert.Equal("PS", result.ScriptLanguage);
    }

    [Theory]
    [InlineData("list SMB shares", "Shares")]
    [InlineData("network shares on this server", "Shares")]
    [InlineData("get-smbshare permissions", "Shares")]
    public void Win_Shares_Classified(string question, string expectedTopic)
    {
        var result = TopicClassifier.Classify(question, "Windows_Live");
        Assert.Equal(expectedTopic, result.TopicName);
        Assert.Equal("PS", result.ScriptLanguage);
    }

    [Theory]
    [InlineData("SSL certificates expiring soon", "Certificates")]
    [InlineData("expired certificates in local machine store", "Certificates")]
    [InlineData("certificate thumbprint list", "Certificates")]
    public void Win_Certificates_Classified(string question, string expectedTopic)
    {
        var result = TopicClassifier.Classify(question, "Windows_Live");
        Assert.Equal(expectedTopic, result.TopicName);
        Assert.Equal("PS", result.ScriptLanguage);
    }

    [Theory]
    [InlineData("WinRM service status and listeners", "WinRMStatus")]
    [InlineData("check PS remoting status", "WinRMStatus")]
    public void Win_WinRMStatus_Classified(string question, string expectedTopic)
    {
        var result = TopicClassifier.Classify(question, "Windows_Live");
        Assert.Equal(expectedTopic, result.TopicName);
        Assert.Equal("PS", result.ScriptLanguage);
    }

    [Theory]
    [InlineData("WMI health check", "WMIStatus")]
    [InlineData("is CIM working properly", "WMIStatus")]
    [InlineData("check WMI repository health", "WMIStatus")]
    public void Win_WMIStatus_Classified(string question, string expectedTopic)
    {
        var result = TopicClassifier.Classify(question, "Windows_Live");
        Assert.Equal(expectedTopic, result.TopicName);
        Assert.Equal("PS", result.ScriptLanguage);
    }

    [Theory]
    [InlineData("OS version and build number", "OSInfoInventory")]
    [InlineData("hardware inventory", "OSInfoInventory")]
    [InlineData("server uptime", "OSInfoInventory")]
    [InlineData("CPU model and memory info", "OSInfoInventory")]
    public void Win_OSInfoInventory_Classified(string question, string expectedTopic)
    {
        var result = TopicClassifier.Classify(question, "Windows_Live");
        Assert.Equal(expectedTopic, result.TopicName);
        Assert.Equal("PS", result.ScriptLanguage);
    }

    [Theory]
    [InlineData("Windows Defender status", "AVDefender")]
    [InlineData("antivirus signature age", "AVDefender")]
    [InlineData("get-mpcomputerstatus", "AVDefender")]
    public void Win_AVDefender_Classified(string question, string expectedTopic)
    {
        var result = TopicClassifier.Classify(question, "Windows_Live");
        Assert.Equal(expectedTopic, result.TopicName);
        Assert.Equal("PS", result.ScriptLanguage);
    }

    [Theory]
    [InlineData("failover cluster status", "Cluster")]
    [InlineData("cluster nodes health", "Cluster")]
    [InlineData("get-clusternode state", "Cluster")]
    public void Win_Cluster_Classified(string question, string expectedTopic)
    {
        var result = TopicClassifier.Classify(question, "Windows_Live");
        Assert.Equal(expectedTopic, result.TopicName);
        Assert.Equal("PS", result.ScriptLanguage);
    }

    [Theory]
    [InlineData("what happened yesterday")]
    [InlineData("hello")]
    public void Win_Other_When_NoKeywordMatch(string question)
    {
        var result = TopicClassifier.Classify(question, "Windows_Live");
        Assert.Equal("Other", result.TopicName);
        Assert.Equal("PS", result.ScriptLanguage);
        Assert.Equal(0, result.Score);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Score tie-breaking: highest score wins
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Sql_HighestScoreWins_When_MultipleTopicsMatch()
    {
        // "failed job history" has strong hits on AgentJobs (job history, failed job)
        // and weak hit on Databases (none) — AgentJobs should win
        var result = TopicClassifier.Classify("show failed job history from sysjobhistory", "SqlServer_Live");
        Assert.Equal("AgentJobs", result.TopicName);
        Assert.True(result.Score >= 20); // multiple strong hits
    }

    [Fact]
    public void Win_HighestScoreWins_When_MultipleTopicsMatch()
    {
        // "disk space free on all drives" hits DiskDrives strongly
        var result = TopicClassifier.Classify("disk space free on all drives", "Windows_Live");
        Assert.Equal("DiskDrives", result.TopicName);
        Assert.True(result.Score >= 10);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  TopicPromptBuilder produces non-empty, topic-anchored prompts
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void SqlPrompt_Contains_TopicConstraints()
    {
        var topic = TopicClassifier.Classify("list all failed jobs", "SqlServer_Live");
        var prompt = TopicPromptBuilder.BuildPrompt(topic, "list all failed jobs", "SqlServer_Live");

        Assert.Contains("TOPIC CONSTRAINTS", prompt);
        Assert.Contains("AgentJobs", prompt);
        Assert.Contains("sysjobs", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DataBot-SQL", prompt);
        Assert.Contains("READ-ONLY T-SQL", prompt);
    }

    [Fact]
    public void WindowsPrompt_Contains_TopicConstraints()
    {
        var topic = TopicClassifier.Classify("check disk space", "Windows_Live");
        var prompt = TopicPromptBuilder.BuildPrompt(topic, "check disk space", "Windows_Live");

        Assert.Contains("TOPIC CONSTRAINTS", prompt);
        Assert.Contains("DiskDrives", prompt);
        Assert.Contains("Win32_Volume", prompt);
        Assert.Contains("DataBot-Windows", prompt);
        Assert.Contains("READ-ONLY PowerShell", prompt);
    }

    [Fact]
    public void SqlOther_Prompt_Still_Has_BaseConstraints()
    {
        var topic = TopicClassifier.Classify("what happened yesterday", "SqlServer_Live");
        var prompt = TopicPromptBuilder.BuildPrompt(topic, "what happened yesterday", "SqlServer_Live");

        Assert.Contains("TOPIC CONSTRAINTS", prompt);
        Assert.Contains("General SQL", prompt);
        Assert.Contains("DataBot-SQL", prompt);
    }

    [Fact]
    public void WindowsOther_Prompt_Still_Has_BaseConstraints()
    {
        var topic = TopicClassifier.Classify("what happened yesterday", "Windows_Live");
        var prompt = TopicPromptBuilder.BuildPrompt(topic, "what happened yesterday", "Windows_Live");

        Assert.Contains("TOPIC CONSTRAINTS", prompt);
        Assert.Contains("General Windows", prompt);
        Assert.Contains("DataBot-Windows", prompt);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  General environment returns Other
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void General_Environment_Returns_Other()
    {
        var result = TopicClassifier.Classify("list all failed jobs", "General");
        Assert.Equal("Other", result.TopicName);
        Assert.Equal(0, result.Score);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Hard overrides — must force correct topic regardless of score competition
    // ════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("show blocking chain now (head blocker + victims with wait type and SQL text)", "BlockingChains")]
    [InlineData("who is blocking whom right now", "BlockingChains")]
    [InlineData("blocked queries with head blocker", "BlockingChains")]
    [InlineData("show blocked sessions and their victims", "BlockingChains")]
    [InlineData("blocking chain analysis", "BlockingChains")]
    [InlineData("blocking_session_id for all active queries", "BlockingChains")]
    public void HardOverride_BlockingChains_Never_WaitStats(string question, string expectedTopic)
    {
        var result = TopicClassifier.Classify(question, "SqlServer_Live");
        Assert.Equal(expectedTopic, result.TopicName);
        Assert.NotEqual("WaitStats", result.TopicName);
    }

    [Theory]
    [InlineData("sql agent job failures last 24 hours", "AgentJobs")]
    [InlineData("show sysjobhistory errors", "AgentJobs")]
    [InlineData("failed job history this week", "AgentJobs")]
    public void HardOverride_AgentJobs_Forced(string question, string expectedTopic)
    {
        var result = TopicClassifier.Classify(question, "SqlServer_Live");
        Assert.Equal(expectedTopic, result.TopicName);
    }

    [Theory]
    [InlineData("tempdb space usage by session", "TempDB")]
    [InlineData("tempdb file IO stats", "TempDB")]
    [InlineData("tempdb contention analysis", "TempDB")]
    public void HardOverride_TempDB_Forced(string question, string expectedTopic)
    {
        var result = TopicClassifier.Classify(question, "SqlServer_Live");
        Assert.Equal(expectedTopic, result.TopicName);
    }

    [Theory]
    [InlineData("xp_readerrorlog last 100 errors", "ErrorLog")]
    [InlineData("sql error log severity 17 and above", "ErrorLog")]
    [InlineData("login failed events in errorlog", "ErrorLog")]
    public void HardOverride_ErrorLog_Forced(string question, string expectedTopic)
    {
        var result = TopicClassifier.Classify(question, "SqlServer_Live");
        Assert.Equal(expectedTopic, result.TopicName);
    }

    [Theory]
    [InlineData("backup history for all databases", "Backups")]
    [InlineData("databases never backed up", "Backups")]
    [InlineData("last backup time for each database", "Backups")]
    [InlineData("backup overdue databases", "Backups")]
    public void HardOverride_Backups_Forced(string question, string expectedTopic)
    {
        var result = TopicClassifier.Classify(question, "SqlServer_Live");
        Assert.Equal(expectedTopic, result.TopicName);
    }

    [Theory]
    [InlineData("event log errors last 24 hours", "EventLogErrors")]
    [InlineData("critical event log entries", "EventLogErrors")]
    public void HardOverride_Win_EventLogErrors_Forced(string question, string expectedTopic)
    {
        var result = TopicClassifier.Classify(question, "Windows_Live");
        Assert.Equal(expectedTopic, result.TopicName);
    }

    [Theory]
    [InlineData("is a reboot pending", "RebootPending")]
    [InlineData("check if pending reboot exists", "RebootPending")]
    public void HardOverride_Win_RebootPending_Forced(string question, string expectedTopic)
    {
        var result = TopicClassifier.Classify(question, "Windows_Live");
        Assert.Equal(expectedTopic, result.TopicName);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  ClassifyWithCandidates — returns top-5 scores
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ClassifyWithCandidates_Returns_Top5_Scores_Sql()
    {
        var (winner, candidates) = TopicClassifier.ClassifyWithCandidates(
            "show blocking chains now", "SqlServer_Live");

        Assert.Equal("BlockingChains", winner.TopicName);
        Assert.True(winner.Score >= 10);
        Assert.NotNull(candidates);
        Assert.True(candidates.Count <= 5);
        Assert.True(candidates.Count >= 1);
        // First candidate should have highest score
        Assert.True(candidates[0].Score >= candidates[^1].Score);
    }

    [Fact]
    public void ClassifyWithCandidates_Returns_Top5_Scores_Windows()
    {
        var (winner, candidates) = TopicClassifier.ClassifyWithCandidates(
            "check disk space on all drives", "Windows_Live");

        Assert.Equal("DiskDrives", winner.TopicName);
        Assert.NotNull(candidates);
        Assert.True(candidates.Count <= 5);
        Assert.True(candidates[0].Score >= candidates[^1].Score);
    }

    [Fact]
    public void ClassifyWithCandidates_General_Returns_Other()
    {
        var (winner, candidates) = TopicClassifier.ClassifyWithCandidates(
            "tell me about SQL Server", "General");

        Assert.Equal("Other", winner.TopicName);
        Assert.Single(candidates);
        Assert.Equal("Other", candidates[0].TopicName);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Spec acceptance tests — topic→script correctness verification
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Spec_BlockingChains_Prompt_References_DmExecRequests()
    {
        var topic = TopicClassifier.Classify(
            "show blocking chains now", "SqlServer_Live");
        Assert.Equal("BlockingChains", topic.TopicName);
        var prompt = TopicPromptBuilder.BuildPrompt(topic,
            "show blocking chains now", "SqlServer_Live");
        Assert.Contains("dm_exec_requests", prompt);
        Assert.Contains("blocking_session_id", prompt);
        // The prompt mentions dm_os_wait_stats only as "Do NOT use" warning — that's OK.
        // The contract checker enforces the actual forbidden source check.
    }

    [Fact]
    public void Spec_WaitStats_Prompt_References_DmOsWaitStats()
    {
        var topic = TopicClassifier.Classify(
            "top waits on the server", "SqlServer_Live");
        Assert.Equal("WaitStats", topic.TopicName);
        var prompt = TopicPromptBuilder.BuildPrompt(topic,
            "top waits on the server", "SqlServer_Live");
        Assert.Contains("dm_os_wait_stats", prompt);
    }

    [Fact]
    public void Spec_AgentJobs_Prompt_References_SysjobsHistory()
    {
        var topic = TopicClassifier.Classify(
            "list failed agent jobs last 24 hours", "SqlServer_Live");
        Assert.Equal("AgentJobs", topic.TopicName);
        var prompt = TopicPromptBuilder.BuildPrompt(topic,
            "list failed agent jobs last 24 hours", "SqlServer_Live");
        Assert.Contains("sysjobs", prompt);
        Assert.Contains("sysjobhistory", prompt);
    }

    [Fact]
    public void Spec_Databases_Prompt_References_SysDatabases()
    {
        var topic = TopicClassifier.Classify(
            "list databases contains SQLGIG", "SqlServer_Live");
        Assert.Equal("Databases", topic.TopicName);
        var prompt = TopicPromptBuilder.BuildPrompt(topic,
            "list databases contains SQLGIG", "SqlServer_Live");
        Assert.Contains("sys.databases", prompt);
    }

    [Fact]
    public void Spec_Transactions_Prompt_References_DmTranActive()
    {
        var topic = TopicClassifier.Classify(
            "open transactions", "SqlServer_Live");
        Assert.Equal("Transactions", topic.TopicName);
        var prompt = TopicPromptBuilder.BuildPrompt(topic,
            "open transactions", "SqlServer_Live");
        Assert.Contains("dm_tran_active_transactions", prompt);
    }

    [Fact]
    public void Spec_Win_DiskDrives_Prompt_References_Win32Volume()
    {
        var topic = TopicClassifier.Classify(
            "disk space on all drives below 10% free", "Windows_Live");
        Assert.Equal("DiskDrives", topic.TopicName);
        var prompt = TopicPromptBuilder.BuildPrompt(topic,
            "list drives below 10% free", "Windows_Live");
        Assert.Contains("Win32_Volume", prompt);
    }

    [Fact]
    public void Spec_Win_Services_Prompt_References_GetService()
    {
        var topic = TopicClassifier.Classify(
            "list stopped services automatic", "Windows_Live");
        Assert.Equal("Services", topic.TopicName);
        var prompt = TopicPromptBuilder.BuildPrompt(topic,
            "list stopped services automatic", "Windows_Live");
        Assert.Contains("Get-Service", prompt);
    }

    [Fact]
    public void Spec_Win_EventLog_Prompt_References_GetWinEvent()
    {
        var topic = TopicClassifier.Classify(
            "event log errors last 6 hours", "Windows_Live");
        Assert.Equal("EventLogErrors", topic.TopicName);
        var prompt = TopicPromptBuilder.BuildPrompt(topic,
            "event log errors last 6 hours", "Windows_Live");
        Assert.Contains("Get-WinEvent", prompt);
    }

    [Fact]
    public void Spec_Win_Ports_Prompt_References_NetTCPConnection()
    {
        var topic = TopicClassifier.Classify(
            "list listening ports", "Windows_Live");
        Assert.Equal("PortsListening", topic.TopicName);
        var prompt = TopicPromptBuilder.BuildPrompt(topic,
            "list listening ports", "Windows_Live");
        Assert.Contains("NetTCPConnection", prompt);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Parameter extraction hints presence in prompts
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Sql_Prompt_Contains_Parameter_Extraction_Section()
    {
        var topic = TopicClassifier.Classify("list all databases", "SqlServer_Live");
        var prompt = TopicPromptBuilder.BuildPrompt(topic, "list all databases", "SqlServer_Live");
        Assert.Contains("PARAMETER EXTRACTION", prompt);
        Assert.Contains("top N", prompt);
        Assert.Contains("last N hours", prompt);
        Assert.Contains("contains", prompt);
    }

    [Fact]
    public void Windows_Prompt_Contains_Parameter_Extraction_Section()
    {
        var topic = TopicClassifier.Classify("list stopped services", "Windows_Live");
        var prompt = TopicPromptBuilder.BuildPrompt(topic, "list stopped services", "Windows_Live");
        Assert.Contains("PARAMETER EXTRACTION", prompt);
        Assert.Contains("top N", prompt);
        Assert.Contains("last N hours", prompt);
    }
}
