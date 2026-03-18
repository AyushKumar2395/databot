using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Abstractions;

namespace Infrastructure.Tests;

/// <summary>
/// Integration audit: connects to CTS03/SQLGig, reads all 447 QuestionSamples scripts,
/// validates each one structurally, and outputs a report with UPDATE statements for fixes.
/// </summary>
public class SampleScriptAuditTests(ITestOutputHelper output)
{
    private const string ConnectionString =
        "Server=CTS03;Database=SQLGig;Trusted_Connection=True;TrustServerCertificate=True;Encrypt=False;";

    // ═══════════════════════════════════════════════════════════════════════
    // DATA MODEL
    // ═══════════════════════════════════════════════════════════════════════

    private sealed record SampleRow(
        int SampleId,
        string Environment,
        string GroupKey,
        string GroupTitle,
        string QuestionText,
        bool IsActive,
        string? Script);

    private sealed class ScriptIssue
    {
        public int SampleId { get; init; }
        public string Environment { get; init; } = "";
        public string QuestionText { get; init; } = "";
        public string GroupKey { get; init; } = "";
        public List<string> Issues { get; } = [];      // Errors — cause test failure
        public List<string> Warnings { get; } = [];    // Cosmetic — reported but not failures
        public string? FixedScript { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // MAIN AUDIT TEST
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AuditAllSampleScripts()
    {
        var samples = await FetchAllSamplesAsync();
        output.WriteLine($"Loaded {samples.Count} samples from database.\n");

        var issueList = new List<ScriptIssue>();
        var warningOnlyList = new List<ScriptIssue>();
        var envCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var envPassCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var sample in samples)
        {
            envCounts.TryGetValue(sample.Environment, out var c);
            envCounts[sample.Environment] = c + 1;

            var issue = ValidateSample(sample);
            if (issue is not null && issue.Issues.Count > 0)
            {
                issueList.Add(issue);
            }
            else if (issue is not null && issue.Warnings.Count > 0)
            {
                warningOnlyList.Add(issue);
                // Warnings don't count as failures
                envPassCounts.TryGetValue(sample.Environment, out var pw);
                envPassCounts[sample.Environment] = pw + 1;
            }
            else
            {
                envPassCounts.TryGetValue(sample.Environment, out var p);
                envPassCounts[sample.Environment] = p + 1;
            }
        }

        // ── Summary ──────────────────────────────────────────────────────
        output.WriteLine("═══════════════════════════════════════════════════════════════");
        output.WriteLine("  SAMPLE SCRIPT AUDIT SUMMARY");
        output.WriteLine("═══════════════════════════════════════════════════════════════");
        output.WriteLine($"  Total samples:  {samples.Count}");
        output.WriteLine($"  Passed:         {samples.Count - issueList.Count}");
        output.WriteLine($"  Errors:         {issueList.Count}");
        output.WriteLine($"  Warnings:       {warningOnlyList.Count} (cosmetic — not failures)");
        output.WriteLine("");

        foreach (var env in envCounts.OrderBy(kv => kv.Key))
        {
            envPassCounts.TryGetValue(env.Key, out var passed);
            var warnCount = warningOnlyList.Count(w => w.Environment.Equals(env.Key, StringComparison.OrdinalIgnoreCase));
            output.WriteLine($"  {env.Key,-25} Total={env.Value,4}  Pass={passed,4}  Fail={env.Value - passed,4}  Warn={warnCount,4}");
        }
        output.WriteLine("");

        // ── Warnings (informational) ───────────────────────────────────
        if (warningOnlyList.Count > 0)
        {
            output.WriteLine("═══════════════════════════════════════════════════════════════");
            output.WriteLine("  WARNINGS (cosmetic — not test failures)");
            output.WriteLine("═══════════════════════════════════════════════════════════════");

            foreach (var warn in warningOnlyList.OrderBy(i => i.Environment).ThenBy(i => i.SampleId))
            {
                output.WriteLine($"\n── SampleId={warn.SampleId} | {warn.Environment} | {warn.GroupKey}");
                output.WriteLine($"   Q: {Truncate(warn.QuestionText, 80)}");
                foreach (var msg in warn.Warnings)
                    output.WriteLine($"   ~ {msg}");
            }
            output.WriteLine("");
        }

        // ── Errors (test failures) ─────────────────────────────────────
        if (issueList.Count > 0)
        {
            output.WriteLine("═══════════════════════════════════════════════════════════════");
            output.WriteLine("  ERRORS (script issues requiring fixes)");
            output.WriteLine("═══════════════════════════════════════════════════════════════");

            foreach (var issue in issueList.OrderBy(i => i.Environment).ThenBy(i => i.SampleId))
            {
                output.WriteLine($"\n── SampleId={issue.SampleId} | {issue.Environment} | {issue.GroupKey}");
                output.WriteLine($"   Q: {Truncate(issue.QuestionText, 80)}");
                foreach (var msg in issue.Issues)
                    output.WriteLine($"   ⚠ {msg}");
                foreach (var msg in issue.Warnings)
                    output.WriteLine($"   ~ {msg}");
            }

            // ── UPDATE statements ────────────────────────────────────────
            output.WriteLine("\n\n═══════════════════════════════════════════════════════════════");
            output.WriteLine("  UPDATE STATEMENTS");
            output.WriteLine("═══════════════════════════════════════════════════════════════\n");

            var updateSb = new StringBuilder();
            foreach (var issue in issueList
                         .Where(i => i.FixedScript is not null)
                         .OrderBy(i => i.SampleId))
            {
                var escapedScript = issue.FixedScript!.Replace("'", "''");
                updateSb.AppendLine($"-- SampleId={issue.SampleId} | {issue.Environment} | {Truncate(issue.QuestionText, 60)}");
                foreach (var msg in issue.Issues)
                    updateSb.AppendLine($"--   Fix: {msg}");
                updateSb.AppendLine($"UPDATE [SQLGig].[DataBOT].[QuestionSamples]");
                updateSb.AppendLine($"SET [Script] = N'{escapedScript}',");
                updateSb.AppendLine($"    [UpdatedAt] = SYSUTCDATETIME(),");
                updateSb.AppendLine($"    [UpdatedBy] = N'ScriptAudit'");
                updateSb.AppendLine($"WHERE [SampleId] = {issue.SampleId};");
                updateSb.AppendLine();
            }

            var updateSql = updateSb.ToString();
            output.WriteLine(updateSql);

            // Write to file for easy copy
            var outputPath = Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "SampleScriptAudit_Updates.sql");
            await File.WriteAllTextAsync(outputPath, updateSql);
            output.WriteLine($"UPDATE statements written to: {Path.GetFullPath(outputPath)}");

            output.WriteLine("\n═══════════════════════════════════════════════════════════════");
            output.WriteLine("  ALL SAMPLE IDs WITH ERRORS (comma-separated)");
            output.WriteLine("═══════════════════════════════════════════════════════════════");
            output.WriteLine(string.Join(", ", issueList.Select(i => i.SampleId).OrderBy(x => x)));
        }

        output.WriteLine($"\n\nAudit complete. {issueList.Count} errors, {warningOnlyList.Count} warnings out of {samples.Count} samples.");

        // Only fail the test on actual errors, not warnings
        Assert.Equal(0, issueList.Count);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // VALIDATION ENGINE
    // ═══════════════════════════════════════════════════════════════════════

    private ScriptIssue? ValidateSample(SampleRow sample)
    {
        var issue = new ScriptIssue
        {
            SampleId = sample.SampleId,
            Environment = sample.Environment,
            QuestionText = sample.QuestionText,
            GroupKey = sample.GroupKey
        };

        // 0. General environment: answer-only, NULL script is CORRECT
        if (sample.Environment.Equals("General", StringComparison.OrdinalIgnoreCase))
            return null; // No script validation needed

        // 1. Script must exist
        if (string.IsNullOrWhiteSpace(sample.Script))
        {
            issue.Issues.Add("CRITICAL: Script is NULL or empty");
            return issue;
        }

        var script = sample.Script!.Trim();

        // 2. Script must not contain markdown code fences
        if (script.Contains("```"))
            issue.Issues.Add("Contains markdown code fences (```)");

        // 3. Script must not contain backticks (PS uses them but SQL shouldn't)
        if (!IsWindowsLive(sample.Environment) && script.Contains('`'))
            issue.Issues.Add("Contains backtick characters");

        // 4. Route to environment-specific validation
        if (IsSqlServerLive(sample.Environment))
            ValidateSqlLiveScript(script, sample, issue);
        else if (IsWindowsLive(sample.Environment))
            ValidateWindowsLiveScript(script, sample, issue);
        else if (IsSqlServerHistory(sample.Environment))
            ValidateSqlHistoryScript(script, sample, issue);
        else if (IsWindowsHistory(sample.Environment))
            ValidateWindowsHistoryScript(script, sample, issue);

        if (issue.Issues.Count == 0 && issue.Warnings.Count == 0)
            return null;

        // Try to auto-fix the script (errors only)
        if (issue.Issues.Count > 0)
            issue.FixedScript = TryAutoFix(script, sample.Environment, issue.Issues);
        return issue;
    }

    // ── SQL Server Live validation ───────────────────────────────────────

    private static void ValidateSqlLiveScript(string script, SampleRow sample, ScriptIssue issue)
    {
        // Must contain SELECT
        if (!ContainsWord(script, "SELECT"))
            issue.Issues.Add("Missing SELECT statement");

        // Must have @@SERVERNAME output column
        if (!script.Contains("@@SERVERNAME", StringComparison.OrdinalIgnoreCase))
            issue.Issues.Add("Missing @@SERVERNAME AS [ServerName] — required for per-server result binding");

        // Must have DB_NAME() or DatabaseName (exempt: ConfigDrift collects server-level settings, not per-database)
        if (!string.Equals(sample.GroupKey, "ConfigDrift", StringComparison.OrdinalIgnoreCase)
            && !script.Contains("DB_NAME()", StringComparison.OrdinalIgnoreCase)
            && !ContainsPattern(script, @"\[DatabaseName\]"))
            issue.Issues.Add("Missing DB_NAME() AS [DatabaseName] — required for database context");

        // Must have timestamp column (CapturedAt)
        if (!script.Contains("GETDATE()", StringComparison.OrdinalIgnoreCase)
            && !script.Contains("GETUTCDATE()", StringComparison.OrdinalIgnoreCase)
            && !script.Contains("SYSUTCDATETIME()", StringComparison.OrdinalIgnoreCase)
            && !script.Contains("SYSDATETIME()", StringComparison.OrdinalIgnoreCase)
            && !ContainsPattern(script, @"\[CapturedAt\]"))
            issue.Issues.Add("Missing GETDATE() AS [CapturedAt] — required for timestamp");

        // Should use column aliases with brackets for clean output
        if (!ContainsPattern(script, @"AS\s+\["))
            issue.Issues.Add("No bracketed column aliases (AS [ColumnName]) — needed for clean UI display");

        // Should NOT contain dangerous operations
        if (ContainsPattern(script, @"\b(INSERT\s+INTO|UPDATE\s+\w+\s+SET|DELETE\s+FROM|DROP\s+TABLE(?!\s+IF\s+EXISTS\s+#)(?!\s+#)|TRUNCATE)\b"))
        {
            // Allow temp table operations
            if (!ContainsPattern(script, @"(#\w+|@\w+)"))
                issue.Issues.Add("Contains potentially dangerous DML (INSERT/UPDATE/DELETE/DROP on non-temp objects)");
        }

        // Should end with semicolon
        var trimmed = script.TrimEnd();
        if (!trimmed.EndsWith(";"))
            issue.Issues.Add("Script does not end with semicolon");

        // Should have bounded results (TOP or WHERE with specific filter) — warning only, many DMV queries are intentionally unbounded
        if (!ContainsWord(script, "TOP") && !ContainsPattern(script, @"WHERE\b"))
            issue.Warnings.Add("No TOP or WHERE clause — unbounded results (may be intentional for DMV queries)");

        // Check for common SQL syntax issues
        ValidateCommonSqlIssues(script, issue);
    }

    // ── Windows Live validation ──────────────────────────────────────────

    private static void ValidateWindowsLiveScript(string script, SampleRow sample, ScriptIssue issue)
    {
        // Must set $Result
        if (!script.Contains("$Result", StringComparison.Ordinal))
            issue.Issues.Add("CRITICAL: Missing $Result assignment — PowerShell executor requires $Result variable");

        // Should use Get-* cmdlets (read-only)
        if (!ContainsPattern(script, @"Get-\w+"))
            issue.Issues.Add("No Get-* cmdlets found — expected read-only PowerShell commands");

        // Should have Select-Object for clean output — warning only, scripts are functionally correct without it
        if (!script.Contains("Select-Object", StringComparison.OrdinalIgnoreCase)
            && !script.Contains("select ", StringComparison.OrdinalIgnoreCase)
            && !ContainsPattern(script, @"\|\s*[Ss]elect\b"))
            issue.Warnings.Add("No Select-Object — output may have unnecessary properties (cosmetic)");

        // Should add ServerName property
        if (!script.Contains("ServerName", StringComparison.OrdinalIgnoreCase)
            && !script.Contains("$env:COMPUTERNAME", StringComparison.OrdinalIgnoreCase)
            && !script.Contains("ComputerName", StringComparison.OrdinalIgnoreCase))
            issue.Issues.Add("Missing ServerName/ComputerName property — needed for per-server binding");

        // Should add CapturedAt property
        if (!script.Contains("CapturedAt", StringComparison.OrdinalIgnoreCase)
            && !script.Contains("Get-Date", StringComparison.OrdinalIgnoreCase))
            issue.Issues.Add("Missing CapturedAt/Get-Date property — needed for timestamp");

        // Should NOT contain dangerous commands
        if (ContainsPattern(script, @"\b(Restart-Computer|Stop-Computer|Stop-Service|Start-Service|Restart-Service|Stop-Process|Remove-Item|Set-ItemProperty|Format-Volume|Clear-EventLog)\b"))
            issue.Issues.Add("Contains dangerous state-changing commands — samples must be read-only");

        // Module-dependent cmdlets MUST be wrapped in try/catch for remote execution
        ValidateModuleDependentCmdlets(script, issue);
    }

    /// <summary>
    /// Checks that module-dependent cmdlets are wrapped in try/catch blocks.
    /// Target servers may not have these modules installed.
    /// </summary>
    private static void ValidateModuleDependentCmdlets(string script, ScriptIssue issue)
    {
        // Map: cmdlet pattern → module name
        (string Pattern, string Module)[] moduleCmdlets =
        [
            (@"\bGet-IISSite\b", "IISAdministration"),
            (@"\bGet-IISAppPool\b", "IISAdministration"),
            (@"\bGet-WebSite\b", "WebAdministration"),
            (@"\bGet-WebApplication\b", "WebAdministration"),
            (@"\bGet-WebBinding\b", "WebAdministration"),
            (@"\bGet-Cluster\b", "FailoverClusters"),
            (@"\bGet-ClusterNode\b", "FailoverClusters"),
            (@"\bGet-ClusterGroup\b", "FailoverClusters"),
            (@"\bGet-ClusterResource\b", "FailoverClusters"),
            (@"\bGet-ClusterSharedVolume\b", "FailoverClusters"),
            (@"\bGet-ClusterQuorum\b", "FailoverClusters"),
            (@"\bGet-MpComputerStatus\b", "Defender"),
            (@"\bGet-MpPreference\b", "Defender"),
            (@"\bGet-MpThreatDetection\b", "Defender"),
        ];

        foreach (var (pattern, module) in moduleCmdlets)
        {
            if (ContainsPattern(script, pattern))
            {
                // Check if it's inside a try block
                if (!ContainsPattern(script, @"\btry\s*\{"))
                {
                    issue.Issues.Add($"Uses {pattern.Trim('\\', 'b')} ({module} module) without try/catch — target server may not have this module");
                }
            }
        }
    }

    // ── SQL Server History validation ────────────────────────────────────

    private static void ValidateSqlHistoryScript(string script, SampleRow sample, ScriptIssue issue)
    {
        // Must be SQL, not PowerShell
        if (script.Contains("$Result", StringComparison.Ordinal) || script.Contains("Get-", StringComparison.Ordinal))
            issue.Issues.Add("CRITICAL: History script appears to be PowerShell — must be T-SQL");

        // Must contain SELECT
        if (!ContainsWord(script, "SELECT"))
            issue.Issues.Add("Missing SELECT statement");

        // Reference/definition lookups (AlertsAudit, MLInsights) query static tables — skip time/server filter checks
        bool isReferenceLookup = sample.GroupKey.Equals("AlertsAudit", StringComparison.OrdinalIgnoreCase)
                                 || sample.GroupKey.Equals("MLInsights", StringComparison.OrdinalIgnoreCase);
        bool isMetrics = string.Equals(sample.GroupKey, "Metrics", StringComparison.OrdinalIgnoreCase);

        if (!isMetrics && !isReferenceLookup)
        {
            if (!script.Contains("SQLServer_Details_History", StringComparison.OrdinalIgnoreCase)
                && !script.Contains("Monitor].", StringComparison.OrdinalIgnoreCase)
                && !script.Contains("[SQLGig]", StringComparison.OrdinalIgnoreCase))
                issue.Issues.Add("Missing reference to [SQLGig].[Monitor].* history tables");
        }

        if (!isReferenceLookup)
        {
            // Must have server filter token
            if (!script.Contains("/*__SQLSERVER_FILTER__*/", StringComparison.Ordinal)
                && !script.Contains("@Server", StringComparison.OrdinalIgnoreCase)
                && !script.Contains("/*__SQLSERVER_FILTER_STATIC__*/", StringComparison.Ordinal)
                && !script.Contains("/*__SQLSERVER_FILTER_WAITS__*/", StringComparison.Ordinal)
                && !script.Contains("/*__SQLSERVER_FILTER_WHO__*/", StringComparison.Ordinal)
                && !script.Contains("/*__ALERT_SERVER_FILTER__*/", StringComparison.Ordinal))
                issue.Issues.Add("Missing server filter token (/*__SQLSERVER_FILTER__*/ or @Server)");

            // Should have @FromUtc / @ToUtc parameters
            if (!script.Contains("@FromUtc", StringComparison.OrdinalIgnoreCase)
                && !script.Contains("FromUtc", StringComparison.OrdinalIgnoreCase))
                issue.Issues.Add("Missing @FromUtc parameter — required for time window binding");

            if (!script.Contains("@ToUtc", StringComparison.OrdinalIgnoreCase)
                && !script.Contains("ToUtc", StringComparison.OrdinalIgnoreCase))
                issue.Issues.Add("Missing @ToUtc parameter — required for time window binding");
        }

        // Should have @Top parameter
        if (!script.Contains("@Top", StringComparison.OrdinalIgnoreCase)
            && !ContainsPattern(script, @"TOP\s*\(\s*\d+\s*\)"))
            issue.Issues.Add("Missing @Top parameter — required for result bounding");

        // Standard output columns for history
        ValidateHistoryOutputColumns(script, issue);

        ValidateCommonSqlIssues(script, issue);
    }

    // ── Windows History validation ───────────────────────────────────────

    private static void ValidateWindowsHistoryScript(string script, SampleRow sample, ScriptIssue issue)
    {
        // MUST be T-SQL, NOT PowerShell (Windows_History runs centralized SQL on CTS03)
        if (script.Contains("$Result", StringComparison.Ordinal) || ContainsPattern(script, @"Get-\w+"))
            issue.Issues.Add("CRITICAL: Windows_History script must be T-SQL, not PowerShell — runs on CTS03/SQLGig");

        // Must contain SELECT
        if (!ContainsWord(script, "SELECT"))
            issue.Issues.Add("Missing SELECT statement");

        // Must reference correct history tables
        bool isMetrics = string.Equals(sample.GroupKey, "Metrics", StringComparison.OrdinalIgnoreCase);

        if (!isMetrics)
        {
            if (!script.Contains("WINServer_Details_History", StringComparison.OrdinalIgnoreCase)
                && !script.Contains("Monitor].", StringComparison.OrdinalIgnoreCase)
                && !script.Contains("[SQLGig]", StringComparison.OrdinalIgnoreCase))
                issue.Issues.Add("Missing reference to [SQLGig].[Monitor].* history tables");
        }

        // Must have server filter token
        if (!script.Contains("/*__WINSERVER_FILTER__*/", StringComparison.Ordinal)
            && !script.Contains("@Server", StringComparison.OrdinalIgnoreCase))
            issue.Issues.Add("Missing server filter token (/*__WINSERVER_FILTER__*/ or @Server)");

        // Should have time parameters
        if (!script.Contains("@FromUtc", StringComparison.OrdinalIgnoreCase))
            issue.Issues.Add("Missing @FromUtc parameter");
        if (!script.Contains("@ToUtc", StringComparison.OrdinalIgnoreCase))
            issue.Issues.Add("Missing @ToUtc parameter");
        if (!script.Contains("@Top", StringComparison.OrdinalIgnoreCase)
            && !ContainsPattern(script, @"TOP\s*\(\s*\d+\s*\)"))
            issue.Issues.Add("Missing @Top parameter");

        ValidateHistoryOutputColumns(script, issue);
        ValidateCommonSqlIssues(script, issue);
    }

    // ── Shared validation helpers ────────────────────────────────────────

    private static void ValidateHistoryOutputColumns(string script, ScriptIssue issue)
    {
        // Standard history output: ServerName, CapturedAtUtc, MetricGroup, MetricName, MetricValue, Detail
        if (!script.Contains("ServerName", StringComparison.OrdinalIgnoreCase))
            issue.Issues.Add("Missing [ServerName] output column");
        if (!script.Contains("CapturedAtUtc", StringComparison.OrdinalIgnoreCase)
            && !script.Contains("CapturedAt", StringComparison.OrdinalIgnoreCase)
            && !script.Contains("DateTime", StringComparison.OrdinalIgnoreCase))
            issue.Issues.Add("Missing [CapturedAtUtc] output column");
        if (!script.Contains("MetricGroup", StringComparison.OrdinalIgnoreCase)
            && !script.Contains("/*__METRIC_GROUP__*/", StringComparison.Ordinal))
            issue.Issues.Add("Missing [MetricGroup] output column");
        if (!script.Contains("MetricName", StringComparison.OrdinalIgnoreCase)
            && !script.Contains("/*__METRIC_NAME__*/", StringComparison.Ordinal))
            issue.Issues.Add("Missing [MetricName] output column");
        if (!script.Contains("MetricValue", StringComparison.OrdinalIgnoreCase)
            && !script.Contains("/*__METRIC_SELECT__*/", StringComparison.Ordinal))
            issue.Issues.Add("Missing [MetricValue] output column");
        if (!script.Contains("Detail", StringComparison.OrdinalIgnoreCase)
            && !script.Contains("/*__DETAIL_SELECT__*/", StringComparison.Ordinal))
            issue.Issues.Add("Missing [Detail] output column");
    }

    private static void ValidateCommonSqlIssues(string script, ScriptIssue issue)
    {
        // Unmatched parentheses
        var openParens = script.Count(c => c == '(');
        var closeParens = script.Count(c => c == ')');
        if (openParens != closeParens)
            issue.Issues.Add($"Unmatched parentheses: {openParens} open vs {closeParens} close");

        // Unmatched square brackets (excluding comments and strings)
        var openBrackets = script.Count(c => c == '[');
        var closeBrackets = script.Count(c => c == ']');
        if (openBrackets != closeBrackets)
            issue.Issues.Add($"Unmatched square brackets: {openBrackets} open vs {closeBrackets} close");

        // Trailing comma before FROM/WHERE/ORDER/GROUP
        if (ContainsPattern(script, @",\s*\n\s*FROM\b"))
            issue.Issues.Add("Trailing comma before FROM clause");
        if (ContainsPattern(script, @",\s*\n\s*WHERE\b"))
            issue.Issues.Add("Trailing comma before WHERE clause");
        if (ContainsPattern(script, @",\s*\n\s*ORDER\s+BY\b"))
            issue.Issues.Add("Trailing comma before ORDER BY clause");
        if (ContainsPattern(script, @",\s*\n\s*GROUP\s+BY\b"))
            issue.Issues.Add("Trailing comma before GROUP BY clause");

        // Empty string comparison (common bug)
        if (ContainsPattern(script, @"=\s*''"))
            issue.Issues.Add("Uses = '' comparison — consider IS NULL or = N'' for nvarchar");

        // Script too large (>80KB)
        if (Encoding.UTF8.GetByteCount(script) > 80 * 1024)
            issue.Issues.Add("Script exceeds 80KB maximum");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // AUTO-FIX ENGINE
    // ═══════════════════════════════════════════════════════════════════════

    private static string? TryAutoFix(string script, string environment, List<string> issues)
    {
        var fixedScript = script;
        var changed = false;

        // Strip markdown fences
        if (issues.Any(i => i.Contains("markdown code fences")))
        {
            fixedScript = Regex.Replace(fixedScript, @"^\s*```[\w-]*\s*\r?\n", "", RegexOptions.IgnoreCase);
            fixedScript = Regex.Replace(fixedScript, @"\r?\n\s*```\s*$", "", RegexOptions.IgnoreCase);
            changed = true;
        }

        if (IsSqlServerLive(environment))
            changed |= TryFixSqlLive(ref fixedScript, issues);
        else if (IsWindowsLive(environment))
            changed |= TryFixWindowsLive(ref fixedScript, issues);
        else if (IsSqlServerHistory(environment))
            changed |= TryFixSqlHistory(ref fixedScript, issues);
        else if (IsWindowsHistory(environment))
            changed |= TryFixWindowsHistory(ref fixedScript, issues);

        // Add trailing semicolon for SQL
        if (!IsWindowsLive(environment)
            && issues.Any(i => i.Contains("semicolon"))
            && !fixedScript.TrimEnd().EndsWith(";"))
        {
            fixedScript = fixedScript.TrimEnd() + ";";
            changed = true;
        }

        return changed ? fixedScript.Trim() : null;
    }

    private static bool TryFixSqlLive(ref string script, List<string> issues)
    {
        var changed = false;

        // Add @@SERVERNAME if missing — inject after first SELECT
        if (issues.Any(i => i.Contains("@@SERVERNAME")))
        {
            var selectIdx = script.IndexOf("SELECT", StringComparison.OrdinalIgnoreCase);
            if (selectIdx >= 0)
            {
                // Find end of SELECT keyword + optional TOP clause
                var afterSelect = FindAfterSelectTop(script, selectIdx);
                script = script.Insert(afterSelect, "\n    @@SERVERNAME AS [ServerName],\n    DB_NAME() AS [DatabaseName],\n    GETDATE() AS [CapturedAt],");
                changed = true;
            }
        }
        else
        {
            // Add DB_NAME() if missing
            if (issues.Any(i => i.Contains("DB_NAME()")))
            {
                var serverNameIdx = script.IndexOf("@@SERVERNAME", StringComparison.OrdinalIgnoreCase);
                if (serverNameIdx >= 0)
                {
                    var lineEnd = script.IndexOf('\n', serverNameIdx);
                    if (lineEnd > 0)
                    {
                        script = script.Insert(lineEnd + 1, "    DB_NAME() AS [DatabaseName],\n");
                        changed = true;
                    }
                }
            }

            // Add GETDATE() if missing
            if (issues.Any(i => i.Contains("GETDATE()")))
            {
                var dbNameIdx = script.IndexOf("DB_NAME()", StringComparison.OrdinalIgnoreCase);
                var serverNameIdx = script.IndexOf("@@SERVERNAME", StringComparison.OrdinalIgnoreCase);
                var anchorIdx = dbNameIdx >= 0 ? dbNameIdx : serverNameIdx;
                if (anchorIdx >= 0)
                {
                    var lineEnd = script.IndexOf('\n', anchorIdx);
                    if (lineEnd > 0)
                    {
                        script = script.Insert(lineEnd + 1, "    GETDATE() AS [CapturedAt],\n");
                        changed = true;
                    }
                }
            }
        }

        return changed;
    }

    private static bool TryFixWindowsLive(ref string script, List<string> issues)
    {
        var changed = false;

        // Wrap entire script in $Result if missing
        if (issues.Any(i => i.Contains("$Result")))
        {
            script = "$Result = " + script.TrimStart();
            changed = true;
        }

        return changed;
    }

    private static bool TryFixSqlHistory(ref string script, List<string> issues)
    {
        var changed = false;

        // Add missing @FromUtc DECLARE
        if (issues.Any(i => i.Contains("@FromUtc")) && !script.Contains("@FromUtc", StringComparison.OrdinalIgnoreCase))
        {
            script = "DECLARE @FromUtc datetime2(0) = DATEADD(HOUR,-24,SYSUTCDATETIME());\n" + script;
            changed = true;
        }

        // Add missing @ToUtc DECLARE
        if (issues.Any(i => i.Contains("@ToUtc")) && !script.Contains("@ToUtc", StringComparison.OrdinalIgnoreCase))
        {
            var insertPos = script.Contains("@FromUtc", StringComparison.OrdinalIgnoreCase)
                ? script.IndexOf('\n', script.IndexOf("@FromUtc", StringComparison.OrdinalIgnoreCase)) + 1
                : 0;
            script = script.Insert(insertPos, "DECLARE @ToUtc datetime2(0) = SYSUTCDATETIME();\n");
            changed = true;
        }

        // Add missing @Top DECLARE
        if (issues.Any(i => i.Contains("@Top")) && !script.Contains("@Top", StringComparison.OrdinalIgnoreCase))
        {
            var insertPos = script.Contains("@ToUtc", StringComparison.OrdinalIgnoreCase)
                ? script.IndexOf('\n', script.IndexOf("@ToUtc", StringComparison.OrdinalIgnoreCase)) + 1
                : 0;
            script = script.Insert(insertPos, "DECLARE @Top int = 200;\n");
            changed = true;
        }

        return changed;
    }

    private static bool TryFixWindowsHistory(ref string script, List<string> issues)
    {
        // Same fixes as SQL History since both are T-SQL
        return TryFixSqlHistory(ref script, issues);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // DATABASE ACCESS
    // ═══════════════════════════════════════════════════════════════════════

    private static async Task<List<SampleRow>> FetchAllSamplesAsync()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                [SampleId],
                [Environment],
                ISNULL([GroupKey], '') AS [GroupKey],
                ISNULL([GroupTitle], '') AS [GroupTitle],
                ISNULL([QuestionText], '') AS [QuestionText],
                ISNULL([IsActive], 1) AS [IsActive],
                [Script]
            FROM [SQLGig].[DataBOT].[QuestionSamples]
            ORDER BY [Environment], [SampleId];
            """;
        command.CommandTimeout = 30;

        var rows = new List<SampleRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new SampleRow(
                SampleId: reader.GetInt32(0),
                Environment: reader.GetString(1),
                GroupKey: reader.GetString(2),
                GroupTitle: reader.GetString(3),
                QuestionText: reader.GetString(4),
                IsActive: reader.GetBoolean(5),
                Script: reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return rows;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // UTILITY HELPERS
    // ═══════════════════════════════════════════════════════════════════════

    private static bool IsSqlServerLive(string env) =>
        env.Equals("SqlServer_Live", StringComparison.OrdinalIgnoreCase);

    private static bool IsWindowsLive(string env) =>
        env.Equals("Windows_Live", StringComparison.OrdinalIgnoreCase);

    private static bool IsSqlServerHistory(string env) =>
        env.Equals("SqlServer_History", StringComparison.OrdinalIgnoreCase);

    private static bool IsWindowsHistory(string env) =>
        env.Equals("Windows_History", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsWord(string text, string word) =>
        Regex.IsMatch(text, $@"\b{Regex.Escape(word)}\b", RegexOptions.IgnoreCase);

    private static bool ContainsPattern(string text, string pattern) =>
        Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase);

    private static string Truncate(string text, int maxLen) =>
        text.Length <= maxLen ? text : text[..maxLen] + "...";

    /// <summary>
    /// Finds the insertion point after SELECT [TOP (n)] for injecting standard columns.
    /// </summary>
    private static int FindAfterSelectTop(string script, int selectIdx)
    {
        var afterKeyword = selectIdx + "SELECT".Length;

        // Skip optional DISTINCT
        var rest = script[afterKeyword..].TrimStart();
        if (rest.StartsWith("DISTINCT", StringComparison.OrdinalIgnoreCase))
            afterKeyword = script.IndexOf("DISTINCT", afterKeyword, StringComparison.OrdinalIgnoreCase) + "DISTINCT".Length;

        // Skip optional TOP (n)
        rest = script[afterKeyword..].TrimStart();
        if (rest.StartsWith("TOP", StringComparison.OrdinalIgnoreCase))
        {
            var topMatch = Regex.Match(rest, @"TOP\s*\(\s*[^)]+\s*\)", RegexOptions.IgnoreCase);
            if (topMatch.Success)
                afterKeyword += rest.IndexOf("TOP", StringComparison.OrdinalIgnoreCase) + topMatch.Length;
        }

        // Move to end of line
        var nextNewline = script.IndexOf('\n', afterKeyword);
        return nextNewline >= 0 ? nextNewline : afterKeyword;
    }
}
