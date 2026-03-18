namespace Infrastructure.Services;

/// <summary>
/// Deterministic weighted scoring gate that detects environment mismatches
/// (Windows question in SqlServer env, or vice versa) and ambiguous overlap.
/// Returns ALLOW, BLOCK_ENV_MISMATCH, or NEEDS_CLARIFICATION.
/// </summary>
internal static class EnvironmentIntentService
{
    // ── SQL strong indicators (weight +3 each) ──────────────────────────────
    private static readonly string[] SqlStrongIndicators =
    [
        "sql server", "tsql", "t-sql", "sys.", "dm_", "msdb", "tempdb",
        "agent job", "sql agent", "availability group", "always on",
        "database", "table", "index", "stored procedure", "sp_",
        "serverproperty", "errorlog", "error log", "xp_readerrorlog", "backupset",
        "restore", "blocking", "deadlock", "wait stats", "backup",
        "login", "logins", "sysadmin", "securityadmin", "server role"
    ];

    // ── Windows strong indicators (weight +3 each) ──────────────────────────
    // NOTE: "reboot" is Windows-specific — SQL Server gets "restarted" as a service,
    // not "rebooted". This prevents "when did the server reboot?" from being ambiguous.
    private static readonly string[] WindowsStrongIndicators =
    [
        "windows", "winrm", "wmi", "cim", "powershell", "event log",
        "get-winevent", "wevtutil", "service control manager", "get-service",
        "task scheduler", "hotfix", "kb", "registry", "iis", "firewall",
        "rdp", "dns", "ipconfig", "netstat", "disk", "volume", "drive",
        "partition", "reboot pending", "reboot"
    ];

    // ── Ambiguous overlap terms (weight +1 each) ────────────────────────────
    // These alone should NOT drive mismatch. They trigger NEEDS_CLARIFICATION
    // only when at least 2 are present and no strong indicators exist.
    private static readonly string[] AmbiguousTerms =
    [
        "server", "service", "restart", "log", "error",
        "status", "health", "startup", "down"
    ];

    // ── Broad SQL relevance keywords (not strong enough for mismatch scoring,
    //    but enough to prove the question is in the SQL domain) ───────────────
    private static readonly string[] SqlRelevanceKeywords =
    [
        "sql", "database", "databases", "table", "tables", "index", "indexes",
        "query", "queries", "stored procedure", "agent", "job", "jobs",
        "deadlock", "blocking", "wait", "backup", "restore", "instance",
        "dmv", "sys.", "tempdb", "msdb", "sp_", "dm_", "errorlog",
        "replication", "always on", "availability", "mirroring", "log shipping",
        "transaction", "lock", "performance", "execution plan", "statistics",
        "select", "insert", "update", "delete", "create", "drop", "alter",
        "truncate", "merge", "grant", "revoke", "deny", "kill", "dbcc",
        "schema", "view", "trigger", "function", "cursor", "column", "row",
        "login", "logins", "user", "role", "permission", "principal", "credential",
        "version", "patch", "cumulative update", "service pack", "build", "edition",
        "configuration", "setting", "option", "trace flag", "compatibility", "collation",
        "size", "space", "growth", "shrink", "file", "filegroup", "extent", "page"
    ];

    // ── Broad Windows relevance keywords ─────────────────────────────────────
    private static readonly string[] WindowsRelevanceKeywords =
    [
        "windows", "disk", "drives", "drive", "cpu", "memory", "ram",
        "process", "processes", "service", "services", "event log",
        "firewall", "port", "ports", "iis", "cluster", "patch", "hotfix",
        "reboot", "uptime", "ping", "task", "registry", "dns", "rdp",
        "network", "adapter", "volume", "partition", "scheduled task",
        "powershell", "wmi", "cim", "winrm", "certificate", "ssl", "tls",
        "restart-", "stop-", "start-", "get-", "set-", "new-", "remove-",
        "invoke-", "enable-", "disable-", "install-", "uninstall-",
        "netstat", "ipconfig", "taskkill", "shutdown", "nslookup"
    ];

    private const int StrongWeight = 3;
    private const int AmbiguousWeight = 1;

    // Mismatch threshold: 1 strong indicator (3) on the wrong side with 0 on the right side
    private const int MismatchThreshold = 3;

    // Minimum ambiguous score to trigger NEEDS_CLARIFICATION (require 2+ ambiguous terms)
    private const int AmbiguousClarificationThreshold = 2;

    public static EnvironmentIntentResult Evaluate(string question, string envTag)
    {
        var normalized = (question ?? string.Empty).ToLowerInvariant();

        var sqlScore = CountWeighted(normalized, SqlStrongIndicators, StrongWeight);
        var winScore = CountWeighted(normalized, WindowsStrongIndicators, StrongWeight);
        var ambiguousScore = CountWeighted(normalized, AmbiguousTerms, AmbiguousWeight);

        // "from disk" / "to disk" is T-SQL backup/restore syntax, not a Windows disk reference.
        if (winScore > 0 && normalized.Contains("disk")
            && (normalized.Contains("from disk") || normalized.Contains("to disk")))
        {
            winScore -= StrongWeight;
        }

        // "Windows login", "Windows group", "Windows authentication", "Windows user" are SQL Server
        // concepts (AD-integrated auth), not Windows OS requests.
        if (winScore > 0 && normalized.Contains("windows")
            && (normalized.Contains("login") || normalized.Contains("group login")
                || normalized.Contains("authentication") || normalized.Contains("windows user")
                || normalized.Contains("windows account") || normalized.Contains("ad login")
                || normalized.Contains("ad group") || normalized.Contains("active directory")))
        {
            winScore -= StrongWeight;
            sqlScore += StrongWeight; // boost SQL relevance
        }

        // ── Rule 0: NO RELEVANCE — question has nothing to do with the env ──
        // Check if the question contains ANY keyword relevant to the selected environment.
        // If not, block early — don't waste LLM calls on irrelevant questions.
        var isSqlEnv = EnvironmentRules.IsSqlServer(envTag);
        var relevanceKeywords = isSqlEnv ? SqlRelevanceKeywords : WindowsRelevanceKeywords;
        var hasRelevance = relevanceKeywords.Any(kw =>
            normalized.Contains(kw, StringComparison.OrdinalIgnoreCase));

        // Also count ambiguous terms as partial relevance (e.g. "server", "status", "health")
        if (!hasRelevance && sqlScore == 0 && winScore == 0 && ambiguousScore < AmbiguousClarificationThreshold)
        {
            var expectedType = isSqlEnv ? "SQL Server" : "Windows";
            var alternatives = isSqlEnv
                ? new[]
                {
                    "Show failed SQL Agent jobs",
                    "Check database backup history",
                    "List databases larger than 10 GB"
                }
                : new[]
                {
                    "Check disk space on all drives",
                    "List stopped Windows services",
                    "When was the server last rebooted?"
                };

            return EnvironmentIntentResult.Mismatch(
                $"Your question doesn't appear to be related to {expectedType}. Please ask a {expectedType} diagnostic or inventory question, or switch to the General environment.",
                envTag,
                alternatives);
        }

        // ── Rule 1: CLEAR mismatch ──────────────────────────────────────────
        // At least 1 strong indicator for the wrong env and zero for the right env.
        if (EnvironmentRules.IsSqlServer(envTag) && winScore >= MismatchThreshold && sqlScore == 0)
        {
            return EnvironmentIntentResult.Mismatch(
                "This looks like a Windows-only request. Switch environment to Windows_Live.",
                "Windows_Live",
                [
                    "Switch to Windows_Live environment and ask again.",
                    "Windows: list stopped services",
                    "Windows: check disk space on all drives"
                ]);
        }

        if (EnvironmentRules.IsWindows(envTag) && sqlScore >= MismatchThreshold && winScore == 0)
        {
            return EnvironmentIntentResult.Mismatch(
                "This looks like a SQL Server request. Switch environment to SqlServer_Live.",
                "SqlServer_Live",
                [
                    "Switch to SqlServer_Live environment and ask again.",
                    "SQL: show failed SQL Agent jobs",
                    "SQL: check database backup history"
                ]);
        }

        // ── Rule 2: Ambiguous — both strong scores > 0 and comparable ────────
        // Both sides have strong signals → genuinely mixed question.
        // But if one side dominates (3x or more), the dominant side wins → ALLOW.
        if (sqlScore > 0 && winScore > 0)
        {
            var dominant = Math.Max(sqlScore, winScore);
            var minor = Math.Min(sqlScore, winScore);
            if (dominant < minor * 3)
            {
                // Scores are close enough → genuinely ambiguous
                return BuildClarification(normalized, envTag);
            }
            // One side dominates → fall through to Rule 3 (ALLOW)
        }

        // Only ambiguous terms, no strong signals on either side.
        // Require at least 2 distinct ambiguous terms to avoid over-triggering
        // on single words like "service" in "show stopped services".
        if (ambiguousScore >= AmbiguousClarificationThreshold && sqlScore == 0 && winScore == 0)
        {
            return BuildClarification(normalized, envTag);
        }

        // ── Rule 3: ALLOW ───────────────────────────────────────────────────
        // Strong score matches env, or no meaningful signal at all.
        return EnvironmentIntentResult.Allowed();
    }

    private static EnvironmentIntentResult BuildClarification(string normalized, string envTag)
    {
        string clarifyQuestion;
        string suggestion1;
        string suggestion2;

        if (normalized.Contains("restart"))
        {
            clarifyQuestion = "Did you mean a Windows OS reboot, or a SQL Server service restart?";
            suggestion1 = "Windows: when did the server reboot last?";
            suggestion2 = "SQL: when did SQL Server service restart?";
        }
        else if (normalized.Contains("event log") || normalized.Contains("log"))
        {
            clarifyQuestion = "Did you mean Windows Event Log, or SQL Server Errorlog?";
            suggestion1 = "Windows: check Windows Event Log for errors";
            suggestion2 = "SQL: check SQL Server errorlog for errors";
        }
        else if (normalized.Contains("service"))
        {
            clarifyQuestion = "Did you mean a Windows service, or SQL Server service / SQL Agent?";
            suggestion1 = "Windows: show stopped Windows services";
            suggestion2 = "SQL: is SQL Server Agent running?";
        }
        else
        {
            clarifyQuestion = "Your question could apply to Windows or SQL Server. Which did you mean?";
            suggestion1 = EnvironmentRules.IsWindows(envTag)
                ? "Windows: check server health"
                : "SQL: check SQL Server health";
            suggestion2 = EnvironmentRules.IsWindows(envTag)
                ? "SQL: check SQL Server health"
                : "Windows: check server health";
        }

        return EnvironmentIntentResult.NeedsClarification(
            clarifyQuestion, suggestion1, suggestion2);
    }

    private static int CountWeighted(string text, string[] terms, int weight)
    {
        var score = 0;
        foreach (var term in terms)
        {
            if (text.Contains(term, StringComparison.OrdinalIgnoreCase))
                score += weight;
        }
        return score;
    }
}

/// <summary>Result of environment intent evaluation.</summary>
internal sealed class EnvironmentIntentResult
{
    /// <summary>"ALLOW" | "BLOCK_ENV_MISMATCH" | "NEEDS_CLARIFICATION"</summary>
    public string Verdict { get; init; } = "ALLOW";
    public string? Message { get; init; }
    public string? SuggestedEnvironment { get; init; }
    public string[]? Alternatives { get; init; }
    public string? Suggestion1 { get; init; }
    public string? Suggestion2 { get; init; }

    public bool IsAllowed => Verdict == "ALLOW";
    public bool IsMismatch => Verdict == "BLOCK_ENV_MISMATCH";
    public bool IsClarificationNeeded => Verdict == "NEEDS_CLARIFICATION";

    public static EnvironmentIntentResult Allowed() => new() { Verdict = "ALLOW" };

    public static EnvironmentIntentResult Mismatch(string message, string suggestedEnv, string[] alternatives) =>
        new()
        {
            Verdict = "BLOCK_ENV_MISMATCH",
            Message = message,
            SuggestedEnvironment = suggestedEnv,
            Alternatives = alternatives
        };

    public static EnvironmentIntentResult NeedsClarification(
        string question, string suggestion1, string suggestion2) =>
        new()
        {
            Verdict = "NEEDS_CLARIFICATION",
            Message = question,
            Suggestion1 = suggestion1,
            Suggestion2 = suggestion2
        };
}
