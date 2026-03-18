namespace Application.Common.Models;

/// <summary>Topic classification for SQL Server questions.</summary>
public enum SqlTopic
{
    AgentJobs,
    Databases,
    Transactions,
    BlockingChains,
    WaitStats,
    TempDB,
    Backups,
    Logins,
    ErrorLog,
    IndexHealth,
    FileSpace,
    InventoryConfig,
    AlwaysOn,
    SessionsActivity,
    ConfigDrift,
    Other
}

/// <summary>Topic classification for Windows questions.</summary>
public enum WindowsTopic
{
    DiskDrives,
    DiskIO,
    TopFolders,
    Services,
    Processes,
    EventLogErrors,
    EventLogWarnings,
    RebootPending,
    RebootHistory,
    UpdatesHotfixes,
    NetworkAdapters,
    DnsResolve,
    PortsListening,
    TcpConnections,
    FirewallProfiles,
    FirewallRules,
    IIS,
    Shares,
    SmbSessions,
    LocalUsersAdmins,
    ScheduledTasks,
    Certificates,
    WinRMStatus,
    WMIStatus,
    OSInfoInventory,
    AVDefender,
    Cluster,
    ConfigDrift,
    Other
}

/// <summary>Unified topic classification result.</summary>
public sealed class TopicClassification
{
    public string TopicName { get; init; } = "Other";
    public string ScriptLanguage { get; init; } = "SQL";
    public int Score { get; init; }

    // Typed access when needed
    public SqlTopic? SqlTopic { get; init; }
    public WindowsTopic? WindowsTopic { get; init; }
}
