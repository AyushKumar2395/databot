using System.Text.RegularExpressions;

namespace Infrastructure.Services;

internal sealed partial class ScriptSafetyScanner
{
    private static readonly string[] SqlBlockedProcedureTokens =
    [
        "xp_cmdshell",
        "xp_regread",
        "xp_regwrite",
        "xp_regdelete",
        "xp_servicecontrol",
        "sp_configure",
        "sp_OACreate",
        "sp_OAMethod",
        "sp_executesql",     // dynamic SQL — LLM should generate direct queries
        "sp_MSForEachTable",
        "sp_MSForEachDB",
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
            var commentFree = StripSqlComments(sanitized);
            if (TryFindBlockedSqlToken(commentFree, out var blockedSqlToken))
            {
                return new SafetyScanResult
                {
                    IsSafe = false,
                    SanitizedScript = sanitized,
                    BlockedToken = blockedSqlToken
                };
            }

            // Reject SQL-specific dangerous procedures: BULK INSERT, OLE automation, CLR
            if (TryFindBlockedSqlAdvancedToken(commentFree, out var advToken))
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
        // 1. Blanket-blocked statements (no safe variant)
        var blockedStatement = SqlBlockedStatementRegex().Match(script);
        if (blockedStatement.Success)
        {
            blockedToken = blockedStatement.Groups["stmt"].Value.ToUpperInvariant();
            return true;
        }

        // 2. INSERT — allow @tableVar / #temp, block real tables
        if (SqlBlockedInsertIntoRealTableRegex().IsMatch(script) ||
            SqlBlockedInsertDirectRealTableRegex().IsMatch(script))
        {
            blockedToken = "INSERT_INTO_TABLE";
            return true;
        }

        // 3. DROP — allow DROP TABLE #temp, block everything else
        if (SqlAnyDropRegex().IsMatch(script) &&
            !AllOccurrencesMatch(script, SqlAnyDropRegex(), SqlAllowedDropTempTableRegex()))
        {
            blockedToken = "DROP";
            return true;
        }

        // 4. CREATE — allow CREATE TABLE #temp and CREATE INDEX ON #temp, block rest
        if (SqlAnyCreateRegex().IsMatch(script) &&
            !AllOccurrencesMatch(script, SqlAnyCreateRegex(), SqlAllowedCreateTempTableRegex(), SqlAllowedCreateIndexOnTempRegex()))
        {
            blockedToken = "CREATE";
            return true;
        }

        // 5. ALTER — allow ALTER INDEX ON #temp, block rest (ALTER EVENT SESSION handled below)
        if (SqlAnyAlterRegex().IsMatch(script) &&
            !AllOccurrencesMatch(script, SqlAnyAlterRegex(), SqlAllowedAlterIndexOnTempRegex()))
        {
            blockedToken = "ALTER";
            return true;
        }

        // 6. Blocked procedure tokens
        foreach (var token in SqlBlockedProcedureTokens)
        {
            if (!Regex.IsMatch(script, $@"\b{Regex.Escape(token)}\b", RegexOptions.IgnoreCase))
                continue;

            blockedToken = token;
            return true;
        }

        blockedToken = string.Empty;
        return false;
    }

    /// <summary>
    /// Returns true when every occurrence of <paramref name="anyRegex"/> in <paramref name="script"/>
    /// is covered by at least one of the <paramref name="allowedPatterns"/>.
    /// Used to ensure all DROP/CREATE/ALTER usages target safe objects (#temp).
    /// </summary>
    private static bool AllOccurrencesMatch(string script, Regex anyRegex, params Regex[] allowedPatterns)
    {
        foreach (Match hit in anyRegex.Matches(script))
        {
            // Check the substring starting at this match position
            var remaining = script[hit.Index..];
            var covered = false;
            foreach (var allowed in allowedPatterns)
            {
                var m = allowed.Match(remaining);
                if (m.Success && m.Index == 0)
                {
                    covered = true;
                    break;
                }
            }
            if (!covered)
                return false;
        }
        return true;
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

    /// <summary>Removes SQL single-line (--) and block (/* */) comments so tokens inside comments don't trigger false blocks.</summary>
    internal static string StripSqlComments(string script)
    {
        // Remove block comments (non-greedy, handles nested by repeated pass)
        var result = Regex.Replace(script, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        // Remove single-line comments
        result = Regex.Replace(result, @"--[^\r\n]*", " ");
        return result;
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

    // ── Blanket-blocked statements (no safe variant exists) ────────────────
    [GeneratedRegex(@"(?im)^\s*(?<stmt>UPDATE|DELETE|MERGE|TRUNCATE|GRANT|REVOKE|DENY|KILL|RECONFIGURE|BACKUP|RESTORE)\b")]
    private static partial Regex SqlBlockedStatementRegex();

    // ── INSERT: allow @tableVar / #temp, block real tables ───────────────
    [GeneratedRegex(@"(?im)^\s*INSERT\s+INTO\s+(?![@#])\w")]
    private static partial Regex SqlBlockedInsertIntoRealTableRegex();

    [GeneratedRegex(@"(?im)^\s*INSERT\s+(?!INTO\b)(?![@#])\w")]
    private static partial Regex SqlBlockedInsertDirectRealTableRegex();

    // ── DROP: allow DROP TABLE #temp, block everything else ──────────────
    [GeneratedRegex(@"(?im)\bDROP\s+TABLE\s+(IF\s+EXISTS\s+)?#\w", RegexOptions.None)]
    private static partial Regex SqlAllowedDropTempTableRegex();

    [GeneratedRegex(@"(?im)\bDROP\b")]
    private static partial Regex SqlAnyDropRegex();

    // ── CREATE: allow CREATE TABLE #temp, block everything else ──────────
    [GeneratedRegex(@"(?im)\bCREATE\s+TABLE\s+#\w")]
    private static partial Regex SqlAllowedCreateTempTableRegex();

    [GeneratedRegex(@"(?im)\bCREATE\s+(UNIQUE\s+)?(NONCLUSTERED\s+|CLUSTERED\s+)?INDEX\b[^;]*\bON\s+#\w", RegexOptions.Singleline)]
    private static partial Regex SqlAllowedCreateIndexOnTempRegex();

    [GeneratedRegex(@"(?im)\bCREATE\b")]
    private static partial Regex SqlAnyCreateRegex();

    // ── ALTER: allow ALTER INDEX ... ON #temp, block everything else ─────
    [GeneratedRegex(@"(?im)\bALTER\s+INDEX\b[^;]*\bON\s+#\w", RegexOptions.Singleline)]
    private static partial Regex SqlAllowedAlterIndexOnTempRegex();

    [GeneratedRegex(@"(?im)\bALTER\b")]
    private static partial Regex SqlAnyAlterRegex();

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
