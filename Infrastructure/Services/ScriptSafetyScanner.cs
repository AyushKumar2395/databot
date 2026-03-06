using System.Text.RegularExpressions;

namespace Infrastructure.Services;

internal sealed partial class ScriptSafetyScanner
{
    private static readonly string[] SqlBlockedProcedureTokens =
    [
        "xp_cmdshell",
        "sp_configure",
        "sp_OACreate",
        "OPENROWSET",
        "OPENDATASOURCE",
        "sp_add_job",
        "sp_update_job",
        "sp_delete_job",
        "sp_add_jobstep",
        "sp_update_jobstep",
        "sp_delete_jobstep",
        "sp_add_jobschedule",
        "sp_update_jobschedule",
        "sp_delete_jobschedule"
    ];

    /// <summary>Maximum allowed script length in bytes (80 KB) to prevent runaway LLM output.</summary>
    private const int MaxScriptLengthBytes = 80 * 1024;

    public SafetyScanResult Scan(string environment, string scriptLanguage, string script)
    {
        var sanitized = StripCodeFences(script);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return new SafetyScanResult
            {
                IsSafe = false,
                SanitizedScript = string.Empty,
                BlockedToken = "EMPTY_SCRIPT"
            };
        }

        // Reject if backticks or markdown fences survived stripping
        if (sanitized.Contains('`'))
        {
            return new SafetyScanResult
            {
                IsSafe = false,
                SanitizedScript = sanitized,
                BlockedToken = "BACKTICK_DETECTED"
            };
        }

        // Reject scripts that exceed max length
        if (System.Text.Encoding.UTF8.GetByteCount(sanitized) > MaxScriptLengthBytes)
        {
            return new SafetyScanResult
            {
                IsSafe = false,
                SanitizedScript = sanitized,
                BlockedToken = "SCRIPT_TOO_LARGE"
            };
        }

        if (EnvironmentRules.IsSqlServer(environment) || string.Equals(scriptLanguage, "SQL", StringComparison.OrdinalIgnoreCase))
        {
            if (TryFindBlockedSqlToken(sanitized, out var blockedSqlToken))
            {
                return new SafetyScanResult
                {
                    IsSafe = false,
                    SanitizedScript = sanitized,
                    BlockedToken = blockedSqlToken
                };
            }

            // Reject SQL-specific dangerous procedures: BULK INSERT, OLE automation, CLR
            if (TryFindBlockedSqlAdvancedToken(sanitized, out var advToken))
            {
                return new SafetyScanResult
                {
                    IsSafe = false,
                    SanitizedScript = sanitized,
                    BlockedToken = advToken
                };
            }
        }

        if (EnvironmentRules.IsWindows(environment) || string.Equals(scriptLanguage, "PS", StringComparison.OrdinalIgnoreCase))
        {
            if (TryFindBlockedWindowsToken(sanitized, out var blockedWindowsToken))
            {
                return new SafetyScanResult
                {
                    IsSafe = false,
                    SanitizedScript = sanitized,
                    BlockedToken = blockedWindowsToken
                };
            }
        }

        return new SafetyScanResult
        {
            IsSafe = true,
            SanitizedScript = sanitized
        };
    }

    private static bool TryFindBlockedSqlAdvancedToken(string script, out string blockedToken)
    {
        // BULK INSERT, OLE automation (sp_OACreate already in list), CLR enabling
        if (Regex.IsMatch(script, @"\bBULK\s+INSERT\b", RegexOptions.IgnoreCase))
        {
            blockedToken = "BULK INSERT";
            return true;
        }

        if (Regex.IsMatch(script, @"\bsp_OAMethod\b", RegexOptions.IgnoreCase))
        {
            blockedToken = "sp_OAMethod";
            return true;
        }

        if (Regex.IsMatch(script, @"\bsp_OADestroy\b", RegexOptions.IgnoreCase))
        {
            blockedToken = "sp_OADestroy";
            return true;
        }

        blockedToken = string.Empty;
        return false;
    }

    private static bool TryFindBlockedSqlToken(string script, out string blockedToken)
    {
        var blockedStatement = SqlBlockedStatementRegex().Match(script);
        if (blockedStatement.Success)
        {
            blockedToken = blockedStatement.Groups["stmt"].Value.ToUpperInvariant();
            return true;
        }

        foreach (var token in SqlBlockedProcedureTokens)
        {
            if (!Regex.IsMatch(script, $@"\b{Regex.Escape(token)}\b", RegexOptions.IgnoreCase))
                continue;

            blockedToken = token;
            return true;
        }

        if (Regex.IsMatch(
                script,
                @"\bALTER\s+EVENT\s+SESSION\b[\s\S]*?\bSTATE\s*=\s*(START|STOP)\b",
                RegexOptions.IgnoreCase))
        {
            blockedToken = "ALTER EVENT SESSION STATE";
            return true;
        }

        blockedToken = string.Empty;
        return false;
    }

    private static bool TryFindBlockedWindowsToken(string script, out string blockedToken)
    {
        var blockedCommand = WindowsBlockedCommandRegex().Match(script);
        if (blockedCommand.Success)
        {
            blockedToken = blockedCommand.Groups["cmd"].Value;
            return true;
        }

        var blockedPatternCommand = WindowsBlockedPatternCommandRegex().Match(script);
        if (blockedPatternCommand.Success)
        {
            blockedToken = blockedPatternCommand.Groups["cmd"].Value;
            return true;
        }

        var userMutationCommand = WindowsUserMutationRegex().Match(script);
        if (userMutationCommand.Success)
        {
            blockedToken = userMutationCommand.Groups["cmd"].Value;
            return true;
        }

        blockedToken = string.Empty;
        return false;
    }

    private static string StripCodeFences(string script)
    {
        var text = (script ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        text = Regex.Replace(text, @"^\s*```[\w-]*\s*\r?\n", string.Empty, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\r?\n\s*```\s*$", string.Empty, RegexOptions.IgnoreCase);
        return text.Trim();
    }

    [GeneratedRegex(@"(?im)^\s*(?<stmt>INSERT|UPDATE|DELETE|MERGE|TRUNCATE|DROP|ALTER|CREATE|GRANT|REVOKE|DENY|KILL|RECONFIGURE|BACKUP|RESTORE)\b")]
    private static partial Regex SqlBlockedStatementRegex();

    [GeneratedRegex(@"(?im)^\s*(?<cmd>Restart-Computer|Stop-Computer|shutdown|Stop-Service|Start-Service|Restart-Service|Stop-Process|taskkill|Set-ItemProperty|New-ItemProperty|Remove-Item|Format-Volume|Clear-EventLog|Disable-NetAdapter|New-NetFirewallRule|Set-NetFirewallRule|Remove-NetFirewallRule)\b")]
    private static partial Regex WindowsBlockedCommandRegex();

    [GeneratedRegex(@"(?im)^\s*(?<cmd>Install-[A-Za-z0-9_-]+|Uninstall-[A-Za-z0-9_-]+)\b")]
    private static partial Regex WindowsBlockedPatternCommandRegex();

    [GeneratedRegex(@"(?im)^\s*(?<cmd>New-LocalUser|Remove-LocalUser|Set-LocalUser|Add-LocalGroupMember|Remove-LocalGroupMember|net\s+user)\b")]
    private static partial Regex WindowsUserMutationRegex();
}

internal sealed class SafetyScanResult
{
    public bool IsSafe { get; init; }
    public string SanitizedScript { get; init; } = string.Empty;
    public string? BlockedToken { get; init; }
}
