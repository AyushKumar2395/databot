using System.Text.Json.Serialization;

namespace Application.Common.Models;

/// <summary>
/// A single server entry returned by Get_UserSQLServer / Get_UserWINServer stored procedures.
/// </summary>
public sealed class UserServerEntry
{
    [JsonPropertyName("serverName")]
    public string ServerName { get; set; } = string.Empty;

    [JsonPropertyName("instanceName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InstanceName { get; set; }

    /// <summary>
    /// Token used by the UI for selectedTargets: "Server#Instance" for SQL, "Server" for Windows.
    /// Internal routing format — use displayName for UI labels.
    /// </summary>
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// Human-friendly label: "CTS02" for default, "CTS02\ADMIN" for named instances.
    /// </summary>
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// SQL Server port returned by Get_UserSQLServer. 0 or null when not available.
    /// </summary>
    [JsonPropertyName("port")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Port { get; set; }

    /// <summary>
    /// The Windows host this SQL instance lives on (from WINServer column).
    /// </summary>
    [JsonPropertyName("winServer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WinServer { get; set; }

    /// <summary>
    /// Monitoring environment from the SP, e.g. "Altra2".
    /// </summary>
    [JsonPropertyName("monitoringEnvironment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MonitoringEnvironment { get; set; }

    /// <summary>
    /// Domain from the SP, e.g. "ctsglobalconnect.com".
    /// </summary>
    [JsonPropertyName("domain")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Domain { get; set; }

    /// <summary>
    /// Is SQL Server service running? (1 = yes, 0 = no). From SQLService column.
    /// </summary>
    [JsonPropertyName("sqlServiceOnline")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SqlServiceOnline { get; set; }

    /// <summary>
    /// Is SQL Agent service running? (1 = yes, 0 = no). From SQLAgentService column.
    /// </summary>
    [JsonPropertyName("sqlAgentOnline")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SqlAgentOnline { get; set; }
}

/// <summary>
/// Response for /api/servers endpoint.
/// </summary>
public sealed class UserServersResponse
{
    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("environment")]
    public string Environment { get; set; } = string.Empty;

    [JsonPropertyName("servers")]
    public List<UserServerEntry> Servers { get; set; } = [];
}
