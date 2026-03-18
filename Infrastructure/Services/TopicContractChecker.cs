using Application.Common.Models;

namespace Infrastructure.Services;

/// <summary>
/// Validates that a generated script satisfies the topic's output contract
/// BEFORE execution. Checks for required DMVs/cmdlets and required column names.
/// </summary>
internal static class TopicContractChecker
{
    /// <summary>
    /// Returns null if the contract passes, or a violation message describing what's wrong.
    /// </summary>
    public static string? Check(TopicClassification topic, string script)
    {
        if (string.IsNullOrWhiteSpace(script))
            return "Script is empty.";

        if (topic.ScriptLanguage == "PS")
            return CheckWindows(topic.WindowsTopic ?? WindowsTopic.Other, script);

        return CheckSql(topic.SqlTopic ?? SqlTopic.Other, script);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  SQL CONTRACT CHECKS
    // ════════════════════════════════════════════════════════════════════════

    private static string? CheckSql(SqlTopic topic, string script)
    {
        // Global SQL contract: must have @@SERVERNAME and SYSUTCDATETIME() (or GETDATE() as fallback)
        if (!Contains(script, "@@SERVERNAME"))
            return "Missing required column: @@SERVERNAME AS [ServerName].";
        if (!Contains(script, "SYSUTCDATETIME()") && !Contains(script, "GETDATE()"))
            return "Missing required column: SYSUTCDATETIME() AS [CapturedAtUtc].";

        var contract = GetSqlContract(topic);
        if (contract is null)
            return null; // Other topic — no specific contract

        // Check required DMVs/sources
        if (!CheckSources(contract, script, out var srcViolation))
            return $"Topic {topic}: {srcViolation} Use ONLY the allowed sources for this topic.";

        // Check required column indicators
        foreach (var col in contract.RequiredColumnIndicators)
        {
            if (!Contains(script, col))
                return $"Topic {topic}: script must include column indicator '{col}' but does not. Include all required output columns for this topic.";
        }

        // Check forbidden sources (e.g., BlockingChains must NOT use dm_os_wait_stats)
        foreach (var forbidden in contract.ForbiddenSources)
        {
            if (Contains(script, forbidden))
                return $"Topic {topic}: script must NOT reference '{forbidden}'. This source belongs to a different topic.";
        }

        return null;
    }

    private static TopicContract? GetSqlContract(SqlTopic topic) => topic switch
    {
        SqlTopic.BlockingChains => new TopicContract(
            RequiredSources: ["dm_exec_requests", "blocking_session_id"],
            RequiredColumnIndicators: ["VictimSessionId", "HeadBlockerSessionId", "WaitType",
                "WaitResource", "DatabaseName",
                "VictimStatementText", "HeadBlockerStatementText"],
            ForbiddenSources: ["dm_os_wait_stats"]),

        SqlTopic.WaitStats => new TopicContract(
            RequiredSources: ["dm_os_wait_stats"],
            RequiredColumnIndicators: ["WaitType", "WaitTimeMs"],
            ForbiddenSources: []),

        SqlTopic.AgentJobs => new TopicContract(
            RequiredSources: ["sysjobs"],
            RequiredColumnIndicators: ["JobName"],
            ForbiddenSources: []),

        SqlTopic.Databases => new TopicContract(
            RequiredSources: ["sys.databases"],
            RequiredColumnIndicators: ["DatabaseName"],
            ForbiddenSources: []),

        SqlTopic.Transactions => new TopicContract(
            RequiredSources: ["dm_tran_active_transactions"],
            RequiredColumnIndicators: ["SessionId"],
            ForbiddenSources: []),

        SqlTopic.TempDB => new TopicContract(
            RequiredSources: ["tempdb"],
            RequiredColumnIndicators: [],
            ForbiddenSources: []),

        SqlTopic.Backups => new TopicContract(
            RequiredSources: ["backupset"],
            RequiredColumnIndicators: ["DatabaseName"],
            ForbiddenSources: []),

        SqlTopic.Logins => new TopicContract(
            RequiredSources: ["server_principals"],
            RequiredColumnIndicators: ["LoginName"],
            ForbiddenSources: []),

        SqlTopic.ErrorLog => new TopicContract(
            RequiredSources: ["xp_readerrorlog"],
            RequiredColumnIndicators: [],
            ForbiddenSources: ["sys.xp_readerrorlog", "FROM xp_readerrorlog"]),

        SqlTopic.IndexHealth => new TopicContract(
            RequiredSources: ["dm_db_index"],
            RequiredColumnIndicators: [],
            ForbiddenSources: []),

        SqlTopic.FileSpace => new TopicContract(
            RequiredSources: ["master_files"],
            RequiredColumnIndicators: ["DatabaseName"],
            ForbiddenSources: []),

        SqlTopic.InventoryConfig => new TopicContract(
            RequiredSources: [],
            RequiredColumnIndicators: [],
            ForbiddenSources: []),

        SqlTopic.AlwaysOn => new TopicContract(
            RequiredSources: ["availability"],
            RequiredColumnIndicators: [],
            ForbiddenSources: []),

        SqlTopic.SessionsActivity => new TopicContract(
            RequiredSources: ["dm_exec_requests"],
            RequiredColumnIndicators: ["SessionId"],
            ForbiddenSources: []),

        SqlTopic.ConfigDrift => new TopicContract(
            RequiredSources: ["sys.configurations"],
            RequiredColumnIndicators: ["Category", "SettingName", "CurrentValue"],
            ForbiddenSources: []),

        _ => null
    };

    // ════════════════════════════════════════════════════════════════════════
    //  WINDOWS CONTRACT CHECKS
    // ════════════════════════════════════════════════════════════════════════

    private static string? CheckWindows(WindowsTopic topic, string script)
    {
        // Global Windows contract: must output $Result
        if (!Contains(script, "$Result"))
            return "Missing required output variable: $Result.";
        if (!Contains(script, "param("))
            return "Missing required: param([string]$TargetServer).";

        var contract = GetWindowsContract(topic);
        if (contract is null)
            return null;

        if (!CheckSources(contract, script, out var srcViolation))
            return $"Topic {topic}: {srcViolation} Use ONLY the allowed cmdlets for this topic.";

        foreach (var col in contract.RequiredColumnIndicators)
        {
            if (!Contains(script, col))
                return $"Topic {topic}: script must include property '{col}' but does not.";
        }

        foreach (var forbidden in contract.ForbiddenSources)
        {
            if (Contains(script, forbidden))
                return $"Topic {topic}: script must NOT reference '{forbidden}'.";
        }

        return null;
    }

    private static TopicContract? GetWindowsContract(WindowsTopic topic) => topic switch
    {
        WindowsTopic.DiskDrives => new TopicContract(
            RequiredSources: ["Win32_Volume", "Win32_LogicalDisk"],
            RequiredColumnIndicators: [],
            ForbiddenSources: [],
            AnyOfSources: true),

        WindowsTopic.Services => new TopicContract(
            RequiredSources: ["Get-Service", "Win32_Service"],
            RequiredColumnIndicators: [],
            ForbiddenSources: [],
            AnyOfSources: true),

        WindowsTopic.Processes => new TopicContract(
            RequiredSources: ["Get-Process", "Win32_Process"],
            RequiredColumnIndicators: [],
            ForbiddenSources: [],
            AnyOfSources: true),

        WindowsTopic.EventLogErrors => new TopicContract(
            RequiredSources: ["Get-WinEvent"],
            RequiredColumnIndicators: [],
            ForbiddenSources: []),

        WindowsTopic.EventLogWarnings => new TopicContract(
            RequiredSources: ["Get-WinEvent"],
            RequiredColumnIndicators: [],
            ForbiddenSources: []),

        WindowsTopic.RebootHistory => new TopicContract(
            RequiredSources: [],
            RequiredColumnIndicators: [],
            ForbiddenSources: []),

        WindowsTopic.RebootPending => new TopicContract(
            RequiredSources: [],
            RequiredColumnIndicators: ["RebootPending"],
            ForbiddenSources: []),

        WindowsTopic.UpdatesHotfixes => new TopicContract(
            RequiredSources: ["Get-HotFix", "Win32_QuickFixEngineering"],
            RequiredColumnIndicators: [],
            ForbiddenSources: [],
            AnyOfSources: true),

        WindowsTopic.NetworkAdapters => new TopicContract(
            RequiredSources: ["NetAdapter", "NetIPConfiguration", "Win32_NetworkAdapter"],
            RequiredColumnIndicators: [],
            ForbiddenSources: [],
            AnyOfSources: true),

        WindowsTopic.PortsListening => new TopicContract(
            RequiredSources: ["NetTCPConnection", "NetUDPEndpoint", "netstat"],
            RequiredColumnIndicators: [],
            ForbiddenSources: [],
            AnyOfSources: true),

        WindowsTopic.TcpConnections => new TopicContract(
            RequiredSources: ["NetTCPConnection"],
            RequiredColumnIndicators: [],
            ForbiddenSources: []),

        WindowsTopic.FirewallProfiles => new TopicContract(
            RequiredSources: ["NetFirewallProfile"],
            RequiredColumnIndicators: [],
            ForbiddenSources: []),

        WindowsTopic.FirewallRules => new TopicContract(
            RequiredSources: ["NetFirewallRule"],
            RequiredColumnIndicators: [],
            ForbiddenSources: []),

        WindowsTopic.IIS => new TopicContract(
            RequiredSources: ["IISSite", "WebSite", "IISAppPool", "w3svc"],
            RequiredColumnIndicators: [],
            ForbiddenSources: [],
            AnyOfSources: true),

        WindowsTopic.Shares => new TopicContract(
            RequiredSources: ["SmbShare"],
            RequiredColumnIndicators: [],
            ForbiddenSources: []),

        WindowsTopic.Certificates => new TopicContract(
            RequiredSources: ["Cert:"],
            RequiredColumnIndicators: [],
            ForbiddenSources: []),

        WindowsTopic.WinRMStatus => new TopicContract(
            RequiredSources: ["WinRM", "WSMan"],
            RequiredColumnIndicators: [],
            ForbiddenSources: [],
            AnyOfSources: true),

        WindowsTopic.WMIStatus => new TopicContract(
            RequiredSources: ["Winmgmt", "CimInstance"],
            RequiredColumnIndicators: [],
            ForbiddenSources: [],
            AnyOfSources: true),

        WindowsTopic.OSInfoInventory => new TopicContract(
            RequiredSources: ["Win32_OperatingSystem"],
            RequiredColumnIndicators: [],
            ForbiddenSources: []),

        WindowsTopic.AVDefender => new TopicContract(
            RequiredSources: ["MpComputerStatus", "MpPreference"],
            RequiredColumnIndicators: [],
            ForbiddenSources: [],
            AnyOfSources: true),

        WindowsTopic.Cluster => new TopicContract(
            RequiredSources: ["Cluster"],
            RequiredColumnIndicators: [],
            ForbiddenSources: []),

        WindowsTopic.ConfigDrift => new TopicContract(
            RequiredSources: [],
            RequiredColumnIndicators: ["Category", "SettingName", "CurrentValue"],
            ForbiddenSources: []),

        _ => null
    };

    // ════════════════════════════════════════════════════════════════════════

    private static bool CheckSources(TopicContract contract, string script, out string violation)
    {
        violation = string.Empty;
        if (contract.RequiredSources.Length == 0)
            return true;

        if (contract.AnyOfSources)
        {
            // At least ONE source must be present
            foreach (var src in contract.RequiredSources)
            {
                if (Contains(script, src))
                    return true;
            }
            violation = $"script must reference at least one of [{string.Join(", ", contract.RequiredSources)}] but none found.";
            return false;
        }

        // ALL sources must be present
        foreach (var src in contract.RequiredSources)
        {
            if (!Contains(script, src))
            {
                violation = $"script must reference '{src}' but does not.";
                return false;
            }
        }
        return true;
    }

    private static bool Contains(string script, string token) =>
        script.Contains(token, StringComparison.OrdinalIgnoreCase);

    private sealed record TopicContract(
        string[] RequiredSources,
        string[] RequiredColumnIndicators,
        string[] ForbiddenSources,
        bool AnyOfSources = false)
    {
        // When AnyOfSources=true, at least ONE of RequiredSources must be present (not all).
    }
}
