using System.Text.RegularExpressions;
using Application.Common.Models;

namespace Infrastructure.Services;

/// <summary>
/// Deterministic keyword-based topic classifier for SQL Server and Windows questions.
/// Scores each topic by keyword hits and returns the highest-scoring topic.
/// </summary>
internal static class TopicClassifier
{
    private const int StrongWeight = 10;
    private const int WeakWeight = 3;
    private const int MinScoreThreshold = 10;

    private static readonly Regex WhitespaceCollapse = new(@"\s+", RegexOptions.Compiled);

    // ════════════════════════════════════════════════════════════════════════
    //  SQL TOPICS — keyword sets
    // ════════════════════════════════════════════════════════════════════════

    private static readonly TopicKeywords<SqlTopic>[] SqlTopics =
    [
        new(SqlTopic.AgentJobs,
            ["sql agent", "agent job", "job history", "failed job", "running job",
             "sysjobs", "sysjobhistory", "sysjobactivity", "sysjobsteps", "sysjobschedules",
             "sysschedules", "agent_datetime", "job failure", "job schedule",
             "jobs are currently", "currently running job"],
            ["job", "jobs", "agent", "scheduled"]),

        new(SqlTopic.Transactions,
            ["open transaction", "transaction age", "dm_tran", "log reuse wait",
             "active transaction", "dm_tran_active_transactions", "dm_tran_session_transactions",
             "oldest transaction", "uncommitted transaction", "long running transaction"],
            ["transaction", "tran"]),

        new(SqlTopic.BlockingChains,
            ["blocking", "blocked", "head blocker", "dm_tran_locks", "lck_m",
             "blocked session", "blocking chain", "block tree", "lead blocker",
             "blocking_session_id", "victim", "who blocks", "who is blocking",
             "blocking tree", "blocked queries", "blocked processes"],
            ["block", "lock", "locks"]),

        new(SqlTopic.WaitStats,
            ["wait stats", "top waits", "dm_os_wait_stats", "cxpacket", "pageiolatch",
             "writelog", "sos_scheduler_yield", "wait type", "signal wait",
             "resource wait", "cumulative waits"],
            ["waits", "wait"]),

        new(SqlTopic.TempDB,
            ["tempdb", "tempdb space", "tempdb file", "dm_db_file_space_usage",
             "dm_db_session_space_usage", "dm_db_task_space_usage", "version store",
             "temp table space", "tempdb contention", "gam contention", "sgam contention"],
            []),

        new(SqlTopic.Backups,
            ["last backup", "backup age", "backup history", "msdb backupset", "backup failures",
             "backupset", "backupmediafamily", "backup size", "backup type",
             "full backup", "diff backup", "log backup", "no backup",
             "backup overdue", "never backed up"],
            ["backup", "backups"]),

        new(SqlTopic.Logins,
            ["logins", "server principals", "disabled login", "locked out",
             "sys.server_principals", "sys.sql_logins", "login failed",
             "orphaned user", "server role", "sysadmin member", "sysadmin"],
            ["login", "principal"]),

        new(SqlTopic.ErrorLog,
            ["errorlog", "xp_readerrorlog", "login failed for user",
             "deadlock graph", "sql error log", "error log entries",
             "stack dump", "severity 17", "severity 18", "severity 19",
             "severity 20", "severity 21", "severity 22", "severity 23", "severity 24",
             "in errorlog", "from errorlog", "errorlog entries"],
            []),

        new(SqlTopic.IndexHealth,
            ["fragmentation", "dm_db_index_physical_stats", "page_count",
             "index usage", "dm_db_index_usage_stats", "missing index",
             "dm_db_missing_index", "unused index", "duplicate index",
             "index rebuild", "index reorganize", "fill factor"],
            ["index", "indexes"]),

        new(SqlTopic.FileSpace,
            ["file space", "data file", "log file", "autogrowth", "sys.master_files",
             "database file", "file size", "log space", "dm_db_log_space_usage",
             "filegrowth", "file growth events", "vlf count", "vlf"],
            ["file", "growth"]),

        new(SqlTopic.Databases,
            ["databases", "db list", "recovery model", "compatibility",
             "database state", "sys.databases", "database size",
             "database larger than", "largest database", "database count",
             "offline database", "suspect database", "restoring database"],
            ["database", "db"]),

        new(SqlTopic.InventoryConfig,
            ["version", "edition", "build", "serverproperty", "maxdop", "ctfp",
             "max server memory", "sys.configurations", "sp_configure",
             "product version", "product level", "server collation",
             "instance name", "cpu count"],
            ["config", "configuration", "setting"]),

        new(SqlTopic.AlwaysOn,
            ["always on", "availability group", "ag health", "hadr",
             "replica", "redo queue", "log send queue", "synchronization",
             "dm_hadr", "failover", "secondary replica", "primary replica",
             "listener", "availability database"],
            []),

        new(SqlTopic.SessionsActivity,
            ["who is active", "active sessions", "dm_exec_requests", "dm_exec_sessions",
             "long running", "sp_whoisactive", "running queries", "open session",
             "session count", "cpu time by session", "memory grant",
             "exec_requests"],
            ["session", "sessions", "activity"]),

        new(SqlTopic.ConfigDrift,
            ["compare server", "compare all", "compare selected", "compare configuration",
             "configuration drift", "config drift", "server drift", "setting difference",
             "configuration difference", "side by side", "cross server compare",
             "compare sql server", "drift detection", "compare instances"],
            ["compare", "drift", "difference"]),
    ];

    // ════════════════════════════════════════════════════════════════════════
    //  WINDOWS TOPICS — keyword sets
    // ════════════════════════════════════════════════════════════════════════

    private static readonly TopicKeywords<WindowsTopic>[] WindowsTopics =
    [
        new(WindowsTopic.DiskDrives,
            ["disk space", "free space", "drive free", "win32_volume", "win32_logicaldisk",
             "disk usage", "volume info", "drive space", "disk health",
             "low disk", "disk full", "c: drive", "d: drive"],
            ["disk", "drives", "drive", "volume", "c:", "d:", "e:"]),

        new(WindowsTopic.DiskIO,
            ["disk io", "disk latency", "avg disk sec", "physicaldisk",
             "perf counter", "io latency", "disk queue", "read latency",
             "write latency", "iops"],
            []),

        new(WindowsTopic.TopFolders,
            ["largest folders", "top folders", "directory size", "consuming space",
             "folder size", "biggest directory", "disk usage by folder",
             "folder space", "recursive size"],
            ["path", "depth"]),

        new(WindowsTopic.Services,
            ["get-service", "stopped service", "running service", "automatic service",
             "disabled service", "service status", "windows service",
             "service not running", "auto start service", "service startup type"],
            ["services", "service"]),

        new(WindowsTopic.Processes,
            ["get-process", "top process", "high cpu process", "high memory process",
             "task manager", "process list", "cpu usage by process",
             "memory usage by process", "running processes", "process count",
             "zombie process", "hung process"],
            ["process", "processes"]),

        new(WindowsTopic.EventLogErrors,
            ["event log error", "critical event", "system error", "application error",
             "get-winevent", "level 2", "level 1", "error events",
             "event log critical", "eventlog error"],
            []),

        new(WindowsTopic.EventLogWarnings,
            ["event log warning", "warning event", "level 3",
             "eventlog warning", "recent warnings"],
            []),

        new(WindowsTopic.RebootPending,
            ["reboot pending", "restart required", "pending reboot",
             "cbs reboot", "windows update reboot", "reboot needed",
             "restart pending", "pending restart"],
            []),

        new(WindowsTopic.RebootHistory,
            ["reboot history", "last reboot", "last boot", "unexpected shutdown",
             "event 6008", "event 41", "event 6005", "event 6006", "event 1074",
             "boot time", "shutdown history"],
            ["reboot", "rebooted", "uptime"]),

        new(WindowsTopic.UpdatesHotfixes,
            ["get-hotfix", "hotfix", "installed updates", "patches",
             "windows update", "kb installed", "patch level",
             "missing updates", "update history"],
            ["updates", "patches", "kb"]),

        new(WindowsTopic.NetworkAdapters,
            ["network adapter", "ip address", "dns server", "gateway",
             "mac address", "netadapter", "netipconfiguration",
             "network interface", "nic", "ipv4 address"],
            ["network", "adapter"]),

        new(WindowsTopic.DnsResolve,
            ["dns resolve", "nslookup", "resolve hostname", "resolve-dnsname",
             "dns lookup", "name resolution"],
            []),

        new(WindowsTopic.PortsListening,
            ["listening port", "open port", "tcp listen", "udp listen",
             "netstat listen", "get-nettcpconnection listen",
             "port in use", "bound port"],
            []),

        new(WindowsTopic.TcpConnections,
            ["tcp connection", "established connection", "active connection",
             "remote endpoint", "get-nettcpconnection established",
             "connection count", "outbound connection"],
            ["connections"]),

        new(WindowsTopic.FirewallProfiles,
            ["firewall profile", "netfirewallprofile", "domain profile",
             "private profile", "public profile", "default inbound action",
             "firewall status", "firewall enabled"],
            []),

        new(WindowsTopic.FirewallRules,
            ["firewall rule", "inbound rule", "outbound rule", "rule contains",
             "port allowed", "get-netfirewallrule", "firewall allow",
             "firewall block"],
            ["firewall"]),

        new(WindowsTopic.IIS,
            ["iis", "app pool", "web site", "binding", "webadministration",
             "iis site", "application pool", "iis status", "w3svc",
             "iis log"],
            []),

        new(WindowsTopic.Shares,
            ["smb share", "get-smbshare", "share permission", "network share",
             "shared folder", "file share"],
            ["shares", "share"]),

        new(WindowsTopic.SmbSessions,
            ["smb session", "get-smbsession", "client connection",
             "smb client", "open file", "get-smbopenfile"],
            []),

        new(WindowsTopic.LocalUsersAdmins,
            ["local user", "local admin", "administrators group", "get-localuser",
             "local account", "built-in admin", "user account"],
            []),

        new(WindowsTopic.ScheduledTasks,
            ["scheduled task", "task scheduler", "failed task", "running task",
             "get-scheduledtask", "task history", "task trigger"],
            []),

        new(WindowsTopic.Certificates,
            ["certificate", "ssl", "thumbprint", "expired cert", "expiring cert",
             "cert store", "cert expiry", "tls certificate",
             "certificate chain", "self-signed"],
            ["cert", "certs"]),

        new(WindowsTopic.WinRMStatus,
            ["winrm", "wsman", "listener", "ps remoting", "enable-psremoting",
             "winrm service", "winrm enumerate", "winrm quickconfig"],
            []),

        new(WindowsTopic.WMIStatus,
            ["wmi", "cim", "winmgmt", "rpc server unavailable",
             "wmi repository", "wmi broken", "cim session",
             "wmi health"],
            []),

        new(WindowsTopic.OSInfoInventory,
            ["os info", "hardware", "bios", "cpu model", "memory",
             "uptime", "inventory", "system info", "computer info",
             "os version", "windows version", "build number",
             "installed memory", "processor", "serial number"],
            []),

        new(WindowsTopic.AVDefender,
            ["defender", "antivirus", "get-mpcomputerstatus",
             "malware", "virus scan", "real-time protection",
             "defender status", "av status"],
            []),

        new(WindowsTopic.Cluster,
            ["failover cluster", "cluster node", "get-cluster",
             "get-clusternode", "cluster group", "cluster resource",
             "cluster shared volume", "csv", "quorum"],
            ["cluster"]),

        new(WindowsTopic.ConfigDrift,
            ["compare server", "compare all", "compare selected", "compare windows",
             "configuration drift", "config drift", "server drift", "setting difference",
             "configuration difference", "side by side", "cross server compare",
             "drift detection", "compare patch", "compare services", "compare firewall"],
            ["compare", "drift", "difference"]),
    ];

    // ════════════════════════════════════════════════════════════════════════
    //  PUBLIC API
    // ════════════════════════════════════════════════════════════════════════

    public static TopicClassification Classify(string tunedQuestion, string environment)
    {
        var normalized = Normalize(tunedQuestion);

        if (EnvironmentRules.IsSqlServer(environment))
            return ClassifySql(normalized);

        if (EnvironmentRules.IsWindows(environment))
            return ClassifyWindows(normalized);

        return new TopicClassification
        {
            TopicName = "Other",
            ScriptLanguage = "SQL",
            Score = 0
        };
    }

    /// <summary>
    /// Returns classification plus top-N candidate scores for debug output.
    /// </summary>
    public static (TopicClassification Winner, List<TopicCandidate> Candidates) ClassifyWithCandidates(
        string tunedQuestion, string environment)
    {
        var normalized = Normalize(tunedQuestion);

        if (EnvironmentRules.IsSqlServer(environment))
            return ClassifySqlWithCandidates(normalized);

        if (EnvironmentRules.IsWindows(environment))
            return ClassifyWindowsWithCandidates(normalized);

        var other = new TopicClassification { TopicName = "Other", ScriptLanguage = "SQL", Score = 0 };
        return (other, [new TopicCandidate("Other", 0)]);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  INTERNALS
    // ════════════════════════════════════════════════════════════════════════

    // ════════════════════════════════════════════════════════════════════════
    //  HARD OVERRIDES — these keywords FORCE a specific topic regardless of scoring
    // ════════════════════════════════════════════════════════════════════════

    private static readonly (string[] Keywords, SqlTopic Topic)[] SqlHardOverrides =
    [
        // BlockingChains: any mention of blocking/blocked/victim/head blocker → never WaitStats
        (["blocking chain", "head blocker", "blocked session", "blocking tree",
          "who is blocking", "who blocks", "blocked queries", "blocked processes",
          "blocking_session_id"], SqlTopic.BlockingChains),

        // AgentJobs: explicit agent/job mentions
        (["sql agent job", "agent job", "sysjobhistory", "sysjobactivity",
          "failed job", "job history", "job failure"], SqlTopic.AgentJobs),

        // TempDB: explicit tempdb mentions
        (["tempdb space", "tempdb contention", "tempdb file",
          "dm_db_file_space_usage", "dm_db_session_space_usage"], SqlTopic.TempDB),

        // ErrorLog: explicit errorlog mentions
        (["xp_readerrorlog", "sql error log", "errorlog entries",
          "in errorlog", "from errorlog"], SqlTopic.ErrorLog),

        // Backups: explicit backup history mentions
        (["backup history", "backupset", "backup age", "backup overdue",
          "never backed up", "last backup"], SqlTopic.Backups),

        // ConfigDrift: explicit compare/drift mentions
        (["compare server", "compare all selected", "config drift",
          "configuration drift", "drift detection", "compare sql server",
          "compare instances", "compare configuration"], SqlTopic.ConfigDrift),
    ];

    private static readonly (string[] Keywords, WindowsTopic Topic)[] WindowsHardOverrides =
    [
        (["get-winevent", "event log error", "eventlog error", "event log critical"], WindowsTopic.EventLogErrors),
        (["event log warning", "eventlog warning"], WindowsTopic.EventLogWarnings),
        (["reboot pending", "pending reboot", "restart required", "reboot needed"], WindowsTopic.RebootPending),
        (["scheduled task", "task scheduler", "get-scheduledtask"], WindowsTopic.ScheduledTasks),

        // ConfigDrift: explicit compare/drift mentions
        (["compare server", "compare all selected", "config drift",
          "configuration drift", "drift detection", "compare windows",
          "compare patch", "compare services"], WindowsTopic.ConfigDrift),
    ];

    private static SqlTopic? CheckSqlHardOverride(string normalized)
    {
        foreach (var (keywords, topic) in SqlHardOverrides)
        {
            foreach (var kw in keywords)
            {
                if (normalized.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    return topic;
            }
        }
        return null;
    }

    private static WindowsTopic? CheckWindowsHardOverride(string normalized)
    {
        foreach (var (keywords, topic) in WindowsHardOverrides)
        {
            foreach (var kw in keywords)
            {
                if (normalized.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    return topic;
            }
        }
        return null;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  SQL CLASSIFICATION
    // ════════════════════════════════════════════════════════════════════════

    private static TopicClassification ClassifySql(string normalized)
    {
        var (winner, _) = ClassifySqlWithCandidates(normalized);
        return winner;
    }

    private static (TopicClassification, List<TopicCandidate>) ClassifySqlWithCandidates(string normalized)
    {
        // Hard override check first
        var hardOverride = CheckSqlHardOverride(normalized);

        var candidates = new List<TopicCandidate>(SqlTopics.Length);
        SqlTopic bestTopic = SqlTopic.Other;
        int bestScore = 0;

        foreach (var topicDef in SqlTopics)
        {
            var score = ScoreTopic(normalized, topicDef.StrongKeywords, topicDef.WeakKeywords);
            candidates.Add(new TopicCandidate(topicDef.Topic.ToString(), score));
            if (score > bestScore)
            {
                bestScore = score;
                bestTopic = topicDef.Topic;
            }
        }

        // Sort candidates descending by score, take top 5
        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
        if (candidates.Count > 5)
            candidates.RemoveRange(5, candidates.Count - 5);

        // Apply hard override if present
        if (hardOverride.HasValue)
        {
            bestTopic = hardOverride.Value;
            // Find the score for the overridden topic (might be lower than bestScore)
            var overriddenCandidate = candidates.Find(c => c.TopicName == bestTopic.ToString());
            bestScore = Math.Max(overriddenCandidate?.Score ?? 0, MinScoreThreshold);
        }
        else if (bestScore < MinScoreThreshold)
        {
            bestTopic = SqlTopic.Other;
            bestScore = 0;
        }

        var winner = new TopicClassification
        {
            TopicName = bestTopic.ToString(),
            ScriptLanguage = "SQL",
            Score = bestScore,
            SqlTopic = bestTopic
        };

        return (winner, candidates);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  WINDOWS CLASSIFICATION
    // ════════════════════════════════════════════════════════════════════════

    private static TopicClassification ClassifyWindows(string normalized)
    {
        var (winner, _) = ClassifyWindowsWithCandidates(normalized);
        return winner;
    }

    private static (TopicClassification, List<TopicCandidate>) ClassifyWindowsWithCandidates(string normalized)
    {
        var hardOverride = CheckWindowsHardOverride(normalized);

        var candidates = new List<TopicCandidate>(WindowsTopics.Length);
        WindowsTopic bestTopic = WindowsTopic.Other;
        int bestScore = 0;

        foreach (var topicDef in WindowsTopics)
        {
            var score = ScoreTopic(normalized, topicDef.StrongKeywords, topicDef.WeakKeywords);
            candidates.Add(new TopicCandidate(topicDef.Topic.ToString(), score));
            if (score > bestScore)
            {
                bestScore = score;
                bestTopic = topicDef.Topic;
            }
        }

        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
        if (candidates.Count > 5)
            candidates.RemoveRange(5, candidates.Count - 5);

        if (hardOverride.HasValue)
        {
            bestTopic = hardOverride.Value;
            var overriddenCandidate = candidates.Find(c => c.TopicName == bestTopic.ToString());
            bestScore = Math.Max(overriddenCandidate?.Score ?? 0, MinScoreThreshold);
        }
        else if (bestScore < MinScoreThreshold)
        {
            bestTopic = WindowsTopic.Other;
            bestScore = 0;
        }

        var winner = new TopicClassification
        {
            TopicName = bestTopic.ToString(),
            ScriptLanguage = "PS",
            Score = bestScore,
            WindowsTopic = bestTopic
        };

        return (winner, candidates);
    }

    private static int ScoreTopic(string normalized, string[] strongKeywords, string[] weakKeywords)
    {
        int score = 0;
        foreach (var kw in strongKeywords)
        {
            if (normalized.Contains(kw, StringComparison.OrdinalIgnoreCase))
                score += StrongWeight;
        }
        foreach (var kw in weakKeywords)
        {
            if (normalized.Contains(kw, StringComparison.OrdinalIgnoreCase))
                score += WeakWeight;
        }
        return score;
    }

    private static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
        return WhitespaceCollapse.Replace(text.Trim().ToLowerInvariant(), " ");
    }

    // ════════════════════════════════════════════════════════════════════════
    //  KEYWORD DEFINITION
    // ════════════════════════════════════════════════════════════════════════

    private readonly record struct TopicKeywords<T>(T Topic, string[] StrongKeywords, string[] WeakKeywords)
        where T : struct, Enum;
}

/// <summary>A scored topic candidate for debug output.</summary>
public sealed record TopicCandidate(string TopicName, int Score);
