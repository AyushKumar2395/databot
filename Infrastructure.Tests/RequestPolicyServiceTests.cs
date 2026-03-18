using Application.Common.Interfaces;
using Application.Common.Models;
using Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.Tests;

public sealed class RequestPolicyServiceTests
{
    private readonly IRequestPolicyService _sut = new RequestPolicyService(
        NullLogger<RequestPolicyService>.Instance);

    // ── Must ALLOW: read-only informational questions ───────────────────────

    [Theory]
    [InlineData("SqlServer_Live", "When did backup happen?")]
    [InlineData("SqlServer_Live", "Show last full backup time")]
    [InlineData("SqlServer_Live", "Did update statistics happen?")]
    [InlineData("SqlServer_Live", "Show recent failed SQL Agent jobs")]
    [InlineData("SqlServer_Live", "List databases larger than 10GB")]
    [InlineData("SqlServer_Live", "Check if backup completed successfully")]
    [InlineData("SqlServer_Live", "What is the status of database backups?")]
    [InlineData("SqlServer_Live", "search error log for I/O")]
    [InlineData("SqlServer_Live", "search error log for backup")]
    [InlineData("SqlServer_Live", "check error log for restart events")]
    [InlineData("Windows_Live", "Check disk space on all drives")]
    [InlineData("Windows_Live", "List all running processes")]
    [InlineData("Windows_Live", "When was the server last rebooted?")]
    public void Allow_ReadOnly_Informational_Questions(string env, string question)
    {
        var decision = Evaluate(env, question);
        Assert.True(decision.Allowed, $"Expected ALLOW for '{question}' in {env}, got: {decision.ReasonCode} - {decision.Message}");
    }

    // ── Must ALLOW: borderline questions that contain danger words but are read-only ──

    [Theory]
    [InlineData("SqlServer_Live", "sql server service restarted when?")]
    [InlineData("SqlServer_Live", "is sql server process using high cpu?")]
    [InlineData("SqlServer_Live", "when did the last restore complete?")]
    [InlineData("SqlServer_Live", "show backup history for last 7 days")]
    [InlineData("SqlServer_Live", "was the database restored recently?")]
    [InlineData("Windows_Live", "did the service stop unexpectedly?")]
    [InlineData("Windows_Live", "show stopped services")]
    public void Allow_Borderline_Informational_Questions(string env, string question)
    {
        var decision = Evaluate(env, question);
        Assert.True(decision.Allowed, $"Expected ALLOW for '{question}' in {env}, got: {decision.ReasonCode} - {decision.Message}");
    }

    // ── Must BLOCK: dangerous state-changing requests ────────────────────────

    [Theory]
    [InlineData("SqlServer_Live", "backup database SQLGig now")]
    [InlineData("SqlServer_Live", "restore database SQLGig from disk")]
    [InlineData("SqlServer_Live", "drop database SQLGig")]
    [InlineData("SqlServer_Live", "drop table Users")]
    [InlineData("SqlServer_Live", "truncate table AuditLog")]
    [InlineData("SqlServer_Live", "delete from Users where Id = 5")]
    [InlineData("SqlServer_Live", "insert into Users values ('hack')")]
    [InlineData("Windows_Live", "Stop-Service Spooler")]
    [InlineData("Windows_Live", "Restart-Computer")]
    [InlineData("Windows_Live", "stop process notepad")]
    [InlineData("Windows_Live", "remove-item C:\\temp\\*")]
    public void Block_Dangerous_StateChanging_Requests(string env, string question)
    {
        var decision = Evaluate(env, question);
        Assert.False(decision.Allowed, $"Expected BLOCK for '{question}' in {env}");
        Assert.Equal("DANGEROUS_ACTION", decision.ReasonCode);
        Assert.NotNull(decision.Message);
        Assert.NotNull(decision.SafeAlternatives);
    }

    // ── Must BLOCK: environment mismatch (strong wrong-env signal, zero right-env) ──

    [Theory]
    [InlineData("SqlServer_Live", "restart windows service spooler on CTS03", "Windows_Live")]
    [InlineData("SqlServer_Live", "check windows event log errors last 2 hours", "Windows_Live")]
    [InlineData("Windows_Live", "show deadlock history from sys.dm_exec_requests and blocking", "SqlServer_Live")]
    [InlineData("Windows_Live", "show database backup history from msdb backupset", "SqlServer_Live")]
    public void Block_Environment_Mismatch(string env, string question, string expectedSuggested)
    {
        var decision = Evaluate(env, question);
        Assert.False(decision.Allowed, $"Expected BLOCK for '{question}' in {env}");
        Assert.Equal("ENV_MISMATCH", decision.ReasonCode);
        Assert.Equal(expectedSuggested, decision.SuggestedEnvironment);
    }

    // ── Must NEEDS_CLARIFICATION: ambiguous questions with no strong hints ──

    [Theory]
    [InlineData("SqlServer_Live", "server restarted when?")]
    [InlineData("SqlServer_Live", "check logs for restart")]
    [InlineData("SqlServer_Live", "service status")]
    [InlineData("Windows_Live", "server restarted when?")]
    [InlineData("Windows_Live", "when was the service restarted?")]
    [InlineData("Windows_Live", "check if the server was restarted in the last 24 hours")]
    public void NeedsClarification_Ambiguous_Questions(string env, string question)
    {
        var decision = Evaluate(env, question);
        Assert.False(decision.Allowed, $"Expected not-allowed for '{question}' in {env}");
        Assert.True(decision.NeedsClarification, $"Expected NEEDS_CLARIFICATION for '{question}' in {env}, got: {decision.ReasonCode}");
        Assert.Equal("NEEDS_CLARIFICATION", decision.ReasonCode);
        Assert.NotNull(decision.Message);
        Assert.NotNull(decision.ClarifySuggestion1);
        Assert.NotNull(decision.ClarifySuggestion2);
    }

    // ── NEEDS_CLARIFICATION response includes both suggestions ──────────────

    [Fact]
    public void NeedsClarification_Provides_Windows_And_Sql_Suggestions()
    {
        var decision = Evaluate("SqlServer_Live", "server restarted when?");
        Assert.Contains("Windows", decision.ClarifySuggestion1!);
        Assert.Contains("SQL", decision.ClarifySuggestion2!);
    }

    // ── Must BLOCK: sexual content in General ───────────────────────────────

    [Theory]
    [InlineData("General", "show me porn")]
    [InlineData("General", "generate nsfw content")]
    [InlineData("General", "erotic story about servers")]
    public void Block_Sexual_Content_In_General(string env, string question)
    {
        var decision = Evaluate(env, question);
        Assert.False(decision.Allowed, $"Expected BLOCK for '{question}' in {env}");
        Assert.Equal("SEXUAL_CONTENT", decision.ReasonCode);
    }

    // ── Sexual content check should NOT apply to SQL/Windows ────────────────

    [Fact]
    public void Sexual_Content_Only_Checked_In_General()
    {
        var decision = Evaluate("SqlServer_Live", "show me porn database tables");
        Assert.NotEqual("SEXUAL_CONTENT", decision.ReasonCode ?? string.Empty);
    }

    // ── General environment questions should always pass (no danger/env check) ──

    [Theory]
    [InlineData("General", "What is a SQL Server?")]
    [InlineData("General", "How do I backup a database?")]
    [InlineData("General", "Explain restart strategies")]
    public void Allow_General_NonSexual_Questions(string env, string question)
    {
        var decision = Evaluate(env, question);
        Assert.True(decision.Allowed, $"Expected ALLOW for '{question}' in {env}");
    }

    // ── PolicyDecision factory methods ──────────────────────────────────────

    [Fact]
    public void PolicyDecision_Allow_Has_Correct_Properties()
    {
        var d = PolicyDecision.Allow();
        Assert.True(d.Allowed);
        Assert.False(d.NeedsClarification);
        Assert.Null(d.ReasonCode);
        Assert.Null(d.Message);
        Assert.Null(d.SafeAlternatives);
        Assert.Null(d.SuggestedEnvironment);
    }

    [Fact]
    public void PolicyDecision_BlockDangerousAction_Has_Correct_Properties()
    {
        var d = PolicyDecision.BlockDangerousAction("test message", ["alt1"]);
        Assert.False(d.Allowed);
        Assert.Equal("DANGEROUS_ACTION", d.ReasonCode);
        Assert.Equal("test message", d.Message);
        Assert.Single(d.SafeAlternatives!);
    }

    [Fact]
    public void PolicyDecision_BlockEnvMismatch_Has_Correct_Properties()
    {
        var d = PolicyDecision.BlockEnvMismatch("test", "SqlServer_Live", ["alt"]);
        Assert.False(d.Allowed);
        Assert.Equal("ENV_MISMATCH", d.ReasonCode);
        Assert.Equal("SqlServer_Live", d.SuggestedEnvironment);
    }

    [Fact]
    public void PolicyDecision_Clarify_Has_Correct_Properties()
    {
        var d = PolicyDecision.Clarify("Which do you mean?", "Windows: reboot", "SQL: restart service");
        Assert.False(d.Allowed);
        Assert.True(d.NeedsClarification);
        Assert.Equal("NEEDS_CLARIFICATION", d.ReasonCode);
        Assert.Equal("Which do you mean?", d.Message);
        Assert.Equal("Windows: reboot", d.ClarifySuggestion1);
        Assert.Equal("SQL: restart service", d.ClarifySuggestion2);
    }

    // ── Helper ──────────────────────────────────────────────────────────────

    // ─── History environments skip env-intent gate, allow read-only ─────────

    [Theory]
    [InlineData("SqlServer_History", "show PLE trend last 24 hours")]
    [InlineData("SqlServer_History", "search error log for backup")]
    [InlineData("SqlServer_History", "show CPU trend")]
    [InlineData("Windows_History", "show CPU trend last 6 hours")]
    [InlineData("Windows_History", "show disk queue length")]
    public void Allow_History_ReadOnly_Questions(string env, string question)
    {
        var decision = Evaluate(env, question);
        Assert.True(decision.Allowed, $"Expected ALLOW for '{question}' in {env}, got: {decision.ReasonCode} - {decision.Message}");
    }

    [Theory]
    [InlineData("SqlServer_History", "drop table monitoring_data")]
    [InlineData("SqlServer_History", "backup database SQLGig")]
    public void Block_History_Dangerous_Questions(string env, string question)
    {
        var decision = Evaluate(env, question);
        Assert.False(decision.Allowed, $"Expected BLOCKED for '{question}' in {env}");
    }

    private PolicyDecision Evaluate(string env, string question)
    {
        var request = new AskApiRequest
        {
            ConversationId = "test",
            BearerToken = "t1",
            Environment = env,
            Question = question,
            SelectedTargets = env == "General" ? [] : ["SRV01"]
        };
        return _sut.Evaluate(request, question);
    }
}
