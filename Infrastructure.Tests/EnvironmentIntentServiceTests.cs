using Infrastructure.Services;
using Xunit;

namespace Infrastructure.Tests;

public sealed class EnvironmentIntentServiceTests
{
    // ── Must BLOCK_ENV_MISMATCH (SqlServer env, clearly Windows question) ───

    [Theory]
    [InlineData("SqlServer_Live", "restart windows service spooler on CTS03")]
    [InlineData("SqlServer_Live", "check windows event log errors last 2 hours")]
    [InlineData("SqlServer_Live", "list windows services stopped and check disk space")]
    [InlineData("SqlServer_Live", "check disk space on all drives")]
    public void Block_Mismatch_Windows_Question_In_SqlServer_Env(string env, string question)
    {
        var result = EnvironmentIntentService.Evaluate(question, env);
        Assert.True(result.IsMismatch, $"Expected BLOCK_ENV_MISMATCH for '{question}' in {env}, got {result.Verdict}");
        Assert.Equal("Windows_Live", result.SuggestedEnvironment);
        Assert.NotNull(result.Alternatives);
    }

    // ── Must BLOCK_ENV_MISMATCH (Windows env, clearly SQL question) ─────────

    [Theory]
    [InlineData("Windows_Live", "show deadlock history from sys.dm_exec_requests and blocking")]
    [InlineData("Windows_Live", "show database backup history from msdb backupset")]
    [InlineData("Windows_Live", "check sql agent job history in msdb for failed stored procedure")]
    [InlineData("Windows_Live", "check error log for errors")]
    public void Block_Mismatch_Sql_Question_In_Windows_Env(string env, string question)
    {
        var result = EnvironmentIntentService.Evaluate(question, env);
        Assert.True(result.IsMismatch, $"Expected BLOCK_ENV_MISMATCH for '{question}' in {env}, got {result.Verdict}");
        Assert.Equal("SqlServer_Live", result.SuggestedEnvironment);
        Assert.NotNull(result.Alternatives);
    }

    // ── Must NEEDS_CLARIFICATION (ambiguous, no strong hints) ───────────────

    [Theory]
    [InlineData("SqlServer_Live", "server restarted when?")]
    [InlineData("SqlServer_Live", "check logs for restart")]
    [InlineData("SqlServer_Live", "service status")]
    [InlineData("Windows_Live", "server restarted when?")]
    [InlineData("Windows_Live", "service status")]
    public void NeedsClarification_Ambiguous_Questions(string env, string question)
    {
        var result = EnvironmentIntentService.Evaluate(question, env);
        Assert.True(result.IsClarificationNeeded,
            $"Expected NEEDS_CLARIFICATION for '{question}' in {env}, got {result.Verdict}");
        Assert.NotNull(result.Message);
        Assert.NotNull(result.Suggestion1);
        Assert.NotNull(result.Suggestion2);
    }

    // ── NEEDS_CLARIFICATION: contextual clarification question ──────────────

    [Fact]
    public void Clarification_For_Restart_Mentions_Reboot_vs_Service()
    {
        var result = EnvironmentIntentService.Evaluate("server restarted when?", "SqlServer_Live");
        Assert.Contains("reboot", result.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Windows", result.Suggestion1!);
        Assert.Contains("SQL", result.Suggestion2!);
    }

    [Fact]
    public void Clarification_For_Log_Mentions_EventLog_vs_Errorlog()
    {
        // "check logs for errors" has only ambiguous terms (log + error), no "error log" compound.
        var result = EnvironmentIntentService.Evaluate("check logs for errors", "SqlServer_Live");
        Assert.Contains("Event Log", result.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Windows", result.Suggestion1!);
        Assert.Contains("SQL", result.Suggestion2!);
    }

    [Fact]
    public void Clarification_For_Service_Mentions_Windows_vs_SqlAgent()
    {
        var result = EnvironmentIntentService.Evaluate("service status", "Windows_Live");
        Assert.Contains("service", result.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Windows", result.Suggestion1!);
        Assert.Contains("SQL", result.Suggestion2!);
    }

    // ── Must ALLOW in SqlServer env (SQL hints present) ─────────────────────

    [Theory]
    [InlineData("SqlServer_Live", "is SQL Agent enabled?")]
    [InlineData("SqlServer_Live", "check sql errorlog for restart messages")]
    [InlineData("SqlServer_Live", "sql server service restarted when?")]
    [InlineData("SqlServer_Live", "show failed agent job history")]
    [InlineData("SqlServer_Live", "list all databases larger than 10gb")]
    [InlineData("SqlServer_Live", "check deadlock graph from sys.dm_exec_requests")]
    [InlineData("SqlServer_Live", "search error log for I/O")]
    [InlineData("SqlServer_Live", "search error log for backup")]
    [InlineData("SqlServer_Live", "check error log for errors last 24 hours")]
    [InlineData("SqlServer_Live", "List Windows group logins only.")]
    [InlineData("SqlServer_Live", "Show logins with default database that is missing or offline.")]
    [InlineData("SqlServer_Live", "List SQL logins where password policy is OFF.")]
    [InlineData("SqlServer_Live", "Show sysadmin role members.")]
    [InlineData("SqlServer_Live", "List disabled logins only.")]
    [InlineData("SqlServer_Live", "Show logins created in the last 30 days.")]
    [InlineData("SqlServer_Live", "Show login count by type.")]
    [InlineData("SqlServer_Live", "List all versions, and tell is al uptodate with latest patch")]
    [InlineData("SqlServer_Live", "Show the current cumulative update level")]
    [InlineData("SqlServer_Live", "What SQL Server edition and version is installed?")]
    [InlineData("SqlServer_Live", "Show database file sizes and growth settings")]
    [InlineData("SqlServer_Live", "List all trace flags enabled")]
    [InlineData("SqlServer_Live", "Show server configuration options")]
    public void Allow_SqlServer_Questions_In_SqlServer_Env(string env, string question)
    {
        var result = EnvironmentIntentService.Evaluate(question, env);
        Assert.True(result.IsAllowed, $"Expected ALLOW for '{question}' in {env}, got {result.Verdict}: {result.Message}");
    }

    // ── Must ALLOW in Windows env (Windows hints present) ───────────────────

    [Theory]
    [InlineData("Windows_Live", "when did the server reboot?")]
    [InlineData("Windows_Live", "check windows event log for reboot events")]
    [InlineData("Windows_Live", "show stopped services")]
    [InlineData("Windows_Live", "check disk space on all drives")]
    [InlineData("Windows_Live", "list installed hotfixes")]
    [InlineData("Windows_Live", "get-service status on CTS03")]
    public void Allow_Windows_Questions_In_Windows_Env(string env, string question)
    {
        var result = EnvironmentIntentService.Evaluate(question, env);
        Assert.True(result.IsAllowed, $"Expected ALLOW for '{question}' in {env}, got {result.Verdict}: {result.Message}");
    }

    // ── Must BLOCK when question has NO relevance to the environment ────────

    [Theory]
    [InlineData("SqlServer_Live", "what happened yesterday")]
    [InlineData("SqlServer_Live", "what is the weather today?")]
    [InlineData("SqlServer_Live", "tell me a joke")]
    [InlineData("SqlServer_Live", "how to cook pasta")]
    [InlineData("SqlServer_Live", "who won the world cup?")]
    [InlineData("Windows_Live", "what happened yesterday")]
    [InlineData("Windows_Live", "who is the president?")]
    [InlineData("Windows_Live", "what is 2+2")]
    [InlineData("Windows_Live", "explain quantum physics")]
    public void Block_NoRelevance_Irrelevant_Questions(string env, string question)
    {
        var result = EnvironmentIntentService.Evaluate(question, env);
        Assert.True(result.IsMismatch,
            $"Expected BLOCK for irrelevant '{question}' in {env}, got {result.Verdict}");
        Assert.Contains("doesn't appear to be related", result.Message!);
        Assert.NotNull(result.Alternatives);
    }

    // ── Relevance keywords should ALLOW (broad domain match) ────────────────

    [Theory]
    [InlineData("SqlServer_Live", "show me query performance")]
    [InlineData("SqlServer_Live", "check the transaction log")]
    [InlineData("Windows_Live", "show stopped services")]
    [InlineData("Windows_Live", "check cpu usage")]
    [InlineData("Windows_Live", "list open ports")]
    public void Allow_When_RelevanceKeyword_Present(string env, string question)
    {
        var result = EnvironmentIntentService.Evaluate(question, env);
        Assert.True(result.IsAllowed,
            $"Expected ALLOW for '{question}' in {env}, got {result.Verdict}: {result.Message}");
    }

    // ── Single ambiguous term with no relevance → blocked ─────────────────

    [Theory]
    [InlineData("SqlServer_Live", "show status")]
    [InlineData("SqlServer_Live", "check health")]
    public void Block_NoRelevance_VagueQuestion_In_SqlServer(string env, string question)
    {
        var result = EnvironmentIntentService.Evaluate(question, env);
        Assert.True(result.IsMismatch,
            $"Expected BLOCK for vague '{question}' in {env}, got {result.Verdict}");
    }

    // ── Mixed signals: both SQL and Windows present → NEEDS_CLARIFICATION ───

    [Theory]
    [InlineData("SqlServer_Live", "is sql server running on this windows machine")]
    [InlineData("Windows_Live", "check disk space and database backup history")]
    public void NeedsClarification_Mixed_Strong_Signals(string env, string question)
    {
        var result = EnvironmentIntentService.Evaluate(question, env);
        Assert.True(result.IsClarificationNeeded,
            $"Expected NEEDS_CLARIFICATION for '{question}' in {env}, got {result.Verdict}: {result.Message}");
    }

    // ── EnvironmentIntentResult factory methods ─────────────────────────────

    [Fact]
    public void Result_Allowed_Has_Correct_Defaults()
    {
        var r = EnvironmentIntentResult.Allowed();
        Assert.True(r.IsAllowed);
        Assert.False(r.IsMismatch);
        Assert.False(r.IsClarificationNeeded);
    }

    [Fact]
    public void Result_Mismatch_Has_Correct_Properties()
    {
        var r = EnvironmentIntentResult.Mismatch("msg", "Windows_Live", ["alt"]);
        Assert.True(r.IsMismatch);
        Assert.Equal("Windows_Live", r.SuggestedEnvironment);
    }

    [Fact]
    public void Result_NeedsClarification_Has_Correct_Properties()
    {
        var r = EnvironmentIntentResult.NeedsClarification("q?", "s1", "s2");
        Assert.True(r.IsClarificationNeeded);
        Assert.Equal("s1", r.Suggestion1);
        Assert.Equal("s2", r.Suggestion2);
    }
}
