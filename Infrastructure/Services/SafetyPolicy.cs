using System.Text.RegularExpressions;

namespace Infrastructure.Services;

internal static class SafetyPolicy
{
    private static readonly string[] SqlGlobalBlockedTokens =
    [
        "DELETE",
        "UPDATE",
        "INSERT",
        "MERGE",
        "DROP",
        "ALTER",
        "TRUNCATE",
        "xp_cmdshell",
        "sp_configure",
        "EXECUTE AS",
        "GRANT",
        "REVOKE",
        "DENY",
        "CREATE LOGIN",
        "ALTER LOGIN",
        "DROP LOGIN"
    ];

    private static readonly string[] WindowsGlobalBlockedTokens =
    [
        "Restart-Computer",
        "Stop-Computer",
        "shutdown",
        "Stop-Process",
        "taskkill",
        "Remove-Item",
        "Format-Volume",
        "Clear-Disk",
        "Resize-Partition",
        "New-NetFirewallRule",
        "Set-NetFirewallRule",
        "Remove-NetFirewallRule",
        "Disable-NetAdapter"
    ];

    public static bool TryFindBlockedToken(
        string environment,
        string script,
        IEnumerable<string>? toolBlockedTokens,
        out string? blockedToken)
    {
        blockedToken = null;
        if (string.IsNullOrWhiteSpace(script))
            return false;

        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (EnvironmentRules.IsSqlServer(environment))
        {
            foreach (var token in SqlGlobalBlockedTokens)
                blocked.Add(token);
        }
        else if (EnvironmentRules.IsWindows(environment))
        {
            foreach (var token in WindowsGlobalBlockedTokens)
                blocked.Add(token);
        }

        if (toolBlockedTokens is not null)
        {
            foreach (var token in toolBlockedTokens.Where(t => !string.IsNullOrWhiteSpace(t)))
                blocked.Add(token);
        }

        foreach (var token in blocked)
        {
            if (!ContainsToken(script, token)) continue;
            blockedToken = token;
            return true;
        }

        return false;
    }

    public static IReadOnlyCollection<string> GetEffectiveBlockedTokens(
        string environment,
        IEnumerable<string>? toolBlockedTokens)
    {
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (EnvironmentRules.IsSqlServer(environment))
        {
            foreach (var token in SqlGlobalBlockedTokens)
                blocked.Add(token);
        }
        else if (EnvironmentRules.IsWindows(environment))
        {
            foreach (var token in WindowsGlobalBlockedTokens)
                blocked.Add(token);
        }

        if (toolBlockedTokens is not null)
        {
            foreach (var token in toolBlockedTokens.Where(t => !string.IsNullOrWhiteSpace(t)))
                blocked.Add(token);
        }

        return blocked.ToList();
    }

    private static bool ContainsToken(string script, string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        if (Regex.IsMatch(token, @"^[a-zA-Z0-9_]+$"))
        {
            var pattern = $@"\b{Regex.Escape(token)}\b";
            return Regex.IsMatch(script, pattern, RegexOptions.IgnoreCase);
        }

        return script.Contains(token, StringComparison.OrdinalIgnoreCase);
    }
}
