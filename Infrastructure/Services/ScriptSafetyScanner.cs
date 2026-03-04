using System.Text.RegularExpressions;

namespace Infrastructure.Services;

internal sealed class ScriptSafetyScanner
{
    private static readonly string[] SqlBlockedTokens =
    [
        "DELETE",
        "UPDATE",
        "INSERT",
        "MERGE",
        "DROP",
        "ALTER",
        "TRUNCATE",
        "EXEC",
        "xp_cmdshell",
        "sp_configure",
        "RECONFIGURE",
        "KILL"
    ];

    private static readonly string[] WindowsBlockedCommands =
    [
        "Remove-Item",
        "Set-ItemProperty",
        "Restart-Computer",
        "Stop-Computer",
        "Stop-Process",
        "Format-Volume"
    ];

    private static readonly HashSet<string> SafeWindowsNewOrSetCmdlets = new(StringComparer.OrdinalIgnoreCase)
    {
        "New-TimeSpan",
        "New-Object",
        "Set-StrictMode"
    };

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

        if (EnvironmentRules.IsSqlServer(environment) || string.Equals(scriptLanguage, "SQL", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var token in SqlBlockedTokens)
            {
                if (ContainsToken(sanitized, token))
                {
                    return new SafetyScanResult
                    {
                        IsSafe = false,
                        SanitizedScript = sanitized,
                        BlockedToken = token
                    };
                }
            }
        }

        if (EnvironmentRules.IsWindows(environment) || string.Equals(scriptLanguage, "PS", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var token in WindowsBlockedCommands)
            {
                if (ContainsToken(sanitized, token))
                {
                    return new SafetyScanResult
                    {
                        IsSafe = false,
                        SanitizedScript = sanitized,
                        BlockedToken = token
                    };
                }
            }

            var commandMatches = Regex.Matches(
                sanitized,
                @"\b(?:Disable|Enable|New|Set)-[A-Za-z0-9]+\b",
                RegexOptions.IgnoreCase);

            foreach (Match match in commandMatches)
            {
                var cmdlet = match.Value;
                if (SafeWindowsNewOrSetCmdlets.Contains(cmdlet))
                    continue;

                return new SafetyScanResult
                {
                    IsSafe = false,
                    SanitizedScript = sanitized,
                    BlockedToken = cmdlet
                };
            }
        }

        return new SafetyScanResult
        {
            IsSafe = true,
            SanitizedScript = sanitized
        };
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

    private static string StripCodeFences(string script)
    {
        var text = (script ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        // Removes wrapping fences like ```sql ... ``` or ```powershell ... ```
        text = Regex.Replace(text, @"^\s*```[\w-]*\s*\r?\n", string.Empty, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\r?\n\s*```\s*$", string.Empty, RegexOptions.IgnoreCase);
        return text.Trim();
    }
}

internal sealed class SafetyScanResult
{
    public bool IsSafe { get; init; }
    public string SanitizedScript { get; init; } = string.Empty;
    public string? BlockedToken { get; init; }
}
