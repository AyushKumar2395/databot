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
    [InlineData("Windows_Live", "check error log")]
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
        var result = EnvironmentIntentService.Evaluate("check error log", "SqlServer_Live");
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

    // ── Must ALLOW when no signal at all ────────────────────────────────────

    [Theory]
    [InlineData("SqlServer_Live", "what happened yesterday")]
    [InlineData("Windows_Live", "what happened yesterday")]
    public void Allow_When_No_EnvironmentSignal(string env, string question)
    {
        var result = EnvironmentIntentService.Evaluate(question, env);
        Assert.True(result.IsAllowed, $"Expected ALLOW for '{question}' in {env}, got {result.Verdict}");
    }

    // ── Single ambiguous term should ALLOW, not trigger clarification ───────

    [Theory]
    [InlineData("SqlServer_Live", "show status")]
    [InlineData("Windows_Live", "show stopped services")]
    [InlineData("Windows_Live", "server uptime")]
    public void Allow_Single_Ambiguous_Term_Does_Not_Trigger_Clarification(string env, string question)
    {
        var result = EnvironmentIntentService.Evaluate(question, env);
        Assert.True(result.IsAllowed, $"Expected ALLOW for '{question}' in {env}, got {result.Verdict}: {result.Message}");
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
