namespace Infrastructure.Services;

internal static class EnvironmentRules
{
    public static bool IsGeneral(string environment) =>
        string.Equals(environment, "General", StringComparison.OrdinalIgnoreCase);

    public static bool IsSqlServer(string environment) =>
        environment.StartsWith("SqlServer_", StringComparison.OrdinalIgnoreCase);

    public static bool IsWindows(string environment) =>
        environment.StartsWith("Windows_", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for SqlServer_History or Windows_History.</summary>
    public static bool IsHistory(string environment) =>
        environment.EndsWith("_History", StringComparison.OrdinalIgnoreCase)
        && (IsSqlServer(environment) || IsWindows(environment));

    /// <summary>True for SqlServer_Live or Windows_Live — NOT History.</summary>
    public static bool IsLive(string environment) =>
        (IsSqlServer(environment) || IsWindows(environment)) && !IsHistory(environment);

    /// <summary>Only Live environments require selected servers for execution. History queries run centrally on CTS03.</summary>
    public static bool RequiresSelectedServers(string environment) =>
        IsLive(environment);

    public static string? ResolveScriptLanguage(string environment)
    {
        // History environments always generate T-SQL (even Windows_History).
        if (IsHistory(environment)) return "SQL";
        if (IsSqlServer(environment)) return "SQL";
        if (IsWindows(environment)) return "PS";
        return null;
    }

    public static bool IsKnownEnvironment(string environment) =>
        IsGeneral(environment) || IsSqlServer(environment) || IsWindows(environment);

    /// <summary>The centralized server where all History queries execute.</summary>
    public const string HistoryExecutionTarget = "CTS03";
}
