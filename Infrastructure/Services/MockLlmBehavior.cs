using System.Text.RegularExpressions;

namespace Infrastructure.Services;

internal static partial class MockLlmBehavior
{
    private const string DangerousRequestMessage =
        "BLOCKED: DANGEROUS_REQUEST - Only safe read-only diagnostics and inventory questions are allowed.";

    private static readonly string[] GreetingWords =
    [
        "hi", "hello", "hey", "good morning", "good afternoon", "good evening"
    ];

    private static readonly string[] SexualWords =
    [
        "sex", "sexual", "erotic", "porn", "sexting", "nude"
    ];

    private static readonly string[] SqlKeywords =
    [
        "sql", "database", "db", "query", "table", "index", "backup", "restore", "login", "wait", "blocking"
    ];

    private static readonly string[] WindowsKeywords =
    [
        "windows", "server", "service", "process", "event log", "disk", "memory", "cpu", "port", "firewall", "patch"
    ];

    private static readonly string[] SqlDangerousTokens =
    [
        "insert", "update", "delete", "merge", "drop", "alter", "truncate", "create", "backup", "restore",
        "xp_cmdshell", "sp_configure", "kill", "grant", "revoke", "deny"
    ];

    private static readonly string[] WindowsDangerousTokens =
    [
        "shutdown", "poweroff", "restart-computer", "stop-process", "remove-item", "set-itemproperty", "format-volume",
        "stop service", "start service", "restart service", "disable firewall", "enable firewall",
        "create user", "delete user", "change password", "install", "uninstall"
    ];

    private static readonly string[] RestartInquiryTokens =
    [
        "when", "last", "history", "unexpected", "pending", "required", "time", "was", "got"
    ];

    public static string BuildTuneLine(string rawQuestion, string environmentTag, string routedQueryCode)
    {
        var queryCodeEcho = routedQueryCode ?? string.Empty;
        var question = rawQuestion?.Trim() ?? string.Empty;

        // Phase 1: Validation
        if (string.IsNullOrWhiteSpace(question) || IsGreetingOnly(question) || !HasMeaningfulContent(question))
            return $"MISMATCH: EMPTY_OR_UNCLEAR - Please ask a clear ops question with what you want to check.||{queryCodeEcho}";

        if (EnvironmentRules.IsGeneral(environmentTag) && ContainsAny(question, SexualWords))
        {
            return
                $"GENERAL_REFUSAL: I can't help with sexual content. I can help with general, educational, or technical questions instead.||{queryCodeEcho}";
        }

        if (EnvironmentRules.IsSqlServer(environmentTag) && !ContainsAny(question, SqlKeywords))
        {
            return
                $"MISMATCH: SQLSERVER_ONLY - Please ask a SQL Server administration/diagnostics question for this mode.||{queryCodeEcho}";
        }

        if (EnvironmentRules.IsWindows(environmentTag) && !ContainsAny(question, WindowsKeywords))
        {
            return
                $"MISMATCH: WINDOWS_ONLY - Please ask a Windows Server / Infrastructure administration question for this mode.||{queryCodeEcho}";
        }

        if (IsDangerousRequest(question, environmentTag))
            return $"{DangerousRequestMessage}||{queryCodeEcho}";

        // Phase 2: Tuning (grammar + normalization for template/LLM selection).
        var tuned = TuneQuestion(question, environmentTag);
        return $"{tuned}||{queryCodeEcho}";
    }

    public static string BuildGenerateScript(string environmentTag, string tunedQuestion)
    {
        if (EnvironmentRules.IsSqlServer(environmentTag))
        {
            if (tunedQuestion.Contains("SQLGIG", StringComparison.OrdinalIgnoreCase))
                return "SELECT name FROM sys.databases WHERE name LIKE '%SQLGIG%';";

            return "SELECT name FROM sys.databases ORDER BY name;";
        }

        if (EnvironmentRules.IsWindows(environmentTag))
        {
            return """
$TargetServer = $TargetServer
$Result = @()
try {
  $os = Get-CimInstance -ClassName Win32_OperatingSystem -ComputerName $TargetServer -ErrorAction Stop
  $Result += [pscustomobject]@{
    Server = $TargetServer
    Caption = $os.Caption
    Version = $os.Version
    LastBootUpTime = $os.LastBootUpTime
  }
} catch {
  $Result += [pscustomobject]@{
    Server = $TargetServer
    Error = $_.Exception.Message
  }
}
$Result
""";
        }

        return string.Empty;
    }

    private static bool IsDangerousRequest(string question, string environmentTag)
    {
        var normalized = Normalize(question).ToLowerInvariant();

        if (EnvironmentRules.IsSqlServer(environmentTag))
            return IsDangerousSqlRequest(normalized);

        if (EnvironmentRules.IsWindows(environmentTag))
            return IsDangerousWindowsRequest(normalized);

        return false;
    }

    private static bool IsDangerousSqlRequest(string normalized)
    {
        if (ContainsAny(normalized, SqlDangerousTokens))
            return true;

        return ContainsRestartCommand(normalized) && !IsAllowedRestartInquiry(normalized);
    }

    private static bool IsDangerousWindowsRequest(string normalized)
    {
        if (ContainsAny(normalized, WindowsDangerousTokens))
            return true;

        return ContainsRestartCommand(normalized) && !IsAllowedRestartInquiry(normalized);
    }

    private static bool ContainsRestartCommand(string normalized)
    {
        return normalized.Contains("restart", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("reboot", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("shutdown", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("poweroff", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAllowedRestartInquiry(string normalized)
    {
        var startsWithImperative =
            normalized.StartsWith("restart ", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("reboot ", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("shutdown ", StringComparison.OrdinalIgnoreCase);

        if (startsWithImperative)
            return false;

        return ContainsAny(normalized, RestartInquiryTokens);
    }

    private static string TuneQuestion(string question, string environmentTag)
    {
        // Deterministic requested mock output for common SQLGIG demo question.
        if (EnvironmentRules.IsSqlServer(environmentTag) &&
            question.Contains("SQLGIG", StringComparison.OrdinalIgnoreCase))
        {
            return "List databases where name contains SQLGIG.";
        }

        var tuned = Normalize(question);
        tuned = CollapseRepeatedSpaces().Replace(tuned, " ");
        tuned = TheThenPhraseRegex().Replace(tuned, " ");
        tuned = CollapseRepeatedSpaces().Replace(tuned, " ");
        tuned = WindowsWordRegex().Replace(tuned, "Windows");
        tuned = SqlServerWordRegex().Replace(tuned, "SQL Server");
        tuned = tuned.Replace(" the version", " version", StringComparison.OrdinalIgnoreCase);

        if (EnvironmentRules.IsWindows(environmentTag))
        {
            if (ContainsRestartCommand(tuned.ToLowerInvariant()) && IsAllowedRestartInquiry(tuned.ToLowerInvariant()))
                return "Show when Windows was restarted.";

            if (WindowsVersionPattern().IsMatch(tuned))
                return "Give Windows version.";

            if (ServerDetailsPattern().IsMatch(tuned))
                return "Give server details.";
        }

        if (string.IsNullOrWhiteSpace(tuned))
            return "Show system diagnostics.";

        // Sentence casing and punctuation for a clean tuned prompt.
        tuned = char.ToUpperInvariant(tuned[0]) + tuned[1..];
        if (!tuned.EndsWith('.') && !tuned.EndsWith('!'))
            tuned += ".";

        return tuned;
    }

    private static string Normalize(string question)
    {
        return question.Trim().TrimEnd('.', '?', '!', ';');
    }

    private static bool IsGreetingOnly(string question)
    {
        var normalized = question.Trim().ToLowerInvariant();
        return GreetingWords.Any(word => normalized.Equals(word, StringComparison.Ordinal));
    }

    private static bool HasMeaningfulContent(string question)
    {
        return question.Any(char.IsLetterOrDigit);
    }

    private static bool ContainsAny(string value, IEnumerable<string> candidates)
    {
        return candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex CollapseRepeatedSpaces();

    [GeneratedRegex(@"\bwindows\b", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsWordRegex();

    [GeneratedRegex(@"\bsql\s*server\b", RegexOptions.IgnoreCase)]
    private static partial Regex SqlServerWordRegex();

    [GeneratedRegex(@"\b(give|get|show|list)\s+windows(\s+the)?\s+version\b", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsVersionPattern();

    [GeneratedRegex(@"\b(give|get|show|list)\s+server(\s+the)?\s+details\b", RegexOptions.IgnoreCase)]
    private static partial Regex ServerDetailsPattern();

    [GeneratedRegex(@"\bthe\s+then\b", RegexOptions.IgnoreCase)]
    private static partial Regex TheThenPhraseRegex();
}
