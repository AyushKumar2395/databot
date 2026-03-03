namespace Infrastructure.Services;

internal static class EnvironmentRules
{
    public static bool IsGeneral(string environment) =>
        string.Equals(environment, "General", StringComparison.OrdinalIgnoreCase);

    public static bool IsSqlServer(string environment) =>
        environment.StartsWith("SqlServer_", StringComparison.OrdinalIgnoreCase);

    public static bool IsWindows(string environment) =>
        environment.StartsWith("Windows_", StringComparison.OrdinalIgnoreCase);

    public static bool RequiresSelectedServers(string environment) =>
        IsSqlServer(environment) || IsWindows(environment);

    public static string? ResolveScriptLanguage(string environment)
    {
        if (IsSqlServer(environment)) return "SQL";
        if (IsWindows(environment)) return "PS";
        return null;
    }

    public static bool IsKnownEnvironment(string environment) =>
        IsGeneral(environment) || IsSqlServer(environment) || IsWindows(environment);
}
