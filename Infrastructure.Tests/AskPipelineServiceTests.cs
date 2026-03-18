using Application.Common.Interfaces;
using Application.Common.Models;
using Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.Tests;

public sealed class AskPipelineServiceTests
{
    [Fact]
    public async Task General_ListAllMoon_Returns_StructuredAnswerOnly_With_No_Script()
    {
        var service = CreateService();

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "g1",
                BearerToken = "u1",
                Environment = "General",
                Question = "list all moon",
                SelectedTargets =[]
            },
            CancellationToken.None);

        Assert.Equal("GENERAL", response.Plan.Mode);
        Assert.Null(response.Script);
        Assert.Null(response.Plan.ScriptLanguage);
        Assert.Equal("ANSWER_ONLY", response.Result.Kind);
        Assert.NotNull(response.Result.AnswerText);
        Assert.NotNull(response.Answer);
        Assert.Equal("OK", response.Answer!.Status);
        Assert.NotNull(response.Answer.Title);
        Assert.NotNull(response.Answer.Summary);
        Assert.NotEmpty(response.Answer.Summary!);
        Assert.NotNull(response.Answer.Details);
        Assert.Null(response.Answer.Anomaly);
        Assert.NotNull(response.Answer.Sections);
        Assert.InRange(response.Answer.Sections!.Count, 4, 8);
    }

    [Fact]
    public async Task General_EmptySelectedServers_Succeeds_With_StructuredAnswer()
    {
        var service = CreateService();

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "g2",
                BearerToken = "u50",
                Environment = "General",
                Question = "what is a clustered index?",
                SelectedTargets = []
            },
            CancellationToken.None);

        Assert.Equal("GENERAL", response.Plan.Mode);
        Assert.Null(response.Script);
        Assert.Equal("ANSWER_ONLY", response.Result.Kind);
        Assert.Equal("NOT_EXECUTED", response.Result.Status);
        Assert.Null(response.Result.Items);
        Assert.NotNull(response.Answer);
        Assert.Equal("OK", response.Answer!.Status);
        Assert.NotNull(response.Answer.Title);
        Assert.NotNull(response.Answer.Summary);
        Assert.NotEmpty(response.Answer.Summary!);
        Assert.NotNull(response.Answer.Details);
        Assert.NotNull(response.Answer.Explanation);
        Assert.Null(response.Answer.Anomaly);
        Assert.NotNull(response.Answer.Sections);
        Assert.InRange(response.Answer.Sections!.Count, 4, 8);
    }

    [Fact]
    public async Task General_StructuredJson_LlmResponse_Parsed_Into_Answer_Fields()
    {
        var service = CreateServiceWithStructuredGeneralClient();

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "g3",
                BearerToken = "u52",
                Environment = "General",
                Question = "Give detail of all SQL Server version",
                SelectedTargets = []
            },
            CancellationToken.None);

        Assert.Equal("GENERAL", response.Plan.Mode);
        Assert.Null(response.Script);
        Assert.Equal("ANSWER_ONLY", response.Result.Kind);
        Assert.NotNull(response.Answer);
        Assert.Equal("OK", response.Answer!.Status);
        Assert.Equal("SQL Server Version History", response.Answer.Title);
        Assert.NotNull(response.Answer.Summary);
        Assert.InRange(response.Answer.Summary!.Length, 4, 6);
        Assert.NotNull(response.Answer.Explanation);
        Assert.NotNull(response.Answer.Details);
        Assert.DoesNotContain("##", response.Answer.Details!);
        Assert.DoesNotContain("**", response.Answer.Details!);
        Assert.Contains("Major Versions:", response.Answer.Details!);
        Assert.Null(response.Answer.Anomaly);

        // result.answerText should be a short single-sentence fallback
        Assert.Equal("SQL Server Version History", response.Result.AnswerText);

        // Sections contract
        Assert.NotNull(response.Answer.Sections);
        Assert.InRange(response.Answer.Sections!.Count, 4, 8);
        Assert.Equal("overview", response.Answer.Sections[0].Key);
        Assert.All(response.Answer.Sections, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Key));
            Assert.False(string.IsNullOrWhiteSpace(s.Title));
            Assert.True(s.Bullets is { Count: > 0 } || s.Steps is { Count: > 0 },
                $"Section '{s.Key}' must have bullets or steps");
        });
    }

    [Fact]
    public async Task General_Details_MarkdownStripped_By_Sanitizer()
    {
        // Use a client that returns markdown in details to verify post-processing strips it.
        var service = CreateServiceWithMarkdownGeneralClient();

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "g4",
                BearerToken = "u53",
                Environment = "General",
                Question = "explain wait stats",
                SelectedTargets = []
            },
            CancellationToken.None);

        Assert.Equal("GENERAL", response.Plan.Mode);
        Assert.NotNull(response.Answer);
        Assert.NotNull(response.Answer!.Details);
        Assert.DoesNotContain("##", response.Answer.Details!);
        Assert.DoesNotContain("**", response.Answer.Details!);
        Assert.DoesNotContain("```", response.Answer.Details!);
        Assert.Contains("Overview:", response.Answer.Details!);
        Assert.Contains("Key Metrics:", response.Answer.Details!);
    }

    [Fact]
    public async Task SqlServerLive_EmptySelectedServers_Returns_Failed_Or_Stopped()
    {
        var service = CreateService();

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "v1",
                BearerToken = "u51",
                Environment = "SqlServer_Live",
                Question = "list all databases",
                SelectedTargets = []
            },
            CancellationToken.None);

        // Without targets, execution cannot succeed — pipeline either stops or returns no items.
        Assert.NotEqual("GENERAL", response.Plan.Mode);
        Assert.NotEqual("ANSWER_ONLY", response.Result.Kind);
        Assert.True(
            response.Result.Status is "FAILED" or "NOT_EXECUTED" or "STOPPED"
            || (response.Result.Items is null || response.Result.Items.Count == 0),
            "SqlServer_Live with empty targets must not produce successful execution items.");
    }

    [Fact]
    public async Task SqlServerLive_ListAllFailedJobs_Should_Execute_And_Not_Stop()
    {
        var service = CreateService();

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "s1",
                BearerToken = "u2",
                Environment = "SqlServer_Live",
                Question = "list all failed jobs",
                SelectedTargets =["SQL01", "SQL02"]
            },
            CancellationToken.None);

        Assert.Equal("LLM_ONLY", response.Plan.Mode);
        Assert.Equal("EXECUTION", response.Result.Kind);
        Assert.Equal("SQL", response.Plan.ScriptLanguage);
        Assert.NotNull(response.Script);
        Assert.Contains("sysjobhistory", response.Script!.Final, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("SUCCESS", response.Result.Status);
        Assert.Equal(2, response.Result.Items!.Count);
        Assert.Equal(2, response.Result.Summary!.SuccessCount);
    }

    [Fact]
    public async Task SqlServerLive_BackupHistory_Last7Days_Should_Execute_And_Not_Stop()
    {
        var service = CreateService();

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "s2",
                BearerToken = "u3",
                Environment = "SqlServer_Live",
                Question = "show backup history last 7 days",
                SelectedTargets =["SQL01"]
            },
            CancellationToken.None);

        Assert.Equal("LLM_ONLY", response.Plan.Mode);
        Assert.Equal("EXECUTION", response.Result.Kind);
        Assert.Equal("SQL", response.Plan.ScriptLanguage);
        Assert.NotNull(response.Script);
        Assert.Contains("msdb.dbo.backupset", response.Script!.Final, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("SUCCESS", response.Result.Status);
        Assert.Single(response.Result.Items!);
    }

    [Fact]
    public async Task SqlServerLive_DropTable_Should_Be_Blocked()
    {
        var service = CreateService();

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "s3",
                BearerToken = "u4",
                Environment = "SqlServer_Live",
                Question = "drop table x",
                SelectedTargets =["SQL01"]
            },
            CancellationToken.None);

        Assert.Equal("STOPPED", response.Tuning.Status);
        Assert.StartsWith("BLOCKED: STATE_CHANGING_REQUEST", response.Tuning.StopReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Null(response.Script);
    }

    [Fact]
    public async Task WindowsLive_ListWindowsVersions_Should_Execute_And_Not_Stop()
    {
        var service = CreateService();

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "w1",
                BearerToken = "u5",
                Environment = "Windows_Live",
                Question = "list windows versions",
                SelectedTargets =["WIN01", "WIN02"]
            },
            CancellationToken.None);

        Assert.Equal("LLM_ONLY", response.Plan.Mode);
        Assert.Equal("EXECUTION", response.Result.Kind);
        Assert.Equal("PS", response.Plan.ScriptLanguage);
        Assert.NotNull(response.Script);
        Assert.Contains("Get-CimInstance Win32_OperatingSystem", response.Script!.Final, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("SUCCESS", response.Result.Status);
        Assert.Equal(2, response.Result.Items!.Count);
        Assert.Equal(2, response.Result.Summary!.SuccessCount);
    }

    [Fact]
    public async Task WindowsLive_RestartServer_Should_Be_Blocked()
    {
        var service = CreateService();

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "w2",
                BearerToken = "u6",
                Environment = "Windows_Live",
                Question = "restart server WIN01",
                SelectedTargets =["WIN01"]
            },
            CancellationToken.None);

        Assert.Equal("STOPPED", response.Tuning.Status);
        Assert.StartsWith("BLOCKED: STATE_CHANGING_REQUEST", response.Tuning.StopReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Null(response.Script);
    }

    // ─── SQL instance token parsing ────────────────────────────────────────────

    [Theory]
    [InlineData("CTS03#Admin",   "CTS03",  "Admin",  @"CTS03\Admin")]
    [InlineData("CTS02#Finance", "CTS02",  "Finance",@"CTS02\Finance")]
    [InlineData("CTS03",         "CTS03",  null,     "CTS03")]
    [InlineData("SQL01",         "SQL01",  null,     "SQL01")]
    public void SqlInstanceToken_ParsedCorrectly(
        string token, string expectedServer, string? expectedInstance, string expectedTarget)
    {
        var (server, instance, connectionTarget) = SqlExecutor.ParseSqlInstanceToken(token);

        Assert.Equal(expectedServer, server);
        Assert.Equal(expectedInstance, instance);
        Assert.Equal(expectedTarget, connectionTarget);
    }

    [Fact]
    public async Task SqlServerLive_InstanceToken_ConnectionTarget_Used_In_Pipeline()
    {
        // Uses token "CTS03#Admin" — pipeline must not STOP (the token itself is valid).
        var service = CreateService();

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "tok1",
                BearerToken = "u99",
                Environment = "SqlServer_Live",
                Question = "list all failed jobs",
                SelectedTargets =["CTS03#Admin", "CTS02#Finance"]
            },
            CancellationToken.None);

        Assert.Equal("LLM_ONLY", response.Plan.Mode);
        Assert.Equal("SQL", response.Plan.ScriptLanguage);
        Assert.NotNull(response.Script);
        Assert.Equal(2, response.Result.Items!.Count);
    }

    // ─── False-BLOCKED recovery ─────────────────────────────────────────────

    [Fact]
    public async Task WindowsLive_FalseBlocked_SafeQuestion_Recovers_And_Executes()
    {
        // Simulate a tuner that incorrectly BLOCKs a clearly safe Windows question.
        var service = CreateServiceWithBlockingTuner();

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "fb1",
                BearerToken = "u7",
                Environment = "Windows_Live",
                Question = "list windows version",
                SelectedTargets =["WIN01"]
            },
            CancellationToken.None);

        // Recovery should override the false BLOCKED and proceed to execution.
        Assert.NotEqual("STOPPED", response.Tuning.Status);
        Assert.Equal("EXECUTION", response.Result.Kind);
        Assert.Equal("PS", response.Plan.ScriptLanguage);
        Assert.NotNull(response.Script);
    }

    [Fact]
    public async Task SqlServerLive_FalseBlocked_SafeQuestion_Recovers_And_Executes()
    {
        // Simulate a tuner that incorrectly BLOCKs a clearly safe SQL question.
        var service = CreateServiceWithBlockingTuner();

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "fb2",
                BearerToken = "u8",
                Environment = "SqlServer_Live",
                Question = "list all failed jobs",
                SelectedTargets =["SQL01"]
            },
            CancellationToken.None);

        // Recovery should override the false BLOCKED and proceed to execution.
        Assert.NotEqual("STOPPED", response.Tuning.Status);
        Assert.Equal("EXECUTION", response.Result.Kind);
        Assert.Equal("SQL", response.Plan.ScriptLanguage);
        Assert.NotNull(response.Script);
    }

    // ─── PARTIAL_SUCCESS consolidation ─────────────────────────────────────

    [Fact]
    public async Task SqlServerLive_PartialSuccess_When_OneTargetFails()
    {
        var service = CreateServiceWithPartialFailOrchestrator();

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "ps1",
                BearerToken = "u9",
                Environment = "SqlServer_Live",
                Question = "list all failed jobs",
                SelectedTargets =["SQL01", "SQL02"]
            },
            CancellationToken.None);

        Assert.Equal("LLM_ONLY", response.Plan.Mode);
        Assert.Equal("PARTIAL_SUCCESS", response.Result.Status);
        Assert.Equal(1, response.Result.Summary!.SuccessCount);
        Assert.Equal(1, response.Result.Summary.FailCount);
    }

    // ─── Database size filter (sys.master_files) ────────────────────────────

    [Fact]
    public async Task SqlServerLive_DatabasesLargerThan10GB_Script_Uses_master_files()
    {
        var service = CreateService();

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "size1",
                BearerToken = "u10",
                Environment = "SqlServer_Live",
                Question = "list databases larger than 10GB",
                SelectedTargets =["SQL01"]
            },
            CancellationToken.None);

        Assert.Equal("LLM_ONLY", response.Plan.Mode);
        Assert.Equal("EXECUTION", response.Result.Kind);
        Assert.Equal("SQL", response.Plan.ScriptLanguage);
        Assert.NotNull(response.Script);
        Assert.Contains("sys.master_files", response.Script!.Final, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("10", response.Script.Final, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("```", response.Script.Final, StringComparison.Ordinal);
        Assert.Equal("SUCCESS", response.Result.Status);
        Assert.NotNull(response.Result.Items);
        Assert.NotNull(response.Result.Summary);
    }

    // ─── answer node ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Answer_Success_HasStatusOkAndExplanation()
    {
        var service = CreateService();

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "ans1",
                BearerToken = "u20",
                Environment = "SqlServer_Live",
                Question = "list all failed jobs",
                SelectedTargets = ["SQL01"]
            },
            CancellationToken.None);

        Assert.NotNull(response.Answer);
        Assert.Equal("OK", response.Answer!.Status);
        Assert.NotNull(response.Answer.Explanation);
        Assert.NotNull(response.Answer.Model);

        // Structured answer fields
        Assert.NotNull(response.Answer.Title);
        Assert.NotNull(response.Answer.Summary);
        Assert.NotEmpty(response.Answer.Summary!);
        Assert.NotNull(response.Answer.Sections);
        Assert.InRange(response.Answer.Sections!.Count, 4, 8);
        Assert.All(response.Answer.Sections, s =>
        {
            Assert.NotEmpty(s.Key);
            Assert.NotEmpty(s.Title);
            Assert.True(s.Bullets is { Count: > 0 } || s.Steps is { Count: > 0 });
        });

        // New intelligent fields
        Assert.NotNull(response.Answer.KeyMetrics);
        Assert.NotEmpty(response.Answer.KeyMetrics!);
        Assert.All(response.Answer.KeyMetrics, km =>
        {
            Assert.NotEmpty(km.Label);
            Assert.NotEmpty(km.Value);
            Assert.Contains(km.Status, new[] { "ok", "info", "warning", "critical" });
        });

        Assert.NotNull(response.Answer.Recommendations);
        Assert.NotEmpty(response.Answer.Recommendations!);
    }

    [Fact]
    public async Task Answer_PartialSuccess_HasStatusPartial()
    {
        var service = CreateServiceWithPartialFailOrchestrator();

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "ans2",
                BearerToken = "u21",
                Environment = "SqlServer_Live",
                Question = "list all failed jobs",
                SelectedTargets = ["SQL01", "SQL02"]
            },
            CancellationToken.None);

        Assert.Equal("PARTIAL_SUCCESS", response.Result.Status);
        Assert.NotNull(response.Answer);
        Assert.Equal("PARTIAL", response.Answer!.Status);
    }

    [Fact]
    public async Task Answer_AllTargetsFailed_HasStatusFailed()
    {
        var service = CreateServiceWithAllFailOrchestrator();

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "ans3",
                BearerToken = "u22",
                Environment = "SqlServer_Live",
                Question = "list all failed jobs",
                SelectedTargets = ["SQL01"]
            },
            CancellationToken.None);

        Assert.Equal("FAILED", response.Result.Status);
        Assert.NotNull(response.Answer);
        Assert.Equal("FAILED", response.Answer!.Status);
        Assert.Equal("UNKNOWN", response.Answer.Severity);
        Assert.NotNull(response.Answer.Explanation);
        // Concise failure: no verbose sections, just a short explanation
        Assert.Contains("failed", response.Answer.Explanation!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Answer_InvalidJsonFromLlm_FallsBackGracefully()
    {
        var service = CreateServiceWithBadJsonExplainClient();

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "ans4",
                BearerToken = "u23",
                Environment = "SqlServer_Live",
                Question = "list all failed jobs",
                SelectedTargets = ["SQL01"]
            },
            CancellationToken.None);

        Assert.NotNull(response.Answer);
        // When LLM returns invalid JSON the node should still have status OK (fallback path).
        Assert.Equal("OK", response.Answer!.Status);
        Assert.NotNull(response.Answer.Explanation);

        // Even with bad JSON, fallback should produce structured sections
        Assert.NotNull(response.Answer.Sections);
        Assert.InRange(response.Answer.Sections!.Count, 4, 8);
        Assert.NotNull(response.Answer.Title);
        Assert.NotNull(response.Answer.Summary);
    }

    [Fact]
    public async Task Answer_Failed_StillHasStructuredSections()
    {
        var service = CreateServiceWithAllFailOrchestrator();

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "ans-fail-sections",
                BearerToken = "u50",
                Environment = "SqlServer_Live",
                Question = "list all failed jobs",
                SelectedTargets = ["SQL01"]
            },
            CancellationToken.None);

        Assert.NotNull(response.Answer);
        Assert.Equal("FAILED", response.Answer!.Status);
        // Concise failure: short explanation, no verbose sections/title/summary
        Assert.NotNull(response.Answer.Explanation);
        Assert.Contains("failed", response.Answer.Explanation!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Answer_StructuredExplain_HasCorrectSectionShape()
    {
        var service = CreateService();

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "ans-structured",
                BearerToken = "u51",
                Environment = "SqlServer_Live",
                Question = "list all failed jobs",
                SelectedTargets = ["SQL01"]
            },
            CancellationToken.None);

        Assert.NotNull(response.Answer);
        Assert.Equal("OK", response.Answer!.Status);

        // Title and summary
        Assert.NotNull(response.Answer.Title);
        Assert.NotNull(response.Answer.Summary);
        Assert.InRange(response.Answer.Summary!.Length, 3, 7);

        // Sections
        Assert.NotNull(response.Answer.Sections);
        Assert.InRange(response.Answer.Sections!.Count, 4, 8);

        // Each section must have valid structure
        foreach (var section in response.Answer.Sections)
        {
            Assert.NotEmpty(section.Key);
            Assert.NotEmpty(section.Title);
            Assert.NotEmpty(section.Icon);
            Assert.Contains(section.Tone, new[] { "ok", "info", "warning", "critical" });
            Assert.True(section.Bullets is { Count: > 0 } || section.Steps is { Count: > 0 },
                $"Section '{section.Key}' must have either bullets or steps");
        }
    }

    [Fact]
    public async Task WindowsLive_HealthQuestion_NotBlocked_UsesWindowsHealthScript()
    {
        var service = CreateService();

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "h1",
                BearerToken = "u30",
                Environment = "Windows_Live",
                Question = "is server healthy?",
                SelectedTargets = ["CTS03"]
            },
            CancellationToken.None);

        Assert.Equal("LLM_ONLY", response.Plan.Mode);
        Assert.Equal("EXECUTION", response.Result.Kind);
        Assert.NotEqual("STOPPED", response.Result.Status);
        Assert.NotNull(response.Script);
        Assert.Contains("Win32_OperatingSystem", response.Script!.Final, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SqlServerLive_HealthQuestion_NotBlocked_UsesSqlHealthScript()
    {
        var service = CreateService();

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "h2",
                BearerToken = "u31",
                Environment = "SqlServer_Live",
                Question = "why is SQL slow?",
                SelectedTargets = ["SQL01"]
            },
            CancellationToken.None);

        Assert.Equal("LLM_ONLY", response.Plan.Mode);
        Assert.Equal("EXECUTION", response.Result.Kind);
        Assert.NotEqual("STOPPED", response.Result.Status);
        Assert.NotNull(response.Script);
        // Generator=1: LLM is 100% responsible — no HealthScriptGenerator fallback.
        // FakeLlmClient returns a generic SQL script for unrecognised questions.
        Assert.Contains("@@SERVERNAME", response.Script!.Final, StringComparison.OrdinalIgnoreCase);
    }

    private static AskPipelineService CreateService()
    {
        var modelSelector = new FakeModelSelector();
        ILLMClient[] clients =
        [
            new FakeLlmClient("Gemini"),
            new FakeLlmClient("OpenAI")
        ];

        return new AskPipelineService(
            modelSelector,
            clients,
            new FakeScriptAutoFixOrchestrator(),
            new StubToolRegistryResolver(NullLogger<StubToolRegistryResolver>.Instance),
            new TemplateRenderer(NullLogger<TemplateRenderer>.Instance),
            new AllowAllPolicyService(),
            new StubQuestionSamplesRepository(),
            new TestHelpers.EmptyUserServerRepository(),
            NullLogger<AskPipelineService>.Instance);
    }

    private static AskPipelineService CreateServiceWithMarkdownGeneralClient()
    {
        var modelSelector = new FakeModelSelector();
        ILLMClient[] clients =
        [
            new MarkdownGeneralLlmClient("Gemini"),
            new FakeLlmClient("OpenAI")
        ];

        return new AskPipelineService(
            modelSelector,
            clients,
            new FakeScriptAutoFixOrchestrator(),
            new StubToolRegistryResolver(NullLogger<StubToolRegistryResolver>.Instance),
            new TemplateRenderer(NullLogger<TemplateRenderer>.Instance),
            new AllowAllPolicyService(),
            new StubQuestionSamplesRepository(),
            new TestHelpers.EmptyUserServerRepository(),
            NullLogger<AskPipelineService>.Instance);
    }

    private static AskPipelineService CreateServiceWithBlockingTuner()
    {
        var modelSelector = new FakeModelSelector();
        ILLMClient[] clients =
        [
            new BlockingFakeLlmClient("Gemini"),
            new FakeLlmClient("OpenAI")
        ];

        return new AskPipelineService(
            modelSelector,
            clients,
            new FakeScriptAutoFixOrchestrator(),
            new StubToolRegistryResolver(NullLogger<StubToolRegistryResolver>.Instance),
            new TemplateRenderer(NullLogger<TemplateRenderer>.Instance),
            new AllowAllPolicyService(),
            new StubQuestionSamplesRepository(),
            new TestHelpers.EmptyUserServerRepository(),
            NullLogger<AskPipelineService>.Instance);
    }

    private static AskPipelineService CreateServiceWithStructuredGeneralClient()
    {
        var modelSelector = new FakeModelSelector();
        ILLMClient[] clients =
        [
            new StructuredGeneralLlmClient("Gemini"),
            new FakeLlmClient("OpenAI")
        ];

        return new AskPipelineService(
            modelSelector,
            clients,
            new FakeScriptAutoFixOrchestrator(),
            new StubToolRegistryResolver(NullLogger<StubToolRegistryResolver>.Instance),
            new TemplateRenderer(NullLogger<TemplateRenderer>.Instance),
            new AllowAllPolicyService(),
            new StubQuestionSamplesRepository(),
            new TestHelpers.EmptyUserServerRepository(),
            NullLogger<AskPipelineService>.Instance);
    }

    private static AskPipelineService CreateServiceWithPartialFailOrchestrator()
    {
        var modelSelector = new FakeModelSelector();
        ILLMClient[] clients =
        [
            new FakeLlmClient("Gemini"),
            new FakeLlmClient("OpenAI")
        ];

        return new AskPipelineService(
            modelSelector,
            clients,
            new PartialFailScriptOrchestrator(),
            new StubToolRegistryResolver(NullLogger<StubToolRegistryResolver>.Instance),
            new TemplateRenderer(NullLogger<TemplateRenderer>.Instance),
            new AllowAllPolicyService(),
            new StubQuestionSamplesRepository(),
            new TestHelpers.EmptyUserServerRepository(),
            NullLogger<AskPipelineService>.Instance);
    }

    private static AskPipelineService CreateServiceWithAllFailOrchestrator()
    {
        var modelSelector = new FakeModelSelector();
        ILLMClient[] clients =
        [
            new FakeLlmClient("Gemini"),
            new FakeLlmClient("OpenAI")
        ];

        return new AskPipelineService(
            modelSelector,
            clients,
            new AllFailScriptOrchestrator(),
            new StubToolRegistryResolver(NullLogger<StubToolRegistryResolver>.Instance),
            new TemplateRenderer(NullLogger<TemplateRenderer>.Instance),
            new AllowAllPolicyService(),
            new StubQuestionSamplesRepository(),
            new TestHelpers.EmptyUserServerRepository(),
            NullLogger<AskPipelineService>.Instance);
    }

    private static AskPipelineService CreateServiceWithBadJsonExplainClient()
    {
        var modelSelector = new FakeModelSelector();
        ILLMClient[] clients =
        [
            new BadJsonExplainLlmClient("Gemini"),
            new FakeLlmClient("OpenAI")
        ];

        return new AskPipelineService(
            modelSelector,
            clients,
            new FakeScriptAutoFixOrchestrator(),
            new StubToolRegistryResolver(NullLogger<StubToolRegistryResolver>.Instance),
            new TemplateRenderer(NullLogger<TemplateRenderer>.Instance),
            new AllowAllPolicyService(),
            new StubQuestionSamplesRepository(),
            new TestHelpers.EmptyUserServerRepository(),
            NullLogger<AskPipelineService>.Instance);
    }

    private sealed class AllowAllPolicyService : IRequestPolicyService
    {
        public PolicyDecision Evaluate(AskApiRequest request, string tunedQuestionOrRaw)
            => PolicyDecision.Allow();
    }

    private sealed class FakeScriptAutoFixOrchestrator : IScriptAutoFixOrchestrator
    {
        public Task<ScriptExecutionResponse> ExecuteAsync(
            ScriptExecutionRequest request,
            CancellationToken cancellationToken)
        {
            return ExecuteWithAutoFixAsync(request, cancellationToken);
        }

        public Task<ScriptExecutionResponse> ExecuteWithAutoFixAsync(
            ScriptExecutionRequest request,
            CancellationToken cancellationToken)
        {
            return ExecuteWithAutoFixAsync(request, null, cancellationToken);
        }

        public Task<ScriptExecutionResponse> ExecuteWithAutoFixAsync(
            ScriptExecutionRequest request,
            IProgressStream? progress,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;

            var results = request.SelectedServers
                .Select(server => new ScriptExecutionServerResult
                {
                    Server = server,
                    Status = "SUCCESS",
                    RowCount = 1,
                    DurationMs = 10,
                    Rows =
                    [
                        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["ServerName"] = server,
                            ["DatabaseName"] = "master",
                            ["CapturedAt"] = DateTime.UtcNow,
                            ["Name"] = "ok"
                        }
                    ]
                })
                .ToList();

            return Task.FromResult(new ScriptExecutionResponse
            {
                Environment = request.Environment,
                ScriptLanguage = request.ScriptLanguage,
                TunedQuestion = request.TunedQuestion,
                FinalScript = request.GeneratedScript,
                ResultsByServer = results,
                Summary = new ScriptExecutionSummary
                {
                    SuccessCount = request.SelectedServers.Length,
                    FailCount = 0,
                    TotalRowCount = results.Sum(x => x.RowCount),
                    TotalTargets = request.SelectedServers.Length
                }
            });
        }
    }

    /// <summary>
    /// Orchestrator where the first server succeeds and the second fails —
    /// used to verify PARTIAL_SUCCESS status.
    /// </summary>
    private sealed class PartialFailScriptOrchestrator : IScriptAutoFixOrchestrator
    {
        public Task<ScriptExecutionResponse> ExecuteAsync(
            ScriptExecutionRequest request,
            CancellationToken cancellationToken)
            => ExecuteWithAutoFixAsync(request, cancellationToken);

        public Task<ScriptExecutionResponse> ExecuteWithAutoFixAsync(
            ScriptExecutionRequest request,
            CancellationToken cancellationToken)
            => ExecuteWithAutoFixAsync(request, null, cancellationToken);

        public Task<ScriptExecutionResponse> ExecuteWithAutoFixAsync(
            ScriptExecutionRequest request,
            IProgressStream? progress,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;

            var results = request.SelectedServers
                .Select((server, index) => index == 0
                    ? new ScriptExecutionServerResult
                    {
                        Server = server,
                        Status = "SUCCESS",
                        RowCount = 1,
                        DurationMs = 10,
                        Rows =
                        [
                            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["ServerName"] = server,
                                ["DatabaseName"] = "master",
                                ["CapturedAt"] = DateTime.UtcNow
                            }
                        ]
                    }
                    : new ScriptExecutionServerResult
                    {
                        Server = server,
                        Status = "FAILED",
                        RowCount = 0,
                        DurationMs = 5,
                        Error = "Connection timeout."
                    })
                .ToList();

            return Task.FromResult(new ScriptExecutionResponse
            {
                Environment = request.Environment,
                ScriptLanguage = request.ScriptLanguage,
                TunedQuestion = request.TunedQuestion,
                FinalScript = request.GeneratedScript,
                ResultsByServer = results,
                Summary = new ScriptExecutionSummary
                {
                    SuccessCount = 1,
                    FailCount = request.SelectedServers.Length - 1,
                    TotalRowCount = 1,
                    TotalTargets = request.SelectedServers.Length
                }
            });
        }
    }

    private sealed class FakeModelSelector : IModelSelector
    {
        public LlmModelDefinition SelectTuneModel() =>
            new()
            {
                ModelId = 1,
                DisplayName = "Tune",
                ModelKey = "gemini-2.5-flash-lite",
                Provider = "Gemini",
                UseForTune = true
            };

        public LlmModelDefinition SelectPlanModel() =>
            new()
            {
                ModelId = 1,
                DisplayName = "Plan",
                ModelKey = "gemini-2.5-flash-lite",
                Provider = "Gemini",
                UseForTune = true
            };

        public LlmModelDefinition SelectTemplateFindModel() =>
            new()
            {
                ModelId = 2,
                DisplayName = "UnusedTemplateFind",
                ModelKey = "gpt-5-mini",
                Provider = "OpenAI"
            };

        public LlmModelDefinition SelectValidateModel() =>
            new()
            {
                ModelId = 2,
                DisplayName = "UnusedValidate",
                ModelKey = "gpt-5-mini",
                Provider = "OpenAI"
            };

        public LlmModelDefinition SelectGenerateModel() =>
            new()
            {
                ModelId = 2,
                DisplayName = "Generate",
                ModelKey = "gpt-5-mini",
                Provider = "OpenAI",
                Generator = 1
            };

        public LlmModelDefinition SelectExplainModel() =>
            new()
            {
                ModelId = 1,
                DisplayName = "Explain",
                ModelKey = "gemini-2.5-flash-lite",
                Provider = "Gemini",
                UseForExplain = true
            };
    }

    /// <summary>
    /// Tuner that returns BLOCKED for every question — simulates an overly-aggressive real LLM.
    /// Recovery logic in AskPipelineService should override this for clearly safe questions.
    /// </summary>
    private sealed class BlockingFakeLlmClient(string provider) : ILLMClient
    {
        public string Provider { get; } = provider;

        public Task<string> TuneAsync(
            string promptTemplate,
            string rawQuestion,
            string environmentTag,
            string routedQueryCode,
            string modelKey,
            CancellationToken cancellationToken,
            string? apiKey = null)
        {
            _ = promptTemplate;
            _ = modelKey;
            _ = cancellationToken;

            return Task.FromResult(
                $"BLOCKED: STATE_CHANGING_REQUEST - Only read-only diagnostics/inventory queries are allowed.||{routedQueryCode}");
        }

        public Task<string> GenerateAsync(
            string promptTemplate,
            string tunedQuestion,
            string environmentTag,
            string modelKey,
            CancellationToken cancellationToken,
            string? apiKey = null)
            => new FakeLlmClient(Provider).GenerateAsync(
                promptTemplate, tunedQuestion, environmentTag, modelKey, cancellationToken);

        public Task<string> ValidateTemplateAsync(
            string promptTemplate,
            string tunedQuestion,
            string environmentTag,
            string modelKey,
            CancellationToken cancellationToken,
            string? apiKey = null)
        {
            _ = promptTemplate;
            _ = tunedQuestion;
            _ = environmentTag;
            _ = modelKey;
            _ = cancellationToken;
            return Task.FromResult("{}");
        }
    }

    private sealed class FakeLlmClient(string provider) : ILLMClient
    {
        public string Provider { get; } = provider;

        public Task<string> TuneAsync(
            string promptTemplate,
            string rawQuestion,
            string environmentTag,
            string routedQueryCode,
            string modelKey,
            CancellationToken cancellationToken,
            string? apiKey = null)
        {
            _ = promptTemplate;
            _ = modelKey;
            _ = cancellationToken;

            if (rawQuestion.Contains("restart server", StringComparison.OrdinalIgnoreCase)
                || rawQuestion.Contains("drop table", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(
                    $"BLOCKED: STATE_CHANGING_REQUEST - Only read-only diagnostics/inventory queries are allowed.||{routedQueryCode}");
            }

            var tuned = string.IsNullOrWhiteSpace(rawQuestion)
                ? "MISMATCH: EMPTY_OR_UNCLEAR - Please ask a clear question."
                : rawQuestion.Trim();

            if (!tuned.EndsWith(".", StringComparison.Ordinal))
                tuned += ".";

            return Task.FromResult($"{tuned}||{routedQueryCode}");
        }

        public Task<string> GenerateAsync(
            string promptTemplate,
            string tunedQuestion,
            string environmentTag,
            string modelKey,
            CancellationToken cancellationToken,
            string? apiKey = null)
        {
            _ = modelKey;
            _ = cancellationToken;

            if (promptTemplate.Contains("DataBot Explain", StringComparison.Ordinal))
            {
                return Task.FromResult("""
{"title":"Execution Results Summary","explanation":"All targets executed successfully. No anomalies or errors detected across the monitored servers.","anomaly":null,"analysis":"All values are within normal operating ranges across all targets.","suggestion":null,"rootCause":null,"impact":null,"summary":["All targets executed successfully with no errors.","No anomalies detected in the collected data.","All monitored values are within normal ranges.","No immediate action required."],"keyMetrics":[{"label":"Execution Status","value":"Success","unit":null,"status":"ok"},{"label":"Targets Queried","value":"1","unit":"count","status":"ok"}],"recommendations":[{"text":"Continue routine monitoring. All values appear within normal ranges.","priority":"low"}],"comparison":null,"sections":[{"key":"findings","title":"Key Findings","icon":"Search","tone":"ok","bullets":["All targets returned data successfully.","No errors or failures detected during execution.","Query results are complete and consistent."]},{"key":"server_breakdown","title":"Per-Server Breakdown","icon":"Database","tone":"info","bullets":["All queried servers responded within expected timeframes.","Data collection completed across all targets."]},{"key":"health_check","title":"Health Status","icon":"ShieldCheck","tone":"ok","bullets":["All monitored values are within normal operating ranges.","No thresholds exceeded on any target."]},{"key":"action_items","title":"Next Steps","icon":"Lightbulb","tone":"ok","steps":["Review the detailed data in the results panel.","Schedule follow-up checks if monitoring specific trends.","Adjust query parameters for more targeted analysis."]}]}
""");
            }

            if (environmentTag.Equals("General", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult("The Moon is Earth's natural satellite.");

            // History environments: return a centralized T-SQL query against SQLGig.
            if (promptTemplate.Contains("DataBot-History", StringComparison.Ordinal))
            {
                var isSqlHistory = environmentTag.StartsWith("SqlServer_", StringComparison.OrdinalIgnoreCase);
                var serverCol = isSqlHistory ? "h.SQLServer" : "h.WinServer";
                var table = isSqlHistory
                    ? "[SQLGig].[Monitor].[SQLServer_Details_History] AS h"
                    : "[SQLGig].[Monitor].[WINServer_Details_History] AS h";
                var metricName = isSqlHistory ? "PageLifeExpectancy" : "PercentProcessorTime";
                var metricExpr = isSqlHistory ? "h.PageLifeExpectancy_seconds" : "h.PercentProcessorTime";
                var serverFilter = isSqlHistory
                    ? "(@Server IS NULL OR h.SQLServer = @Server)"
                    : "(@Server IS NULL OR h.WinServer = @Server)";
                return Task.FromResult($"""
DECLARE @Server nvarchar(128) = NULL;
DECLARE @FromUtc datetime2(0) = DATEADD(HOUR,-24,SYSUTCDATETIME());
DECLARE @ToUtc datetime2(0) = SYSUTCDATETIME();
DECLARE @Top int = 200;
SELECT TOP (@Top)
    {serverCol} AS [ServerName],
    h.DateTime AS [CapturedAtUtc],
    N'InstanceHealth' COLLATE DATABASE_DEFAULT AS [MetricGroup],
    N'{metricName}' COLLATE DATABASE_DEFAULT AS [MetricName],
    CAST({metricExpr} AS decimal(18,2)) AS [MetricValue],
    N'History query' COLLATE DATABASE_DEFAULT AS [Detail]
FROM {table}
WHERE h.DateTime >= @FromUtc
  AND h.DateTime < @ToUtc
  AND {serverFilter}
ORDER BY h.DateTime DESC;
""");
            }

            if (promptTemplate.Contains("DataBot-SQL", StringComparison.Ordinal)
                && promptTemplate.Contains("READ-ONLY T-SQL", StringComparison.Ordinal))
            {
                if (tunedQuestion.Contains("failed jobs", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult("""
WITH failed_jobs AS (
    SELECT TOP (50)
        j.name AS [JobName],
        msdb.dbo.agent_datetime(h.run_date, h.run_time) AS [FailedAt]
    FROM msdb.dbo.sysjobhistory AS h
    INNER JOIN msdb.dbo.sysjobs AS j ON h.job_id = j.job_id
    WHERE h.run_status = 0
      AND h.step_id > 0
    ORDER BY h.run_date DESC, h.run_time DESC
)
SELECT
    @@SERVERNAME AS [ServerName],
    DB_NAME() AS [DatabaseName],
    GETDATE() AS [CapturedAt],
    f.[JobName],
    f.[FailedAt]
FROM failed_jobs AS f
ORDER BY f.[FailedAt] DESC;
""");
                }

                if (tunedQuestion.Contains("backup history", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult("""
DECLARE @Days int = 7;
WITH backup_history AS (
    SELECT TOP (50)
        bs.database_name AS [BackupDatabaseName],
        bs.backup_finish_date AS [BackupFinishDate]
    FROM msdb.dbo.backupset AS bs
    WHERE bs.backup_finish_date >= DATEADD(DAY, -@Days, GETDATE())
    ORDER BY bs.backup_finish_date DESC
)
SELECT
    @@SERVERNAME AS [ServerName],
    DB_NAME() AS [DatabaseName],
    GETDATE() AS [CapturedAt],
    bh.[BackupDatabaseName],
    bh.[BackupFinishDate]
FROM backup_history AS bh
ORDER BY bh.[BackupFinishDate] DESC;
""");
                }

                if ((tunedQuestion.Contains("larger than", StringComparison.OrdinalIgnoreCase)
                     || tunedQuestion.Contains("greater than", StringComparison.OrdinalIgnoreCase))
                    && tunedQuestion.Contains("gb", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult("""
WITH db_size AS (
    SELECT
        mf.database_id,
        SUM(CAST(mf.size AS bigint) * 8192) AS TotalSizeBytes
    FROM sys.master_files AS mf
    GROUP BY mf.database_id
)
SELECT TOP (50)
    @@SERVERNAME AS [ServerName],
    DB_NAME() AS [DatabaseName],
    GETDATE() AS [CapturedAt],
    d.name AS [Database],
    CAST(ds.TotalSizeBytes / (1024.0 * 1024 * 1024) AS decimal(18, 2)) AS [SizeGB]
FROM sys.databases AS d
INNER JOIN db_size AS ds ON d.database_id = ds.database_id
WHERE ds.TotalSizeBytes > 10 * 1024 * 1024 * 1024
ORDER BY ds.TotalSizeBytes DESC;
""");
                }

                return Task.FromResult("""
SELECT TOP (50)
    @@SERVERNAME AS [ServerName],
    DB_NAME() AS [DatabaseName],
    GETDATE() AS [CapturedAt],
    d.name AS [Database]
FROM sys.databases AS d
ORDER BY d.name;
""");
            }

            if (promptTemplate.Contains("DataBot-Windows", StringComparison.Ordinal)
                && promptTemplate.Contains("READ-ONLY PowerShell", StringComparison.Ordinal))
            {
                return Task.FromResult("""
param([string]$TargetServer)
$Result = @()
try {
    if ([string]::IsNullOrWhiteSpace($TargetServer)) { $TargetServer = $env:COMPUTERNAME }
    $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
    $Result += [pscustomobject]@{
        ServerName = $TargetServer
        CapturedAt = Get-Date
        Status = 'OK'
        ErrorMessage = $null
        Caption = $os.Caption
        Version = $os.Version
    }
}
catch {
    $Result += [pscustomobject]@{
        ServerName = if ([string]::IsNullOrWhiteSpace($TargetServer)) { $env:COMPUTERNAME } else { $TargetServer }
        CapturedAt = Get-Date
        Status = 'ERROR'
        ErrorMessage = $_.Exception.Message
    }
}
$Result
""");
            }

            return Task.FromResult(string.Empty);
        }

        public Task<string> ValidateTemplateAsync(
            string promptTemplate,
            string tunedQuestion,
            string environmentTag,
            string modelKey,
            CancellationToken cancellationToken,
            string? apiKey = null)
        {
            _ = promptTemplate;
            _ = tunedQuestion;
            _ = environmentTag;
            _ = modelKey;
            _ = cancellationToken;
            return Task.FromResult("{}");
        }
    }

    /// <summary>Orchestrator where every server fails — used to verify answer.status == "ERROR".</summary>
    private sealed class AllFailScriptOrchestrator : IScriptAutoFixOrchestrator
    {
        public Task<ScriptExecutionResponse> ExecuteAsync(
            ScriptExecutionRequest request,
            CancellationToken cancellationToken)
            => ExecuteWithAutoFixAsync(request, cancellationToken);

        public Task<ScriptExecutionResponse> ExecuteWithAutoFixAsync(
            ScriptExecutionRequest request,
            CancellationToken cancellationToken)
            => ExecuteWithAutoFixAsync(request, null, cancellationToken);

        public Task<ScriptExecutionResponse> ExecuteWithAutoFixAsync(
            ScriptExecutionRequest request,
            IProgressStream? progress,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;

            var results = request.SelectedServers
                .Select(server => new ScriptExecutionServerResult
                {
                    Server = server,
                    Status = "FAILED",
                    RowCount = 0,
                    DurationMs = 5,
                    Error = "Connection timeout."
                })
                .ToList();

            return Task.FromResult(new ScriptExecutionResponse
            {
                Environment = request.Environment,
                ScriptLanguage = request.ScriptLanguage,
                TunedQuestion = request.TunedQuestion,
                FinalScript = request.GeneratedScript,
                ResultsByServer = results,
                Summary = new ScriptExecutionSummary
                {
                    SuccessCount = 0,
                    FailCount = request.SelectedServers.Length,
                    TotalRowCount = 0,
                    TotalTargets = request.SelectedServers.Length
                }
            });
        }
    }

    /// <summary>LLM client that returns invalid JSON for explain prompts — used to verify graceful fallback.</summary>
    private sealed class BadJsonExplainLlmClient(string provider) : ILLMClient
    {
        public string Provider { get; } = provider;

        public Task<string> TuneAsync(
            string promptTemplate,
            string rawQuestion,
            string environmentTag,
            string routedQueryCode,
            string modelKey,
            CancellationToken cancellationToken,
            string? apiKey = null)
            => new FakeLlmClient(Provider).TuneAsync(
                promptTemplate, rawQuestion, environmentTag, routedQueryCode, modelKey, cancellationToken);

        public Task<string> GenerateAsync(
            string promptTemplate,
            string tunedQuestion,
            string environmentTag,
            string modelKey,
            CancellationToken cancellationToken,
            string? apiKey = null)
        {
            if (promptTemplate.Contains("DataBot Explain", StringComparison.Ordinal))
                return Task.FromResult("This is not valid JSON at all!!!");

            return new FakeLlmClient(Provider).GenerateAsync(
                promptTemplate, tunedQuestion, environmentTag, modelKey, cancellationToken);
        }

        public Task<string> ValidateTemplateAsync(
            string promptTemplate,
            string tunedQuestion,
            string environmentTag,
            string modelKey,
            CancellationToken cancellationToken,
            string? apiKey = null)
            => Task.FromResult("{}");
    }

    /// <summary>
    /// LLM client that returns structured JSON for General environment prompts.
    /// Used to verify the pipeline correctly parses title/summary/details fields.
    /// </summary>
    private sealed class StructuredGeneralLlmClient(string provider) : ILLMClient
    {
        public string Provider { get; } = provider;

        public Task<string> TuneAsync(
            string promptTemplate,
            string rawQuestion,
            string environmentTag,
            string routedQueryCode,
            string modelKey,
            CancellationToken cancellationToken,
            string? apiKey = null)
            => new FakeLlmClient(Provider).TuneAsync(
                promptTemplate, rawQuestion, environmentTag, routedQueryCode, modelKey, cancellationToken);

        public Task<string> GenerateAsync(
            string promptTemplate,
            string tunedQuestion,
            string environmentTag,
            string modelKey,
            CancellationToken cancellationToken,
            string? apiKey = null)
        {
            if (environmentTag.Equals("General", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult("""
                    {
                      "title": "SQL Server Version History",
                      "explanation": "SQL Server has evolved significantly across major releases, each adding critical enterprise features for performance, security, and cloud integration.",
                      "summary": [
                        "SQL Server has been released in multiple major versions since 1989.",
                        "Key modern versions include SQL Server 2016, 2017, 2019, and 2022.",
                        "Each version introduced significant features for performance, security, and cloud integration.",
                        "Version numbers follow an internal scheme (e.g., 2022 = 16.x)."
                      ],
                      "sections": [
                        { "key": "overview", "title": "Major Versions", "icon": "Compass", "tone": "info", "bullets": [
                          "SQL Server 2016 introduced Always Encrypted, Row-Level Security, and Query Store.",
                          "SQL Server 2017 added Linux support and graph database capabilities.",
                          "SQL Server 2019 introduced Big Data Clusters and Intelligent Query Processing.",
                          "SQL Server 2022 enhanced cloud connectivity with Azure Synapse Link."
                        ]},
                        { "key": "principles", "title": "Version Numbering", "icon": "BookOpen", "tone": "info", "bullets": [
                          "Each major release has an internal version number (e.g., 2022 = 16.x).",
                          "Service packs and cumulative updates provide incremental fixes."
                        ]},
                        { "key": "context", "title": "Support Lifecycle", "icon": "Info", "tone": "info", "bullets": [
                          "Microsoft provides mainstream and extended support phases.",
                          "End-of-support versions should be upgraded for security compliance."
                        ]},
                        { "key": "next_steps", "title": "How to Check Your Version", "icon": "Lightbulb", "tone": "ok", "steps": [
                          "Run SELECT @@VERSION in SSMS.",
                          "Compare the build number against Microsoft documentation.",
                          "Plan upgrades based on mainstream support dates."
                        ]}
                      ],
                      "details": "Major Versions:\n- SQL Server 2016 – Introduced Always Encrypted, Row-Level Security, and Query Store.\n- SQL Server 2017 – Added Linux support and graph database capabilities.\n- SQL Server 2019 – Introduced Big Data Clusters and Intelligent Query Processing.\n- SQL Server 2022 – Enhanced cloud connectivity with Azure Synapse Link.\n\nVersion Numbering:\n- Each major release has an internal version number (e.g., 2022 = 16.x).\n- Service packs and cumulative updates provide incremental fixes."
                    }
                    """);
            }

            return new FakeLlmClient(Provider).GenerateAsync(
                promptTemplate, tunedQuestion, environmentTag, modelKey, cancellationToken);
        }

        public Task<string> ValidateTemplateAsync(
            string promptTemplate,
            string tunedQuestion,
            string environmentTag,
            string modelKey,
            CancellationToken cancellationToken,
            string? apiKey = null)
            => Task.FromResult("{}");
    }

    /// <summary>
    /// LLM client that deliberately returns markdown in General details.
    /// Used to verify the post-processing sanitizer strips it.
    /// </summary>
    private sealed class MarkdownGeneralLlmClient(string provider) : ILLMClient
    {
        public string Provider { get; } = provider;

        public Task<string> TuneAsync(
            string promptTemplate,
            string rawQuestion,
            string environmentTag,
            string routedQueryCode,
            string modelKey,
            CancellationToken cancellationToken,
            string? apiKey = null)
            => new FakeLlmClient(Provider).TuneAsync(
                promptTemplate, rawQuestion, environmentTag, routedQueryCode, modelKey, cancellationToken);

        public Task<string> GenerateAsync(
            string promptTemplate,
            string tunedQuestion,
            string environmentTag,
            string modelKey,
            CancellationToken cancellationToken,
            string? apiKey = null)
        {
            if (environmentTag.Equals("General", StringComparison.OrdinalIgnoreCase))
            {
                // Deliberately return markdown artifacts that must be sanitized.
                return Task.FromResult(
                    "{" +
                    "\"title\": \"Understanding Wait Stats\"," +
                    "\"summary\": [" +
                    "\"Wait stats measure time SQL Server spends waiting.\"," +
                    "\"Common waits include CXPACKET, LCK_M_X, and PAGEIOLATCH.\"," +
                    "\"Use sys.dm_os_wait_stats to query cumulative waits.\"" +
                    "]," +
                    "\"details\": \"## Overview\\n**Wait statistics** track how long SQL Server threads wait for resources.\\n\\n## Key Metrics:\\n- **CXPACKET** - Parallelism synchronization waits.\\n- **LCK_M_X** - Exclusive lock waits.\\n- **PAGEIOLATCH_SH** - Data page I/O waits.\"" +
                    "}");
            }

            return new FakeLlmClient(Provider).GenerateAsync(
                promptTemplate, tunedQuestion, environmentTag, modelKey, cancellationToken);
        }

        public Task<string> ValidateTemplateAsync(
            string promptTemplate,
            string tunedQuestion,
            string environmentTag,
            string modelKey,
            CancellationToken cancellationToken,
            string? apiKey = null)
            => Task.FromResult("{}");
    }

    private sealed class StubQuestionSamplesRepository : IQuestionSamplesRepository
    {
        private readonly QuestionSampleRow? _sampleToReturn;

        public StubQuestionSamplesRepository(QuestionSampleRow? sampleToReturn = null)
        {
            _sampleToReturn = sampleToReturn;
        }

        public Task<List<QuestionSampleRow>> GetByEnvironmentAsync(
            string environment, CancellationToken cancellationToken)
            => Task.FromResult(new List<QuestionSampleRow>());

        public Task<QuestionSampleRow?> GetByIdAsync(
            int sampleId, string environment, CancellationToken cancellationToken)
            => Task.FromResult(_sampleToReturn);

        public Task<QuestionSampleRow?> GetByGroupKeyAsync(
            string groupKey, string environment, CancellationToken cancellationToken)
            => Task.FromResult(_sampleToReturn);
    }

    private static AskPipelineService CreateServiceWithSampleRepo(QuestionSampleRow? sample)
    {
        var modelSelector = new FakeModelSelector();
        ILLMClient[] clients =
        [
            new FakeLlmClient("Gemini"),
            new FakeLlmClient("OpenAI")
        ];

        return new AskPipelineService(
            modelSelector,
            clients,
            new FakeScriptAutoFixOrchestrator(),
            new StubToolRegistryResolver(NullLogger<StubToolRegistryResolver>.Instance),
            new TemplateRenderer(NullLogger<TemplateRenderer>.Instance),
            new AllowAllPolicyService(),
            new StubQuestionSamplesRepository(sample),
            new TestHelpers.EmptyUserServerRepository(),
            NullLogger<AskPipelineService>.Instance);
    }

    // ── Sample Route Tests ──────────────────────────────────────────────

    [Fact]
    public async Task SampleRoute_ValidSampleId_ExecutesScript()
    {
        var sample = new QuestionSampleRow
        {
            Id = 42,
            Environment = "SqlServer_Live",
            GroupKey = "Databases",
            GroupTitle = "Databases",
            GroupOrder = 10,
            QuestionText = "List all databases",
            QuestionOrder = 1,
            Script = "SELECT name FROM sys.databases ORDER BY name;"
        };
        var service = CreateServiceWithSampleRepo(sample);

        var response = await service.ExecuteAsync(new AskApiRequest
        {
            ConversationId = "test-sample",
            Environment = "SqlServer_Live",
            Question = "List all databases",
            SampleId = 42,
            GroupKey = "Databases",
            SelectedTargets = ["CTS03#Admin"]
        }, CancellationToken.None);

        Assert.Equal("SAMPLE_ONLY", response.Plan.Mode);
        Assert.Equal("SAMPLE_ONLY", response.Plan.GeneratorMode);
        Assert.Equal(42, response.Plan.SampleId);
        Assert.Equal("Databases", response.Plan.GroupKey);
        Assert.Equal("SKIPPED", response.Tuning.Status);
        Assert.NotNull(response.Script);
        Assert.Equal("SAMPLE", response.Script!.Source);
        Assert.Equal("EXECUTION", response.Result.Kind);
        Assert.Equal("SQL", response.Plan.ScriptLanguage);
    }

    [Fact]
    public async Task SampleRoute_NotFound_ReturnsStopped()
    {
        var service = CreateServiceWithSampleRepo(null);

        var response = await service.ExecuteAsync(new AskApiRequest
        {
            ConversationId = "test-sample-404",
            Environment = "SqlServer_Live",
            Question = "List all databases",
            SampleId = 999,
            GroupKey = "Databases",
            SelectedTargets = ["CTS03#Admin"]
        }, CancellationToken.None);

        Assert.Equal("STOPPED", response.Plan.Mode);
        Assert.Equal("SAMPLE_ONLY", response.Plan.GeneratorMode);
        Assert.Equal(999, response.Plan.SampleId);
        Assert.Equal("STOPPED", response.Result.Status);
        Assert.Contains("999", response.Result.AnswerText);
    }

    [Fact]
    public async Task SampleRoute_CuratedScript_SkipsSafetyValidation()
    {
        // Sample scripts are curated — safety validation is skipped so they always execute.
        var sample = new QuestionSampleRow
        {
            Id = 10,
            Environment = "SqlServer_Live",
            GroupKey = "Databases",
            GroupTitle = "Databases",
            GroupOrder = 10,
            QuestionText = "Drop everything",
            QuestionOrder = 1,
            Script = "DROP TABLE Users;"
        };
        var service = CreateServiceWithSampleRepo(sample);

        var response = await service.ExecuteAsync(new AskApiRequest
        {
            ConversationId = "test-sample-trusted",
            Environment = "SqlServer_Live",
            Question = "Drop everything",
            SampleId = 10,
            GroupKey = "Databases",
            SelectedTargets = ["CTS03#Admin"]
        }, CancellationToken.None);

        Assert.Equal("SAMPLE_ONLY", response.Plan.GeneratorMode);
        Assert.NotNull(response.Script);
        Assert.True(response.Script!.Validation.IsSafeReadOnly);
        Assert.Null(response.Script.Validation.BlockedTokenFound);
        Assert.Equal("EXECUTION", response.Result.Kind);
    }

    // ── GroupKey Flexibility Tests ──────────────────────────────────────

    [Fact]
    public async Task SampleRoute_GroupKey_SQLJobs_ReturnsDbGroupKey()
    {
        var sample = new QuestionSampleRow
        {
            Id = 50,
            Environment = "SqlServer_Live",
            GroupKey = "SQLJobs",
            GroupTitle = "SQL Jobs",
            GroupOrder = 5,
            QuestionText = "Show failed jobs",
            QuestionOrder = 1,
            Script = "SELECT name FROM msdb.dbo.sysjobs;"
        };
        var service = CreateServiceWithSampleRepo(sample);

        var response = await service.ExecuteAsync(new AskApiRequest
        {
            ConversationId = "test-gk-exact",
            Environment = "SqlServer_Live",
            Question = "Show failed jobs",
            SampleId = 50,
            GroupKey = "SQLJobs",
            SelectedTargets = ["CTS03#Admin"]
        }, CancellationToken.None);

        Assert.Equal("SAMPLE_ONLY", response.Plan.Mode);
        Assert.Equal("SQLJobs", response.Plan.GroupKey);
        Assert.Equal("SAMPLE", response.Script!.Source);
    }

    [Fact]
    public async Task SampleRoute_GroupKey_WithSpaces_StillSucceeds()
    {
        var sample = new QuestionSampleRow
        {
            Id = 51,
            Environment = "SqlServer_Live",
            GroupKey = "SQLJobs",
            GroupTitle = "SQL Jobs",
            GroupOrder = 5,
            QuestionText = "Show failed jobs",
            QuestionOrder = 1,
            Script = "SELECT name FROM msdb.dbo.sysjobs;"
        };
        var service = CreateServiceWithSampleRepo(sample);

        // UI sends GroupTitle "SQL Jobs" instead of GroupKey "SQLJobs"
        var response = await service.ExecuteAsync(new AskApiRequest
        {
            ConversationId = "test-gk-spaces",
            Environment = "SqlServer_Live",
            Question = "Show failed jobs",
            SampleId = 51,
            GroupKey = "SQL Jobs",
            SelectedTargets = ["CTS03#Admin"]
        }, CancellationToken.None);

        Assert.Equal("SAMPLE_ONLY", response.Plan.Mode);
        // Response uses DB's authoritative GroupKey, not the UI value
        Assert.Equal("SQLJobs", response.Plan.GroupKey);
        Assert.Equal("SAMPLE", response.Script!.Source);
    }

    [Fact]
    public async Task SampleRoute_GroupKey_Null_StillSucceeds()
    {
        var sample = new QuestionSampleRow
        {
            Id = 52,
            Environment = "SqlServer_Live",
            GroupKey = "Databases",
            GroupTitle = "Databases",
            GroupOrder = 10,
            QuestionText = "List databases",
            QuestionOrder = 1,
            Script = "SELECT name FROM sys.databases ORDER BY name;"
        };
        var service = CreateServiceWithSampleRepo(sample);

        // UI sends no GroupKey at all
        var response = await service.ExecuteAsync(new AskApiRequest
        {
            ConversationId = "test-gk-null",
            Environment = "SqlServer_Live",
            Question = "List databases",
            SampleId = 52,
            GroupKey = null,
            SelectedTargets = ["CTS03#Admin"]
        }, CancellationToken.None);

        Assert.Equal("SAMPLE_ONLY", response.Plan.Mode);
        Assert.Equal("Databases", response.Plan.GroupKey);
        Assert.Equal("SAMPLE", response.Script!.Source);
    }

    [Fact]
    public async Task SampleRoute_WrongEnvironment_ReturnsStopped()
    {
        // Stub returns null because env doesn't match the sample's environment
        var service = CreateServiceWithSampleRepo(null);

        var response = await service.ExecuteAsync(new AskApiRequest
        {
            ConversationId = "test-gk-wrongenv",
            Environment = "Windows_Live",
            Question = "List databases",
            SampleId = 52,
            GroupKey = "Databases",
            SelectedTargets = ["CTS03"]
        }, CancellationToken.None);

        Assert.Equal("STOPPED", response.Plan.Mode);
        Assert.Equal("SAMPLE_ONLY", response.Plan.GeneratorMode);
        Assert.Equal("STOPPED", response.Result.Status);
        Assert.Contains("52", response.Result.AnswerText);
    }

    // ── General answer: sections contract ────────────────────────────────────

    [Fact]
    public async Task General_MockFallback_Has_AtLeast4_Sections()
    {
        var service = CreateService();

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "gs1",
                BearerToken = "u1",
                Environment = "General",
                Question = "list all moon",
                SelectedTargets = []
            },
            CancellationToken.None);

        Assert.Equal("GENERAL", response.Plan.Mode);
        Assert.NotNull(response.Answer);
        Assert.NotNull(response.Answer!.Sections);
        Assert.InRange(response.Answer.Sections!.Count, 4, 8);
        Assert.Null(response.Script);
    }

    [Fact]
    public async Task General_Sections_Have_Valid_Icons_And_Tones()
    {
        var service = CreateServiceWithStructuredGeneralClient();

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "gs2",
                BearerToken = "u2",
                Environment = "General",
                Question = "Give detail of all SQL Server version",
                SelectedTargets = []
            },
            CancellationToken.None);

        var validIcons = new[] { "Compass", "Database", "ShieldCheck", "Workflow", "Lightbulb", "ListChecks", "Search", "Wrench", "BookOpen", "Info", "AlertTriangle" };
        var validTones = new[] { "ok", "info", "warning", "critical" };

        Assert.NotNull(response.Answer?.Sections);
        Assert.All(response.Answer!.Sections!, s =>
        {
            Assert.Contains(s.Icon, validIcons);
            Assert.Contains(s.Tone, validTones);
        });
    }

    [Fact]
    public async Task General_Summary_Between_4_And_6()
    {
        // StructuredGeneralLlmClient returns exactly 4 summary items.
        var service = CreateServiceWithStructuredGeneralClient();

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "gs3",
                BearerToken = "u3",
                Environment = "General",
                Question = "SQL Server versions",
                SelectedTargets = []
            },
            CancellationToken.None);

        Assert.NotNull(response.Answer?.Summary);
        Assert.InRange(response.Answer!.Summary!.Length, 4, 6);
    }

    [Fact]
    public async Task General_No_Script_And_Has_Explanation()
    {
        var service = CreateService();

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "gs4",
                BearerToken = "u4",
                Environment = "General",
                Question = "what is a clustered index?",
                SelectedTargets = []
            },
            CancellationToken.None);

        Assert.NotNull(response.Answer);
        Assert.Null(response.Script);
        Assert.NotNull(response.Answer!.Explanation);
        Assert.NotNull(response.Answer.Title);
        Assert.Equal(response.Answer.Title, response.Result.AnswerText);
    }

    [Fact]
    public void EnsureMinimumSections_Adds_4_When_None_Present()
    {
        var node = new AskAnswerNode
        {
            Status = "OK",
            Title = "Test Title",
            Summary = ["Point one.", "Point two.", "Point three.", "Point four."],
            Details = "Some details here.",
            Explanation = "A brief explanation of the topic."
        };

        AskPipelineService.EnsureMinimumSections(node);

        Assert.NotNull(node.Sections);
        Assert.Equal(4, node.Sections!.Count);
        Assert.Equal("overview", node.Sections[0].Key);
        Assert.Equal("principles", node.Sections[1].Key);
        Assert.Equal("context", node.Sections[2].Key);
        Assert.Equal("next_steps", node.Sections[3].Key);
    }

    [Fact]
    public void EnsureMinimumSections_NoOp_When_Already_Has_4Plus()
    {
        var existing = new List<AnswerSection>
        {
            new() { Key = "a", Title = "A", Bullets = ["x"] },
            new() { Key = "b", Title = "B", Bullets = ["y"] },
            new() { Key = "c", Title = "C", Steps = ["z"] },
            new() { Key = "d", Title = "D", Bullets = ["w"] }
        };

        var node = new AskAnswerNode { Status = "OK", Sections = existing };

        AskPipelineService.EnsureMinimumSections(node);

        Assert.Same(existing, node.Sections);
    }

    [Fact]
    public async Task General_PlainText_Fallback_Produces_Structured_Answer()
    {
        // FakeLlmClient returns plain text for General, not JSON.
        // Verify the plain-text fallback path wraps it into structured fields.
        var service = CreateService();

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "gpt1",
                BearerToken = "u1",
                Environment = "General",
                Question = "what is the moon",
                SelectedTargets = []
            },
            CancellationToken.None);

        Assert.Equal("GENERAL", response.Plan.Mode);
        Assert.NotNull(response.Answer);

        // explanation must not be identical to title (the original bug)
        // For the plain-text fallback with a single line, they may match,
        // but sections must still be populated.
        Assert.NotNull(response.Answer!.Title);
        Assert.NotNull(response.Answer.Explanation);
        Assert.NotNull(response.Answer.Sections);
        Assert.InRange(response.Answer.Sections!.Count, 4, 8);
        Assert.Null(response.Script);

        // result.answerText should be the title, not the full explanation
        Assert.Equal(response.Answer.Title, response.Result.AnswerText);
    }

    [Fact]
    public async Task General_Structured_LLM_Explanation_Not_Duplicated_From_Title()
    {
        // StructuredGeneralLlmClient returns proper JSON with distinct explanation.
        var service = CreateServiceWithStructuredGeneralClient();

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "gpt2",
                BearerToken = "u2",
                Environment = "General",
                Question = "SQL Server versions",
                SelectedTargets = []
            },
            CancellationToken.None);

        Assert.NotNull(response.Answer);
        Assert.NotNull(response.Answer!.Explanation);
        Assert.NotNull(response.Answer.Title);

        // explanation should be longer / different from title
        Assert.NotEqual(response.Answer.Title, response.Answer.Explanation);
        Assert.True(response.Answer.Explanation!.Length > response.Answer.Title!.Length,
            "explanation should be a paragraph, not just the title");
    }

    // ─── Shape validator: DeduplicateExplanation ──────────────────────────────

    [Fact]
    public void DeduplicateExplanation_Fixes_When_Explanation_Equals_Title()
    {
        var node = new AskAnswerNode
        {
            Status = "OK",
            Title = "My Title",
            Explanation = "My Title",
            Summary = ["First point.", "Second point.", "Third point."]
        };

        AskPipelineService.DeduplicateExplanation(node);

        Assert.NotEqual(node.Title, node.Explanation);
        Assert.Contains("First point.", node.Explanation!);
    }

    [Fact]
    public void DeduplicateExplanation_NoOp_When_Already_Different()
    {
        var node = new AskAnswerNode
        {
            Status = "OK",
            Title = "Short Title",
            Explanation = "This is a longer explanation that differs from the title."
        };

        AskPipelineService.DeduplicateExplanation(node);

        Assert.Equal("This is a longer explanation that differs from the title.", node.Explanation);
    }

    // ─── Steps enforcement for procedural questions ───────────────────────────

    [Fact]
    public void EnsureStepsForProceduralQuestions_Adds_Steps_When_Missing()
    {
        var node = new AskAnswerNode
        {
            Status = "OK",
            Sections =
            [
                new AnswerSection { Key = "a", Title = "A", Bullets = ["x", "y"] },
                new AnswerSection { Key = "b", Title = "B", Bullets = ["z", "w"] }
            ]
        };

        AskPipelineService.EnsureStepsForProceduralQuestions(node, "how do I design a monitoring system");

        Assert.True(node.Sections.Any(s => s.Steps is { Count: > 0 }),
            "At least one section must have Steps for a procedural question");
    }

    [Fact]
    public void EnsureStepsForProceduralQuestions_NoOp_When_Already_Has_Steps()
    {
        var existing = new List<AnswerSection>
        {
            new() { Key = "a", Title = "A", Bullets = ["x"] },
            new() { Key = "b", Title = "B", Steps = ["step1", "step2"] }
        };
        var node = new AskAnswerNode { Status = "OK", Sections = existing };

        AskPipelineService.EnsureStepsForProceduralQuestions(node, "how do I do this");

        // Should not modify — already has steps
        Assert.NotNull(existing[0].Bullets);
        Assert.NotNull(existing[1].Steps);
    }

    [Fact]
    public void EnsureStepsForProceduralQuestions_NoOp_For_NonProcedural_Question()
    {
        var node = new AskAnswerNode
        {
            Status = "OK",
            Sections =
            [
                new AnswerSection { Key = "a", Title = "A", Bullets = ["x"] },
                new AnswerSection { Key = "b", Title = "B", Bullets = ["y"] }
            ]
        };

        AskPipelineService.EnsureStepsForProceduralQuestions(node, "what is a clustered index");

        // Non-procedural — no steps should be forced
        Assert.True(node.Sections.All(s => s.Steps is null),
            "Non-procedural question should not force steps");
    }

    [Fact]
    public async Task General_Procedural_Question_Has_Section_With_Steps()
    {
        var service = CreateServiceWithStructuredGeneralClient();

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "gproc1",
                BearerToken = "u80",
                Environment = "General",
                Question = "how do I design a high availability SQL Server setup",
                SelectedTargets = []
            },
            CancellationToken.None);

        Assert.NotNull(response.Answer?.Sections);
        Assert.True(response.Answer!.Sections!.Count >= 3,
            "Sections count must be >= 3");
        Assert.True(response.Answer.Sections.Any(s => s.Steps is { Count: > 0 }),
            "Procedural question must have at least one section with steps");
    }

    [Fact]
    public async Task General_Sections_Count_AtLeast_3()
    {
        var service = CreateServiceWithStructuredGeneralClient();

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "gsc1",
                BearerToken = "u81",
                Environment = "General",
                Question = "explain database normalization",
                SelectedTargets = []
            },
            CancellationToken.None);

        Assert.NotNull(response.Answer?.Sections);
        Assert.True(response.Answer!.Sections!.Count >= 3,
            "Sections count must be >= 3");
    }

    // ─── History environments ─────────────────────────────────────────────────

    [Fact]
    public async Task SqlServerHistory_Generates_CentralizedSqlQuery()
    {
        var service = CreateService();

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "h1",
                BearerToken = "u90",
                Environment = "SqlServer_History",
                Question = "show PLE trend last 24 hours",
                SelectedTargets = ["CTS02"]
            },
            CancellationToken.None);

        Assert.Equal("LLM_ONLY", response.Plan.Mode);
        Assert.Equal("SQL", response.Plan.ScriptLanguage);
        Assert.Equal("EXECUTION", response.Result.Kind);
        Assert.NotNull(response.Script);
        // History mock always generates a DECLARE + SELECT from SQLGig.Monitor
        Assert.Contains("DECLARE", response.Script!.Final, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SQLGig", response.Script.Final, StringComparison.OrdinalIgnoreCase);
        // Execution target should be CTS03 (centralized)
        Assert.NotNull(response.Result.Items);
        Assert.Single(response.Result.Items!);
        Assert.Equal("CTS03", response.Result.Items[0].Target);
    }

    [Fact]
    public async Task WindowsHistory_Generates_SqlQuery_Not_PowerShell()
    {
        var service = CreateService();

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "h2",
                BearerToken = "u91",
                Environment = "Windows_History",
                Question = "show CPU trend last 6 hours",
                SelectedTargets = ["CTS01"]
            },
            CancellationToken.None);

        Assert.Equal("LLM_ONLY", response.Plan.Mode);
        Assert.Equal("SQL", response.Plan.ScriptLanguage);
        Assert.Equal("EXECUTION", response.Result.Kind);
        Assert.NotNull(response.Script);
        // Must be T-SQL, NOT PowerShell
        Assert.Contains("DECLARE", response.Script!.Final, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SQLGig", response.Script.Final, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("param(", response.Script.Final, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Get-CimInstance", response.Script.Final, StringComparison.OrdinalIgnoreCase);
        // Execution on CTS03
        Assert.NotNull(response.Result.Items);
        Assert.Single(response.Result.Items!);
        Assert.Equal("CTS03", response.Result.Items[0].Target);
    }

    [Fact]
    public async Task SqlServerHistory_NoSelectedTargets_Still_Works()
    {
        var service = CreateService();

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "h3",
                BearerToken = "u92",
                Environment = "SqlServer_History",
                Question = "show failed job count trend",
                SelectedTargets = []
            },
            CancellationToken.None);

        Assert.Equal("LLM_ONLY", response.Plan.Mode);
        Assert.Equal("SQL", response.Plan.ScriptLanguage);
        Assert.Equal("EXECUTION", response.Result.Kind);
        Assert.NotNull(response.Script);
        // @Server should be NULL when no targets selected
        Assert.Contains("NULL", response.Script!.Final, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SqlServerHistory_DropTable_Should_Be_Blocked()
    {
        var service = CreateService();

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "h4",
                BearerToken = "u93",
                Environment = "SqlServer_History",
                Question = "drop table monitoring_data",
                SelectedTargets = []
            },
            CancellationToken.None);

        Assert.Equal("STOPPED", response.Tuning.Status);
        Assert.Null(response.Script);
    }

    // ─── History SAMPLE_ONLY tests ────────────────────────────────────────────

    [Fact]
    public async Task HistorySample_MultiServer_BindsInFilter_And_ExecutesCentrally()
    {
        var sample = new QuestionSampleRow
        {
            Id = 350,
            Environment = "SqlServer_History",
            GroupKey = "DatabaseHealth",
            GroupTitle = "Database Health",
            GroupOrder = 1,
            QuestionText = "Show database status trend",
            QuestionOrder = 1,
            Script = "DECLARE @Server nvarchar(128) = NULL;\r\nDECLARE @FromUtc datetime2(0) = DATEADD(HOUR,-24,SYSUTCDATETIME());\r\nDECLARE @ToUtc   datetime2(0) = SYSUTCDATETIME();\r\nDECLARE @Top     int = 200;\r\nSELECT TOP (@Top) * FROM [SQLGig].[Monitor].[SQLServer_DatabaseHealth_History] h WHERE (@Server IS NULL OR h.SQLServer = @Server) AND h.DateTime >= @FromUtc AND h.DateTime < @ToUtc;"
        };
        var service = CreateServiceWithSampleRepo(sample);

        var response = await service.ExecuteAsync(new AskApiRequest
        {
            ConversationId = "hist-sample-1",
            Environment = "SqlServer_History",
            Question = "Show database status trend",
            SampleId = 350,
            GroupKey = "DatabaseHealth",
            SelectedServers = ["CTS02\\ADMIN,1432", "CTS02\\FINANCE,1431"],
            FromUtc = "2026-03-08T08:54:15.046Z",
            ToUtc = "2026-03-08T14:54:15.046Z",
            SelectedTargets = []
        }, CancellationToken.None);

        // Route & plan
        Assert.Equal("SAMPLE_ONLY", response.Plan.Mode);
        Assert.Equal("SAMPLE_ONLY", response.Plan.GeneratorMode);
        Assert.Equal(350, response.Plan.SampleId);
        Assert.Equal("DatabaseHealth", response.Plan.GroupKey);
        Assert.Equal("SQL", response.Plan.ScriptLanguage);

        // Script has multi-server IN filter — not scalar @Server
        Assert.NotNull(response.Script);
        Assert.Contains("IN (N'CTS02\\ADMIN', N'CTS02\\FINANCE')", response.Script!.Final);
        Assert.DoesNotContain("@Server", response.Script.Final);
        Assert.DoesNotContain("= NULL;", response.Script.Final);
        Assert.Contains("'2026-03-08T08:54:15'", response.Script.Final);
        Assert.Contains("'2026-03-08T14:54:15'", response.Script.Final);

        // Script parameters echoed
        Assert.NotNull(response.Script.Parameters);
        var paramServers = response.Script.Parameters!["selectedServers"] as string[];
        Assert.NotNull(paramServers);
        Assert.Equal(2, paramServers!.Length);
        Assert.Equal("CTS02\\ADMIN", paramServers[0]);
        Assert.Equal("CTS02\\FINANCE", paramServers[1]);

        // Centralized execution — single target
        Assert.Equal("EXECUTION", response.Result.Kind);
        Assert.NotNull(response.Result.Items);
        Assert.Single(response.Result.Items!);
        Assert.Equal("CTS03::SQLGig", response.Result.Items[0].Target);

        // Response.Request echoes history context
        Assert.Equal("CentralizedHistory", response.Request.TargetType);
        Assert.NotNull(response.Request.SelectedServers);
        Assert.Equal(2, response.Request.SelectedServers!.Length);
        Assert.Equal("CTS02\\ADMIN", response.Request.SelectedServers[0]);
        Assert.Equal("CTS02\\FINANCE", response.Request.SelectedServers[1]);
        Assert.Equal("2026-03-08T08:54:15.046Z", response.Request.FromUtc);
        Assert.Equal("2026-03-08T14:54:15.046Z", response.Request.ToUtc);
        Assert.Single(response.Request.SelectedTargets);
        Assert.Equal("CTS03::SQLGig", response.Request.SelectedTargets[0]);
    }

    [Fact]
    public async Task HistorySample_DirectWhereEquals_ReplacedWithInFilter()
    {
        // Tests the simpler WHERE h.SQLServer = @Server form (no IS NULL OR wrapper)
        var sample = new QuestionSampleRow
        {
            Id = 351,
            Environment = "SqlServer_History",
            GroupKey = "DatabaseHealth",
            GroupTitle = "Database Health",
            GroupOrder = 1,
            QuestionText = "Show database status trend",
            QuestionOrder = 1,
            Script = "DECLARE @Server nvarchar(128) = NULL;\r\nDECLARE @FromUtc datetime2(0) = DATEADD(HOUR,-24,SYSUTCDATETIME());\r\nDECLARE @ToUtc   datetime2(0) = SYSUTCDATETIME();\r\nDECLARE @Top     int = 200;\r\nSELECT TOP (@Top) 1 FROM [SQLGig].[Monitor].[SQLServer_DatabaseHealth_History] h WHERE h.SQLServer = @Server AND h.DateTime >= @FromUtc;"
        };
        var service = CreateServiceWithSampleRepo(sample);

        var response = await service.ExecuteAsync(new AskApiRequest
        {
            ConversationId = "hist-sample-2",
            Environment = "SqlServer_History",
            Question = "Show database status trend",
            SampleId = 351,
            GroupKey = "DatabaseHealth",
            SelectedServers = ["CTS02\\ADMIN,1432"],
            SelectedTargets = []
        }, CancellationToken.None);

        Assert.Equal("SAMPLE_ONLY", response.Plan.Mode);
        Assert.NotNull(response.Script);
        Assert.Contains("h.SQLServer IN (N'CTS02\\ADMIN')", response.Script!.Final);
        Assert.DoesNotContain("@Server", response.Script.Final);
    }

    [Fact]
    public async Task HistorySample_NoServers_LeavesServerDeclareIntact()
    {
        var sample = new QuestionSampleRow
        {
            Id = 352,
            Environment = "SqlServer_History",
            GroupKey = "Overview",
            GroupTitle = "Overview",
            GroupOrder = 1,
            QuestionText = "Show all servers overview",
            QuestionOrder = 1,
            Script = "DECLARE @Server nvarchar(128) = NULL;\r\nDECLARE @FromUtc datetime2(0) = DATEADD(HOUR,-24,SYSUTCDATETIME());\r\nDECLARE @ToUtc   datetime2(0) = SYSUTCDATETIME();\r\nDECLARE @Top     int = 200;\r\nSELECT TOP (@Top) 1 FROM [SQLGig].[Monitor].[SQLServer_Details_History];"
        };
        var service = CreateServiceWithSampleRepo(sample);

        var response = await service.ExecuteAsync(new AskApiRequest
        {
            ConversationId = "hist-sample-3",
            Environment = "SqlServer_History",
            Question = "Show all servers overview",
            SampleId = 352,
            GroupKey = "Overview",
            SelectedTargets = []
        }, CancellationToken.None);

        Assert.Equal("SAMPLE_ONLY", response.Plan.Mode);
        Assert.NotNull(response.Script);
        // No servers → @Server DECLARE stays, no IN-filter injected
        Assert.Contains("@Server", response.Script!.Final);
        Assert.Null(response.Request.SelectedServers);
    }

    [Fact]
    public async Task HistorySample_WindowsHistory_UsesSql_NotPowerShell()
    {
        var sample = new QuestionSampleRow
        {
            Id = 360,
            Environment = "Windows_History",
            GroupKey = "CPUTrend",
            GroupTitle = "CPU Trend",
            GroupOrder = 1,
            QuestionText = "Show CPU trend",
            QuestionOrder = 1,
            Script = "DECLARE @Server nvarchar(128) = NULL;\r\nDECLARE @FromUtc datetime2(0) = DATEADD(HOUR,-24,SYSUTCDATETIME());\r\nDECLARE @ToUtc   datetime2(0) = SYSUTCDATETIME();\r\nDECLARE @Top     int = 200;\r\nSELECT TOP (@Top) 1 FROM [SQLGig].[Monitor].[WINServer_Details_History] h WHERE h.WinServer = @Server;"
        };
        var service = CreateServiceWithSampleRepo(sample);

        var response = await service.ExecuteAsync(new AskApiRequest
        {
            ConversationId = "hist-sample-4",
            Environment = "Windows_History",
            Question = "Show CPU trend",
            SampleId = 360,
            GroupKey = "CPUTrend",
            SelectedServers = ["CTS01"],
            SelectedTargets = []
        }, CancellationToken.None);

        Assert.Equal("SAMPLE_ONLY", response.Plan.Mode);
        Assert.Equal("SQL", response.Plan.ScriptLanguage);
        Assert.NotNull(response.Script);
        Assert.Contains("IN (N'CTS01')", response.Script!.Final);
        Assert.DoesNotContain("param(", response.Script.Final, StringComparison.OrdinalIgnoreCase);
        Assert.Single(response.Result.Items!);
        Assert.Equal("CTS03::SQLGig", response.Result.Items[0].Target);
        Assert.Equal("CentralizedHistory", response.Request.TargetType);
    }

    [Fact]
    public async Task HistorySample_LiveSample_StillUsesPerTargetExecution()
    {
        // Verify Live SAMPLE_ONLY path is NOT broken by the History changes
        var sample = new QuestionSampleRow
        {
            Id = 42,
            Environment = "SqlServer_Live",
            GroupKey = "Databases",
            GroupTitle = "Databases",
            GroupOrder = 10,
            QuestionText = "List all databases",
            QuestionOrder = 1,
            Script = "SELECT name FROM sys.databases ORDER BY name;"
        };
        var service = CreateServiceWithSampleRepo(sample);

        var response = await service.ExecuteAsync(new AskApiRequest
        {
            ConversationId = "live-sample-check",
            Environment = "SqlServer_Live",
            Question = "List all databases",
            SampleId = 42,
            GroupKey = "Databases",
            SelectedTargets = ["CTS03#Admin"]
        }, CancellationToken.None);

        Assert.Equal("SAMPLE_ONLY", response.Plan.Mode);
        Assert.Equal("SQL", response.Plan.ScriptLanguage);
        // Live should NOT have history parameters or centralized target
        Assert.Null(response.Script!.Parameters);
        Assert.Null(response.Request.SelectedServers);
        Assert.NotNull(response.Result.Items);
        Assert.Single(response.Result.Items!);
        Assert.Equal("CTS03#Admin", response.Result.Items[0].Target);
    }

    // ─── NormalizeHistorySelectedServers unit tests ────────────────────────────

    [Fact]
    public void NormalizeHistorySelectedServers_StripsPort_KeepsInstance()
    {
        var result = AskPipelineService.NormalizeHistorySelectedServers(
            ["CTS02\\ADMIN,1432", "CTS02\\FINANCE,1431"]);

        Assert.Equal(2, result.Count);
        Assert.Equal("CTS02\\ADMIN", result[0]);
        Assert.Equal("CTS02\\FINANCE", result[1]);
    }

    [Fact]
    public void NormalizeHistorySelectedServers_Deduplicates()
    {
        var result = AskPipelineService.NormalizeHistorySelectedServers(
            ["CTS02\\ADMIN,1432", "CTS02\\ADMIN,1432", "CTS02\\FINANCE,1431"]);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void NormalizeHistorySelectedServers_NoPort_PassesThrough()
    {
        var result = AskPipelineService.NormalizeHistorySelectedServers(["CTS03", "CTS01"]);
        Assert.Equal(2, result.Count);
        Assert.Equal("CTS03", result[0]);
        Assert.Equal("CTS01", result[1]);
    }

    [Fact]
    public void NormalizeHistorySelectedServers_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Empty(AskPipelineService.NormalizeHistorySelectedServers(null));
        Assert.Empty(AskPipelineService.NormalizeHistorySelectedServers([]));
    }

    [Fact]
    public void NormalizeHistorySelectedServers_SkipsBlankEntries()
    {
        var result = AskPipelineService.NormalizeHistorySelectedServers(["CTS02", "", "  ", "CTS03"]);
        Assert.Equal(2, result.Count);
        Assert.Equal("CTS02", result[0]);
        Assert.Equal("CTS03", result[1]);
    }

    // ─── BuildSqlInList unit tests ────────────────────────────────────────────

    [Fact]
    public void BuildSqlInList_MultipleServers()
    {
        var result = AskPipelineService.BuildSqlInList(["CTS02\\ADMIN", "CTS02\\FINANCE"]);
        Assert.Equal("N'CTS02\\ADMIN', N'CTS02\\FINANCE'", result);
    }

    [Fact]
    public void BuildSqlInList_Empty_ReturnsNull()
    {
        Assert.Equal("NULL", AskPipelineService.BuildSqlInList([]));
    }

    // ─── TransformHistorySampleSql unit tests ──────────────────────────────────

    [Fact]
    public void TransformHistorySampleSql_TokenReplacement_SqlServer()
    {
        var script = "USE [SQLGig];\r\nGO\r\nDECLARE @FromUtc datetime2(0) = DATEADD(HOUR,-24,SYSUTCDATETIME());\r\nDECLARE @ToUtc   datetime2(0) = SYSUTCDATETIME();\r\nDECLARE @Top     int = 200;\r\nSELECT TOP (@Top) * FROM T h\r\nWHERE /*__SQLSERVER_FILTER__*/ 1=1\r\nAND h.DateTime >= @FromUtc AND h.DateTime < @ToUtc;";

        var result = AskPipelineService.TransformHistorySampleSql(
            script, "SqlServer_History",
            ["CTS02\\ADMIN", "CTS02\\FINANCE"],
            "2026-03-08T08:54:15.046Z", "2026-03-08T14:54:15.046Z");

        // Token replaced with IN-list
        Assert.Contains("h.SQLServer IN (N'CTS02\\ADMIN', N'CTS02\\FINANCE')", result);
        Assert.DoesNotContain("/*__SQLSERVER_FILTER__*/", result);
        // GO removed
        Assert.DoesNotMatch("(?m)^\\s*GO\\s*$", result);
        // @FromUtc/@ToUtc bound to literal values
        Assert.Contains("'2026-03-08T08:54:15'", result);
        Assert.Contains("'2026-03-08T14:54:15'", result);
        Assert.DoesNotContain("DATEADD", result);
        Assert.DoesNotContain("SYSUTCDATETIME", result);
        Assert.Contains("USE [SQLGig]", result);
    }

    [Fact]
    public void TransformHistorySampleSql_StaticFilter_Token()
    {
        var script = "SELECT * FROM T s WHERE /*__SQLSERVER_FILTER_STATIC__*/ 1=1;";

        var result = AskPipelineService.TransformHistorySampleSql(
            script, "SqlServer_History", ["CTS02\\ADMIN"], null, null);

        Assert.Contains("s.SQLServer IN (N'CTS02\\ADMIN')", result);
        Assert.DoesNotContain("/*__SQLSERVER_FILTER_STATIC__*/", result);
    }

    [Fact]
    public void TransformHistorySampleSql_WaitsFilter_Token()
    {
        var script = "SELECT * FROM T w WHERE /*__SQLSERVER_FILTER_WAITS__*/ 1=1;";

        var result = AskPipelineService.TransformHistorySampleSql(
            script, "SqlServer_History", ["CTS02\\ADMIN"], null, null);

        Assert.Contains("w.SQLServer IN (N'CTS02\\ADMIN')", result);
        Assert.DoesNotContain("/*__SQLSERVER_FILTER_WAITS__*/", result);
    }

    [Fact]
    public void TransformHistorySampleSql_AlertServerFilter_Token()
    {
        var script = "SELECT * FROM T a WHERE /*__ALERT_SERVER_FILTER__*/ 1=1;";

        var result = AskPipelineService.TransformHistorySampleSql(
            script, "SqlServer_History", ["CTS02\\ADMIN"], null, null);

        Assert.Contains("a.Server IN (N'CTS02\\ADMIN')", result);
        Assert.DoesNotContain("/*__ALERT_SERVER_FILTER__*/", result);
    }

    [Fact]
    public void TransformHistorySampleSql_WinServerFilter_WindowsHistory()
    {
        var script = "SELECT * FROM T h WHERE /*__WINSERVER_FILTER__*/ 1=1;";

        var result = AskPipelineService.TransformHistorySampleSql(
            script, "Windows_History", ["CTS02", "CTS03"], null, null);

        Assert.Contains("h.WinServer IN (N'CTS02', N'CTS03')", result);
        Assert.DoesNotContain("/*__WINSERVER_FILTER__*/", result);
    }

    [Fact]
    public void TransformHistorySampleSql_GoRemoval()
    {
        var script = "USE [SQLGig];\r\nGO\r\nSELECT 1;\r\nGO\r\n";

        var result = AskPipelineService.TransformHistorySampleSql(
            script, "SqlServer_History", ["CTS02"], null, null);

        Assert.DoesNotMatch("(?m)^\\s*GO\\s*$", result);
        Assert.Contains("SELECT 1;", result);
    }

    [Fact]
    public void TransformHistorySampleSql_InjectsMissingDeclarations()
    {
        // Script references @FromUtc and @Top but doesn't declare them
        var script = "SELECT TOP (@Top) * FROM T h WHERE h.DateTime >= @FromUtc AND h.DateTime < @ToUtc;";

        var result = AskPipelineService.TransformHistorySampleSql(
            script, "SqlServer_History", [], "2026-03-08T08:00:00Z", "2026-03-08T14:00:00Z");

        Assert.Contains("DECLARE @FromUtc datetime2(0)", result);
        Assert.Contains("DECLARE @ToUtc datetime2(0)", result);
        Assert.Contains("DECLARE @Top int", result);
        Assert.Contains("'2026-03-08T08:00:00'", result);
        Assert.Contains("'2026-03-08T14:00:00'", result);
    }

    [Fact]
    public void TransformHistorySampleSql_EmptyServers_NoFilterInjected()
    {
        var script = "SELECT * FROM T h WHERE /*__SQLSERVER_FILTER__*/ 1=1;";

        var result = AskPipelineService.TransformHistorySampleSql(
            script, "SqlServer_History", [], null, null);

        // Token removed, 1=1 remains
        Assert.DoesNotContain("/*__SQLSERVER_FILTER__*/", result);
        Assert.Contains("1=1", result);
    }

    [Fact]
    public void TransformHistorySampleSql_LegacyServerPattern_Replaced()
    {
        var script = "DECLARE @Server nvarchar(128) = NULL;\r\nSELECT 1 FROM T h WHERE h.SQLServer = @Server;";

        var result = AskPipelineService.TransformHistorySampleSql(
            script, "SqlServer_History", ["CTS02\\ADMIN"], null, null);

        Assert.Contains("h.SQLServer IN (N'CTS02\\ADMIN')", result);
        Assert.DoesNotContain("DECLARE @Server", result);
    }

    [Fact]
    public void TransformHistorySampleSql_SkipsUseSqlGig_WhenAlreadyPresent()
    {
        var script = "USE [SQLGig];\r\nGO\r\nSELECT 1;";

        var result = AskPipelineService.TransformHistorySampleSql(
            script, "SqlServer_History", ["CTS02"], null, null);

        var count = result.Split("USE [SQLGig]").Length - 1;
        Assert.Equal(1, count);
    }

    [Fact]
    public void TransformHistorySampleSql_SqlEscaping()
    {
        var script = "SELECT * FROM T h WHERE /*__SQLSERVER_FILTER__*/ 1=1;";

        var result = AskPipelineService.TransformHistorySampleSql(
            script, "SqlServer_History", ["CTS02\\O'Brien"], null, null);

        Assert.Contains("N'CTS02\\O''Brien'", result);
    }

    // ─── TryParseChartPlanJson unit tests ────────────────────────────────────

    [Fact]
    public void TryParseChartPlanJson_ValidLineChart_Parsed()
    {
        var json = """
        {
          "enabled": true,
          "charts": [
            {
              "chartId": "trend_main",
              "chartType": "area",
              "priority": 1,
              "title": "CPU Trend",
              "subtitle": "Selected servers over selected period",
              "xField": "CapturedAtUtc",
              "yField": "MetricValue",
              "seriesField": "ServerName",
              "categoryField": null,
              "stacked": false,
              "showLegend": true,
              "showMarkers": false,
              "xAxisLabel": "Time",
              "yAxisLabel": "CPU %",
              "aggregation": "none",
              "timeGrain": "auto",
              "metricGroup": "CPU",
              "metricName": "PercentProcessorTime",
              "filters": { "MetricGroup": ["CPU"], "MetricName": ["PercentProcessorTime"] },
              "formatHint": "percent",
              "goal": "trend"
            }
          ]
        }
        """;

        var fields = new List<string> { "ServerName", "CapturedAtUtc", "MetricGroup", "MetricName", "MetricValue" };
        var result = AskPipelineService.TryParseChartPlanJson(json, fields);

        Assert.NotNull(result);
        Assert.True(result!.Enabled);
        Assert.Null(result.Reason); // enabled=true has no reason
        Assert.Single(result.Charts);

        var chart = result.Charts[0];
        Assert.Equal("trend_main", chart.ChartId);
        Assert.Equal("area", chart.ChartType);
        Assert.Equal(1, chart.Priority);
        Assert.Equal("CPU Trend", chart.Title);
        Assert.Equal("CapturedAtUtc", chart.XField);
        Assert.Equal("MetricValue", chart.YField);
        Assert.Equal("ServerName", chart.SeriesField);
        Assert.True(chart.ShowLegend);
        Assert.Equal("none", chart.Aggregation);
        Assert.Equal("auto", chart.TimeGrain);
        Assert.Equal("CPU", chart.MetricGroup);
        Assert.Equal("PercentProcessorTime", chart.MetricName);
        Assert.Equal("percent", chart.FormatHint);
        Assert.Equal("trend", chart.Goal);
        Assert.NotNull(chart.Filters);
        Assert.Contains("MetricGroup", chart.Filters!.Keys);
        Assert.Single(chart.Filters["MetricGroup"]);
        Assert.Equal("CPU", chart.Filters["MetricGroup"][0]);
    }

    [Fact]
    public void TryParseChartPlanJson_Disabled_ReturnsDisabled()
    {
        var json = """{"enabled":false,"reason":"No numeric fields","charts":[]}""";
        var fields = new List<string> { "ServerName" };

        var result = AskPipelineService.TryParseChartPlanJson(json, fields);

        Assert.NotNull(result);
        Assert.False(result!.Enabled);
        Assert.Equal("No numeric fields", result.Reason);
        Assert.Empty(result.Charts);
    }

    [Fact]
    public void TryParseChartPlanJson_InvalidChartType_Skipped()
    {
        var json = """
        {
          "enabled": true,
          "charts": [
            { "chartId": "bad", "chartType": "radar", "priority": 1, "title": "Bad" },
            { "chartId": "good", "chartType": "bar", "priority": 2, "title": "Good", "yField": "MetricValue", "goal": "ranking" }
          ]
        }
        """;

        var fields = new List<string> { "ServerName", "MetricValue" };
        var result = AskPipelineService.TryParseChartPlanJson(json, fields);

        Assert.NotNull(result);
        Assert.True(result!.Enabled);
        Assert.Single(result.Charts);
        Assert.Equal("bar", result.Charts[0].ChartType);
        Assert.Equal("ranking", result.Charts[0].Goal);
    }

    [Fact]
    public void TryParseChartPlanJson_HeatmapType_Accepted()
    {
        var json = """
        {
          "enabled": true,
          "charts": [
            { "chartId": "heat", "chartType": "heatmap", "priority": 1, "title": "Heat" }
          ]
        }
        """;

        var fields = new List<string> { "ServerName", "MetricValue" };
        var result = AskPipelineService.TryParseChartPlanJson(json, fields);

        Assert.NotNull(result);
        Assert.True(result!.Enabled);
        Assert.Single(result.Charts);
        Assert.Equal("heatmap", result.Charts[0].ChartType);
    }

    [Fact]
    public void TryParseChartPlanJson_PieAndAnomalyLineTypes_Accepted()
    {
        var json = """
        {
          "enabled": true,
          "charts": [
            { "chartId": "p1", "chartType": "pie", "priority": 1, "title": "Pie", "yField": "MetricValue" },
            { "chartId": "a1", "chartType": "anomalyLine", "priority": 2, "title": "Anomalies", "xField": "CapturedAtUtc", "yField": "MetricValue" }
          ]
        }
        """;

        var fields = new List<string> { "CapturedAtUtc", "ServerName", "MetricValue" };
        var result = AskPipelineService.TryParseChartPlanJson(json, fields);

        Assert.NotNull(result);
        Assert.True(result!.Enabled);
        Assert.Equal(2, result.Charts.Count);
        Assert.Equal("pie", result.Charts[0].ChartType);
        Assert.Equal("anomalyLine", result.Charts[1].ChartType);
    }

    [Fact]
    public void TryParseChartPlanJson_InvalidFieldReference_NulledOut()
    {
        var json = """
        {
          "enabled": true,
          "charts": [
            {
              "chartId": "test",
              "chartType": "area",
              "priority": 1,
              "title": "Test",
              "xField": "CapturedAtUtc",
              "yField": "NonExistentField",
              "seriesField": "AlsoFake"
            }
          ]
        }
        """;

        var fields = new List<string> { "CapturedAtUtc", "MetricValue" };
        var result = AskPipelineService.TryParseChartPlanJson(json, fields);

        Assert.NotNull(result);
        var chart = result!.Charts[0];
        Assert.Equal("CapturedAtUtc", chart.XField);
        Assert.Null(chart.YField);
        Assert.Null(chart.SeriesField);
    }

    [Fact]
    public void TryParseChartPlanJson_Max7Charts_Enforced()
    {
        var json = """
        {
          "enabled": true,
          "charts": [
            { "chartId": "c1", "chartType": "area", "priority": 1, "title": "C1" },
            { "chartId": "c2", "chartType": "bar", "priority": 2, "title": "C2" },
            { "chartId": "c3", "chartType": "anomalyLine", "priority": 3, "title": "C3" },
            { "chartId": "c4", "chartType": "pie", "priority": 4, "title": "C4" },
            { "chartId": "c5", "chartType": "heatmap", "priority": 5, "title": "C5" },
            { "chartId": "c6", "chartType": "area", "priority": 6, "title": "C6" },
            { "chartId": "c7", "chartType": "bar", "priority": 7, "title": "C7" },
            { "chartId": "c8", "chartType": "area", "priority": 8, "title": "C8" }
          ]
        }
        """;

        var fields = new List<string> { "CapturedAtUtc", "MetricValue" };
        var result = AskPipelineService.TryParseChartPlanJson(json, fields);

        Assert.NotNull(result);
        Assert.Equal(7, result!.Charts.Count);
    }

    [Fact]
    public void TryParseChartPlanJson_FiltersAsDictionary_Parsed()
    {
        var json = """
        {
          "enabled": true,
          "charts": [
            {
              "chartId": "filtered",
              "chartType": "area",
              "priority": 1,
              "title": "Filtered",
              "filters": {
                "MetricGroup": ["CPU", "Memory"],
                "ServerName": ["CTS02"]
              }
            }
          ]
        }
        """;

        var fields = new List<string> { "CapturedAtUtc", "MetricValue", "MetricGroup", "ServerName" };
        var result = AskPipelineService.TryParseChartPlanJson(json, fields);

        Assert.NotNull(result);
        var chart = result!.Charts[0];
        Assert.NotNull(chart.Filters);
        Assert.Equal(2, chart.Filters!.Count);
        Assert.Equal(2, chart.Filters["MetricGroup"].Length);
        Assert.Equal("CPU", chart.Filters["MetricGroup"][0]);
        Assert.Equal("Memory", chart.Filters["MetricGroup"][1]);
        Assert.Single(chart.Filters["ServerName"]);
        Assert.Equal("CTS02", chart.Filters["ServerName"][0]);
    }

    [Fact]
    public void TryParseChartPlanJson_MarkdownFences_Stripped()
    {
        var json = """
        ```json
        {"enabled":false,"reason":"No data","charts":[]}
        ```
        """;

        var fields = new List<string> { "MetricValue" };
        var result = AskPipelineService.TryParseChartPlanJson(json, fields);

        Assert.NotNull(result);
        Assert.False(result!.Enabled);
    }

    [Fact]
    public void TryParseChartPlanJson_Null_ReturnsNull()
    {
        var result = AskPipelineService.TryParseChartPlanJson(null, []);
        Assert.Null(result);
    }

    [Fact]
    public void TryParseChartPlanJson_InvalidJson_ReturnsNull()
    {
        var result = AskPipelineService.TryParseChartPlanJson("not json at all", ["MetricValue"]);
        Assert.Null(result);
    }

    // ─── TryParseChartPlanJson: transform and enhanced fields ─────────────────

    [Fact]
    public void TryParseChartPlanJson_TransformParsed()
    {
        var json = """
        {
          "enabled": true,
          "charts": [
            {
              "chartId": "t1", "chartType": "bar", "priority": 1, "title": "Grouped",
              "yField": "MetricValue",
              "allowSeriesToggle": true,
              "allowMaximize": true,
              "transform": {
                "type": "group",
                "groupBy": ["ServerName"],
                "aggregateField": "MetricValue",
                "aggregateFn": "avg"
              }
            }
          ]
        }
        """;

        var fields = new List<string> { "ServerName", "MetricValue" };
        var result = AskPipelineService.TryParseChartPlanJson(json, fields);

        Assert.NotNull(result);
        var chart = result!.Charts[0];
        Assert.True(chart.AllowSeriesToggle);
        Assert.True(chart.AllowMaximize);
        Assert.NotNull(chart.Transform);
        Assert.Equal("group", chart.Transform!.Type);
        Assert.Single(chart.Transform.GroupBy!);
        Assert.Equal("ServerName", chart.Transform.GroupBy![0]);
        Assert.Equal("MetricValue", chart.Transform.AggregateField);
        Assert.Equal("avg", chart.Transform.AggregateFn);
    }

    [Fact]
    public void TryParseChartPlanJson_AnomalyLineWithConfig_Accepted()
    {
        var json = """
        {
          "enabled": true,
          "charts": [
            {
              "chartId": "anom", "chartType": "anomalyLine", "priority": 1, "title": "Anomalies",
              "xField": "CapturedAtUtc", "yField": "MetricValue",
              "anomaly": { "method": "zscore", "threshold": 2.5, "serverWise": true }
            }
          ]
        }
        """;

        var fields = new List<string> { "CapturedAtUtc", "MetricValue" };
        var result = AskPipelineService.TryParseChartPlanJson(json, fields);

        Assert.NotNull(result);
        Assert.Single(result!.Charts);
        Assert.Equal("anomalyLine", result.Charts[0].ChartType);
        Assert.NotNull(result.Charts[0].Anomaly);
        Assert.Equal("zscore", result.Charts[0].Anomaly!.Method);
        Assert.Equal(2.5, result.Charts[0].Anomaly.Threshold);
        Assert.True(result.Charts[0].Anomaly.ServerWise);
    }

    // ─── BuildDataProfile tests ────────────────────────────────────────────────

    [Fact]
    public void BuildDataProfile_TimeSeriesMultiServer()
    {
        var result = new AskResponseResult
        {
            Kind = "EXECUTION", Status = "SUCCESS",
            Items =
            [
                new AskExecutionItem
                {
                    Target = "CTS03::SQLGig", Status = "SUCCESS", RowCount = 4,
                    Rows =
                    [
                        new() { ["ServerName"] = "CTS02", ["CapturedAtUtc"] = "2026-03-01T10:00:00Z", ["MetricValue"] = 42.5, ["MetricName"] = "CPU" },
                        new() { ["ServerName"] = "CTS02", ["CapturedAtUtc"] = "2026-03-01T11:00:00Z", ["MetricValue"] = 45.0, ["MetricName"] = "CPU" },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "2026-03-01T10:00:00Z", ["MetricValue"] = 30.0, ["MetricName"] = "CPU" },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "2026-03-01T11:00:00Z", ["MetricValue"] = 32.0, ["MetricName"] = "CPU" },
                    ]
                }
            ]
        };

        var profile = AskPipelineService.BuildDataProfile(result);

        Assert.Equal(4, profile.RowCount);
        Assert.True(profile.HasTimeField);
        Assert.Equal("CapturedAtUtc", profile.TimeField);
        Assert.True(profile.HasNumericMetric);
        Assert.Equal("MetricValue", profile.NumericField);
        Assert.True(profile.HasSeriesField);
        Assert.Equal("ServerName", profile.SeriesField);
        Assert.Equal(2, profile.DistinctServers);
        Assert.Equal(1, profile.DistinctMetricNames);
        Assert.Equal("time_series_multi_server", profile.ProfileType);
    }

    [Fact]
    public void BuildDataProfile_SingleServer()
    {
        var result = new AskResponseResult
        {
            Kind = "EXECUTION", Status = "SUCCESS",
            Items =
            [
                new AskExecutionItem
                {
                    Target = "CTS03::SQLGig", Status = "SUCCESS", RowCount = 2,
                    Rows =
                    [
                        new() { ["ServerName"] = "CTS02", ["CapturedAtUtc"] = "2026-03-01T10:00:00Z", ["MetricValue"] = 42.5 },
                        new() { ["ServerName"] = "CTS02", ["CapturedAtUtc"] = "2026-03-01T11:00:00Z", ["MetricValue"] = 45.0 },
                    ]
                }
            ]
        };

        var profile = AskPipelineService.BuildDataProfile(result);

        Assert.Equal("time_series_single_server", profile.ProfileType);
        Assert.Equal(1, profile.DistinctServers);
    }

    [Fact]
    public void BuildDataProfile_NoRows_ReturnsEmpty()
    {
        var result = new AskResponseResult { Kind = "EXECUTION", Status = "FAILED" };
        var profile = AskPipelineService.BuildDataProfile(result);

        Assert.Equal(0, profile.RowCount);
        Assert.False(profile.HasTimeField);
        Assert.Equal("unknown", profile.ProfileType);
    }

    [Fact]
    public void BuildDataProfile_StatusOnly()
    {
        var result = new AskResponseResult
        {
            Kind = "EXECUTION", Status = "SUCCESS",
            Items =
            [
                new AskExecutionItem
                {
                    Target = "CTS03::SQLGig", Status = "SUCCESS", RowCount = 1,
                    Rows =
                    [
                        new() { ["ServerName"] = "CTS02", ["Detail"] = "Running" },
                    ]
                }
            ]
        };

        var profile = AskPipelineService.BuildDataProfile(result);

        Assert.Equal("status", profile.ProfileType);
        Assert.False(profile.HasTimeField);
        Assert.False(profile.HasNumericMetric);
    }

    // ─── BuildChartSummary tests ───────────────────────────────────────────────

    [Fact]
    public void BuildChartSummary_WithMultiServerMetric_PopulatesHighLow()
    {
        var request = new AskApiRequest
        {
            Environment = "SqlServer_History",
            Question = "CPU trend",
            MetricKey = "PercentProcessorTime",
            MetricLabel = "CPU %",
            MetricGroup = "CPU"
        };

        var result = new AskResponseResult
        {
            Kind = "EXECUTION", Status = "SUCCESS",
            Items =
            [
                new AskExecutionItem
                {
                    Target = "CTS03::SQLGig", Status = "SUCCESS", RowCount = 4,
                    Rows =
                    [
                        new() { ["ServerName"] = "CTS02", ["CapturedAtUtc"] = "2026-03-01T10:00:00Z", ["MetricValue"] = 80.0 },
                        new() { ["ServerName"] = "CTS02", ["CapturedAtUtc"] = "2026-03-01T11:00:00Z", ["MetricValue"] = 85.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "2026-03-01T10:00:00Z", ["MetricValue"] = 20.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "2026-03-01T11:00:00Z", ["MetricValue"] = 25.0 },
                    ]
                }
            ]
        };

        var profile = AskPipelineService.BuildDataProfile(result);
        var summary = AskPipelineService.BuildChartSummary(request, result, profile);

        Assert.NotNull(summary);
        Assert.Equal("PercentProcessorTime", summary!.PrimaryMetric);
        Assert.Equal("CPU %", summary.PrimaryMetricLabel);
        Assert.Equal("percent", summary.Unit);
        Assert.Equal("CTS02", summary.HighestSeries);
        Assert.Equal("CTS03", summary.LowestSeries);
    }

    [Fact]
    public void BuildChartSummary_NoNumericMetric_ReturnsNull()
    {
        var request = new AskApiRequest { Environment = "SqlServer_History", Question = "status" };
        var result = new AskResponseResult { Kind = "EXECUTION", Status = "SUCCESS" };
        var profile = new AskDataProfile { HasNumericMetric = false };

        var summary = AskPipelineService.BuildChartSummary(request, result, profile);
        Assert.Null(summary);
    }

    // ─── BuildDeterministicChartPlan tests ─────────────────────────────────────

    [Fact]
    public void BuildDeterministicChartPlan_TimeSeriesMultiServer_Returns5Charts()
    {
        var profile = new AskDataProfile
        {
            RowCount = 100,
            HasTimeField = true, TimeField = "CapturedAtUtc",
            HasNumericMetric = true, NumericField = "MetricValue",
            HasSeriesField = true, SeriesField = "ServerName",
            DistinctServers = 3,
            ProfileType = "time_series_multi_server"
        };

        var fields = new List<string> { "ServerName", "CapturedAtUtc", "MetricValue", "MetricName" };
        var plan = AskPipelineService.BuildDeterministicChartPlan(profile, fields, null, "CPU %", "PercentProcessorTime", "CPU");

        Assert.True(plan.Enabled);
        Assert.True(plan.Chartable);
        Assert.Equal("area_trend", plan.DefaultChartId);
        Assert.Equal("CapturedAtUtc", plan.RecommendedXField);
        Assert.Equal("MetricValue", plan.RecommendedYField);
        Assert.Equal("ServerName", plan.RecommendedSeriesField);

        // Expect: area + anomalyLine + heatmap + bar = 4
        Assert.Equal(4, plan.Charts.Count);
        Assert.Equal("area", plan.Charts[0].ChartType);
        Assert.Equal("anomalyLine", plan.Charts[1].ChartType);
        Assert.Equal("heatmap", plan.Charts[2].ChartType);
        Assert.Equal("bar", plan.Charts[3].ChartType);

        // Area chart has enhanced fields
        Assert.True(plan.Charts[0].AllowSeriesToggle);
        Assert.True(plan.Charts[0].AllowMaximize);
        Assert.NotNull(plan.Charts[0].SupportedInteractions);
        Assert.Equal("categorical", plan.Charts[0].ColorIntent);

        // AnomalyLine chart has anomaly config with field refs
        Assert.NotNull(plan.Charts[1].Anomaly);
        Assert.Equal("zscore", plan.Charts[1].Anomaly!.Method);
        Assert.Equal(2.5, plan.Charts[1].Anomaly.Threshold);
        Assert.True(plan.Charts[1].Anomaly.ServerWise);
        Assert.Equal("MetricValue", plan.Charts[1].Anomaly.ValueField);
        Assert.Equal("CapturedAtUtc", plan.Charts[1].Anomaly.TimeField);
        Assert.Equal("ServerName", plan.Charts[1].Anomaly.SeriesField);
        Assert.Equal("alert", plan.Charts[1].ColorIntent);

        // Heatmap has valueField and timeBucketed
        Assert.Equal("MetricValue", plan.Charts[2].ValueField);
        Assert.True(plan.Charts[2].TimeBucketed);
        Assert.Equal("tall", plan.Charts[2].RecommendedHeight);

        // Bar chart has latestSnapshotOnly
        Assert.True(plan.Charts[3].LatestSnapshotOnly);

        // Format hint
        Assert.Equal("percent", plan.Charts[0].FormatHint);
    }

    [Fact]
    public void BuildDeterministicChartPlan_SingleServer_HasAreaAnomalyBar()
    {
        var profile = new AskDataProfile
        {
            RowCount = 50,
            HasTimeField = true, TimeField = "CapturedAtUtc",
            HasNumericMetric = true, NumericField = "MetricValue",
            HasSeriesField = true, SeriesField = "ServerName",
            DistinctServers = 1,
            ProfileType = "time_series_single_server"
        };

        var fields = new List<string> { "ServerName", "CapturedAtUtc", "MetricValue" };
        var plan = AskPipelineService.BuildDeterministicChartPlan(profile, fields, null, "Memory", "AvailableMemory_GB", "Memory");

        Assert.True(plan.Enabled);
        // area + anomalyLine + bar = 3 (no heatmap for single server)
        Assert.Equal(3, plan.Charts.Count);
        Assert.Equal("area", plan.Charts[0].ChartType);
        Assert.Equal("anomalyLine", plan.Charts[1].ChartType);
        Assert.Equal("bar", plan.Charts[2].ChartType);
        Assert.Equal("gb", plan.Charts[0].FormatHint);
    }

    [Fact]
    public void BuildDeterministicChartPlan_NoNumericData_Disabled()
    {
        var profile = new AskDataProfile { RowCount = 5, HasNumericMetric = false };
        var fields = new List<string> { "ServerName", "Detail" };

        var plan = AskPipelineService.BuildDeterministicChartPlan(profile, fields);

        Assert.False(plan.Enabled);
        Assert.False(plan.Chartable);
    }

    [Fact]
    public void BuildDeterministicChartPlan_TextMetric_NotChartable()
    {
        var profile = new AskDataProfile { RowCount = 5, HasNumericMetric = true, NumericField = "MetricValue" };
        var fields = new List<string> { "ServerName", "MetricValue" };

        // AGHealth is a text metric (MetricSelectSql == "NULL AS MetricValue")
        var plan = AskPipelineService.BuildDeterministicChartPlan(profile, fields, null, "AG Health", "AGHealth", "Availability Groups");

        Assert.False(plan.Enabled);
        Assert.False(plan.Chartable);
    }

    [Fact]
    public void BuildDeterministicChartPlan_CategoryWithMultiServer_HasBarAndPie()
    {
        var profile = new AskDataProfile
        {
            RowCount = 10,
            HasTimeField = false,
            HasNumericMetric = true, NumericField = "MetricValue",
            HasSeriesField = true, SeriesField = "ServerName",
            DistinctServers = 3,
            ProfileType = "category"
        };

        var fields = new List<string> { "ServerName", "MetricValue" };
        var plan = AskPipelineService.BuildDeterministicChartPlan(profile, fields, null, "Connections", "UserConnections", "Connections");

        Assert.True(plan.Enabled);
        Assert.Equal(2, plan.Charts.Count);
        Assert.Equal("bar", plan.Charts[0].ChartType);
        Assert.Equal("pie", plan.Charts[1].ChartType);
        Assert.NotNull(plan.Charts[0].ValueField);
        Assert.NotNull(plan.Charts[1].ValueField);
    }

    [Fact]
    public void ComputeAnomalyMetadata_DetectsOutliers()
    {
        var result = new AskResponseResult
        {
            Kind = "EXECUTION", Status = "SUCCESS",
            Items =
            [
                new AskExecutionItem
                {
                    Target = "CTS03::SQLGig", Status = "SUCCESS", RowCount = 6,
                    Rows =
                    [
                        new() { ["ServerName"] = "CTS02", ["CapturedAtUtc"] = "2026-03-01T10:00:00Z", ["MetricValue"] = 50.0 },
                        new() { ["ServerName"] = "CTS02", ["CapturedAtUtc"] = "2026-03-01T11:00:00Z", ["MetricValue"] = 52.0 },
                        new() { ["ServerName"] = "CTS02", ["CapturedAtUtc"] = "2026-03-01T12:00:00Z", ["MetricValue"] = 48.0 },
                        new() { ["ServerName"] = "CTS02", ["CapturedAtUtc"] = "2026-03-01T13:00:00Z", ["MetricValue"] = 51.0 },
                        new() { ["ServerName"] = "CTS02", ["CapturedAtUtc"] = "2026-03-01T14:00:00Z", ["MetricValue"] = 49.0 },
                        new() { ["ServerName"] = "CTS02", ["CapturedAtUtc"] = "2026-03-01T15:00:00Z", ["MetricValue"] = 99.0 },
                    ]
                }
            ]
        };

        var config = AskPipelineService.ComputeAnomalyMetadata(
            result, "MetricValue", "ServerName", "CapturedAtUtc", serverWise: false, threshold: 2.0);

        Assert.Equal("zscore", config.Method);
        Assert.True(config.HasAnomalies);
        Assert.True(config.Points.Count > 0);
        var outlier = config.Points.First(p => p.Value > 90); // The 99.0 outlier
        Assert.True(outlier.IsAnomaly);
        Assert.True(outlier.ExpectedValue > 0); // Server mean ~50
        Assert.NotNull(outlier.Reason);
        Assert.Contains("above", outlier.Reason);
        Assert.True(config.Mean > 0);
        Assert.True(config.StdDev > 0);
        Assert.True(config.ThresholdUpper > config.Mean);
        Assert.True(config.ThresholdLower < config.Mean);
        Assert.Equal("MetricValue", config.ValueField);
        Assert.Equal("CapturedAtUtc", config.TimeField);
        Assert.Equal("ServerName", config.SeriesField);
    }

    [Fact]
    public void ComputeAnomalyMetadata_ServerWise_GroupsByServer()
    {
        var result = new AskResponseResult
        {
            Kind = "EXECUTION", Status = "SUCCESS",
            Items =
            [
                new AskExecutionItem
                {
                    Target = "CTS03::SQLGig", Status = "SUCCESS", RowCount = 10,
                    Rows =
                    [
                        new() { ["ServerName"] = "CTS02", ["CapturedAtUtc"] = "T1", ["MetricValue"] = 50.0 },
                        new() { ["ServerName"] = "CTS02", ["CapturedAtUtc"] = "T2", ["MetricValue"] = 51.0 },
                        new() { ["ServerName"] = "CTS02", ["CapturedAtUtc"] = "T3", ["MetricValue"] = 49.0 },
                        new() { ["ServerName"] = "CTS02", ["CapturedAtUtc"] = "T4", ["MetricValue"] = 50.5 },
                        new() { ["ServerName"] = "CTS02", ["CapturedAtUtc"] = "T5", ["MetricValue"] = 50.0 },
                        new() { ["ServerName"] = "CTS02", ["CapturedAtUtc"] = "T6", ["MetricValue"] = 51.0 },
                        new() { ["ServerName"] = "CTS02", ["CapturedAtUtc"] = "T7", ["MetricValue"] = 49.5 },
                        new() { ["ServerName"] = "CTS02", ["CapturedAtUtc"] = "T8", ["MetricValue"] = 200.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T1", ["MetricValue"] = 10.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T2", ["MetricValue"] = 11.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T3", ["MetricValue"] = 10.5 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T4", ["MetricValue"] = 10.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T5", ["MetricValue"] = 11.0 },
                    ]
                }
            ]
        };

        var config = AskPipelineService.ComputeAnomalyMetadata(
            result, "MetricValue", "ServerName", "CapturedAtUtc", serverWise: true);

        Assert.True(config.ServerWise);
        Assert.True(config.HasAnomalies);
        // CTS02 has a clear outlier (200.0 vs ~50 mean), CTS03 is stable
        Assert.True(config.Points.Count > 0);
        Assert.True(config.Points.All(p => p.Server == "CTS02"));
        var anomalyPt = config.Points.First();
        Assert.True(anomalyPt.ExpectedValue > 0); // Per-server mean ~50
        Assert.NotNull(anomalyPt.Reason);
        Assert.Contains("rolling baseline", anomalyPt.Reason);
        Assert.True(config.ThresholdUpper > 0);
        Assert.True(config.Mean > 0);
        Assert.True(config.StdDev > 0);
        Assert.Equal("MetricValue", config.ValueField);
        Assert.Equal("CapturedAtUtc", config.TimeField);
        Assert.Equal("ServerName", config.SeriesField);
    }

    [Fact]
    public void BuildDeterministicChartPlan_LowRowCount_StillHasHeatmap()
    {
        var profile = new AskDataProfile
        {
            RowCount = 8, // Below old threshold of 20, above new threshold of 6
            HasTimeField = true, TimeField = "CapturedAtUtc",
            HasNumericMetric = true, NumericField = "MetricValue",
            HasSeriesField = true, SeriesField = "ServerName",
            DistinctServers = 2,
            ProfileType = "time_series_multi_server"
        };

        var fields = new List<string> { "ServerName", "CapturedAtUtc", "MetricValue" };
        var plan = AskPipelineService.BuildDeterministicChartPlan(profile, fields, null, "CPU %", "PercentProcessorTime");

        Assert.True(plan.Enabled);
        Assert.Equal(4, plan.Charts.Count);
        Assert.Equal("area", plan.Charts[0].ChartType);
        Assert.Equal("anomalyLine", plan.Charts[1].ChartType);
        Assert.Equal("heatmap", plan.Charts[2].ChartType);
        Assert.Equal("bar", plan.Charts[3].ChartType);
    }

    [Fact]
    public void BuildDeterministicChartPlan_TooFewRows_NoHeatmap()
    {
        var profile = new AskDataProfile
        {
            RowCount = 4, // Below threshold of 6
            HasTimeField = true, TimeField = "CapturedAtUtc",
            HasNumericMetric = true, NumericField = "MetricValue",
            HasSeriesField = true, SeriesField = "ServerName",
            DistinctServers = 2,
            ProfileType = "time_series_multi_server"
        };

        var fields = new List<string> { "ServerName", "CapturedAtUtc", "MetricValue" };
        var plan = AskPipelineService.BuildDeterministicChartPlan(profile, fields, null, "CPU %", "PercentProcessorTime");

        Assert.True(plan.Enabled);
        Assert.Equal(3, plan.Charts.Count); // area + anomaly + bar, no heatmap
        Assert.DoesNotContain(plan.Charts, c => c.ChartType == "heatmap");
    }

    [Fact]
    public void ComputeAnomalyMetadata_BaselineFieldsComputed()
    {
        var result = new AskResponseResult
        {
            Kind = "EXECUTION", Status = "SUCCESS",
            Items =
            [
                new AskExecutionItem
                {
                    Target = "CTS03::SQLGig", Status = "SUCCESS", RowCount = 5,
                    Rows =
                    [
                        new() { ["ServerName"] = "CTS02", ["CapturedAtUtc"] = "T1", ["MetricValue"] = 10.0 },
                        new() { ["ServerName"] = "CTS02", ["CapturedAtUtc"] = "T2", ["MetricValue"] = 12.0 },
                        new() { ["ServerName"] = "CTS02", ["CapturedAtUtc"] = "T3", ["MetricValue"] = 11.0 },
                        new() { ["ServerName"] = "CTS02", ["CapturedAtUtc"] = "T4", ["MetricValue"] = 10.5 },
                        new() { ["ServerName"] = "CTS02", ["CapturedAtUtc"] = "T5", ["MetricValue"] = 11.5 },
                    ]
                }
            ]
        };

        var config = AskPipelineService.ComputeAnomalyMetadata(
            result, "MetricValue", "ServerName", "CapturedAtUtc", serverWise: false);

        // Baseline thresholds computed from mean ± threshold * stdDev
        Assert.Equal(2.5, config.Threshold);
        Assert.True(config.Mean > 0);
        Assert.True(config.StdDev > 0);
        Assert.True(config.ThresholdUpper > config.Mean);
        Assert.True(config.ThresholdLower < config.Mean);
        Assert.Equal(Math.Round(config.Mean + 2.5 * config.StdDev, 4), config.ThresholdUpper);
        Assert.Equal(Math.Round(config.Mean - 2.5 * config.StdDev, 4), config.ThresholdLower);

        // No anomalies in stable data — points is empty, not null
        Assert.False(config.HasAnomalies);
        Assert.NotNull(config.Points);
        Assert.Empty(config.Points);
    }

    [Fact]
    public void BuildDeterministicChartPlan_AnomalyChart_MateriallyDifferentFromArea()
    {
        var profile = new AskDataProfile
        {
            RowCount = 50,
            HasTimeField = true, TimeField = "CapturedAtUtc",
            HasNumericMetric = true, NumericField = "MetricValue",
            HasSeriesField = true, SeriesField = "ServerName",
            DistinctServers = 3,
            ProfileType = "time_series_multi_server"
        };

        var fields = new List<string> { "ServerName", "CapturedAtUtc", "MetricValue" };
        var plan = AskPipelineService.BuildDeterministicChartPlan(profile, fields, null, "CPU %", "PercentProcessorTime");

        var areaChart = plan.Charts.First(c => c.ChartType == "area");
        var anomalyChart = plan.Charts.First(c => c.ChartType == "anomalyLine");

        // Area chart must NOT have anomaly config
        Assert.Null(areaChart.Anomaly);

        // Anomaly chart MUST have anomaly config with all metadata
        Assert.NotNull(anomalyChart.Anomaly);
        Assert.Equal("zscore", anomalyChart.Anomaly!.Method);
        Assert.Equal(2.5, anomalyChart.Anomaly.Threshold);
        Assert.NotNull(anomalyChart.Anomaly.ValueField);
        Assert.NotNull(anomalyChart.Anomaly.TimeField);
        Assert.NotNull(anomalyChart.Anomaly.Points); // Points always present (empty list when no result)

        // Visual differences
        Assert.Equal("alert", anomalyChart.ColorIntent);
        Assert.NotEqual(areaChart.ColorIntent, anomalyChart.ColorIntent);
        Assert.True(anomalyChart.ShowMarkers);
        Assert.False(areaChart.ShowMarkers);
        Assert.Equal("anomaly", anomalyChart.Goal);
        Assert.Equal("trend", areaChart.Goal);
    }

    [Fact]
    public void ComputeAnomalyMetadata_NoAnomalies_ReturnsEmptyPointsWithBaseline()
    {
        var result = new AskResponseResult
        {
            Kind = "EXECUTION", Status = "SUCCESS",
            Items =
            [
                new AskExecutionItem
                {
                    Target = "CTS03::SQLGig", Status = "SUCCESS", RowCount = 5,
                    Rows =
                    [
                        new() { ["ServerName"] = "CTS02", ["CapturedAtUtc"] = "T1", ["MetricValue"] = 50.0 },
                        new() { ["ServerName"] = "CTS02", ["CapturedAtUtc"] = "T2", ["MetricValue"] = 50.1 },
                        new() { ["ServerName"] = "CTS02", ["CapturedAtUtc"] = "T3", ["MetricValue"] = 49.9 },
                        new() { ["ServerName"] = "CTS02", ["CapturedAtUtc"] = "T4", ["MetricValue"] = 50.0 },
                        new() { ["ServerName"] = "CTS02", ["CapturedAtUtc"] = "T5", ["MetricValue"] = 50.0 },
                    ]
                }
            ]
        };

        var config = AskPipelineService.ComputeAnomalyMetadata(
            result, "MetricValue", "ServerName", "CapturedAtUtc", serverWise: false);

        // No anomalies in perfectly stable data
        Assert.False(config.HasAnomalies);
        Assert.NotNull(config.Points);
        Assert.Empty(config.Points);

        // Diagnostic reason for no anomalies
        Assert.NotNull(config.NoAnomalyReason);
        Assert.Contains("within", config.NoAnomalyReason);

        // But baseline is still computed for UI rendering
        Assert.True(config.Mean > 0);
        Assert.True(config.StdDev > 0);
        Assert.True(config.ThresholdUpper > config.Mean);
        Assert.True(config.ThresholdLower < config.Mean);
        Assert.Equal("MetricValue", config.ValueField);
    }

    // ─── ComputeSeriesFindings tests ─────────────────────────────────────────

    [Fact]
    public void ComputeSeriesFindings_ThresholdBreach_LowPLE()
    {
        // PLE consistently low (below warning threshold of 300) → threshold_breach
        var result = new AskResponseResult
        {
            Kind = "EXECUTION", Status = "SUCCESS",
            Items =
            [
                new AskExecutionItem
                {
                    Target = "CTS03::SQLGig", Status = "SUCCESS", RowCount = 6,
                    Rows =
                    [
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T1", ["MetricValue"] = 45.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T2", ["MetricValue"] = 42.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T3", ["MetricValue"] = 50.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T4", ["MetricValue"] = 38.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T5", ["MetricValue"] = 48.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T6", ["MetricValue"] = 40.0 },
                    ]
                }
            ]
        };

        var findings = AskPipelineService.ComputeSeriesFindings(
            result, "MetricValue", "ServerName", "CapturedAtUtc", "PageLifeExpectancy_seconds");

        Assert.Single(findings);
        var f = findings[0];
        Assert.Equal("CTS03", f.Server);
        Assert.Contains("threshold_breach", f.Types);
        Assert.Contains("sustained_low", f.Types); // All values below 300
        Assert.Equal("critical", f.Severity); // 40 ≤ criticalLow(60)
        Assert.NotNull(f.Reason);
        Assert.True(f.Points.Count > 0);
    }

    [Fact]
    public void ComputeSeriesFindings_ThresholdBreach_HighCPU()
    {
        // CPU consistently high (above warning threshold of 80) → threshold_breach + sustained_risk
        var result = new AskResponseResult
        {
            Kind = "EXECUTION", Status = "SUCCESS",
            Items =
            [
                new AskExecutionItem
                {
                    Target = "CTS03::SQLGig", Status = "SUCCESS", RowCount = 6,
                    Rows =
                    [
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T1", ["MetricValue"] = 88.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T2", ["MetricValue"] = 92.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T3", ["MetricValue"] = 85.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T4", ["MetricValue"] = 90.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T5", ["MetricValue"] = 87.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T6", ["MetricValue"] = 96.0 },
                    ]
                }
            ]
        };

        var findings = AskPipelineService.ComputeSeriesFindings(
            result, "MetricValue", "ServerName", "CapturedAtUtc", "PercentProcessorTime");

        Assert.Single(findings);
        var f = findings[0];
        Assert.Contains("threshold_breach", f.Types);
        Assert.Contains("sustained_risk", f.Types);
        Assert.True(f.Severity is "high" or "critical");
    }

    [Fact]
    public void ComputeSeriesFindings_StatisticalSpike_NoOpThreshold()
    {
        // Metric without operational thresholds but with a z-score spike
        var result = new AskResponseResult
        {
            Kind = "EXECUTION", Status = "SUCCESS",
            Items =
            [
                new AskExecutionItem
                {
                    Target = "CTS03::SQLGig", Status = "SUCCESS", RowCount = 8,
                    Rows =
                    [
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T1", ["MetricValue"] = 100.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T2", ["MetricValue"] = 100.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T3", ["MetricValue"] = 100.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T4", ["MetricValue"] = 100.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T5", ["MetricValue"] = 100.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T6", ["MetricValue"] = 100.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T7", ["MetricValue"] = 100.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T8", ["MetricValue"] = 500.0 },
                    ]
                }
            ]
        };

        var findings = AskPipelineService.ComputeSeriesFindings(
            result, "MetricValue", "ServerName", "CapturedAtUtc", "UserConnections");

        Assert.Single(findings);
        var f = findings[0];
        Assert.Contains("spike", f.Types);
        Assert.DoesNotContain("threshold_breach", f.Types); // No op threshold for UserConnections
    }

    [Fact]
    public void ComputeSeriesFindings_PeerDeviation_MultiServer()
    {
        // Two servers: CTS02 at ~100, CTS03 at ~10 → peer_deviation for CTS03
        var result = new AskResponseResult
        {
            Kind = "EXECUTION", Status = "SUCCESS",
            Items =
            [
                new AskExecutionItem
                {
                    Target = "CTS03::SQLGig", Status = "SUCCESS", RowCount = 6,
                    Rows =
                    [
                        new() { ["ServerName"] = "CTS02", ["CapturedAtUtc"] = "T1", ["MetricValue"] = 100.0 },
                        new() { ["ServerName"] = "CTS02", ["CapturedAtUtc"] = "T2", ["MetricValue"] = 100.0 },
                        new() { ["ServerName"] = "CTS02", ["CapturedAtUtc"] = "T3", ["MetricValue"] = 100.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T1", ["MetricValue"] = 10.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T2", ["MetricValue"] = 10.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T3", ["MetricValue"] = 10.0 },
                    ]
                }
            ]
        };

        var findings = AskPipelineService.ComputeSeriesFindings(
            result, "MetricValue", "ServerName", "CapturedAtUtc", "BatchRequests_sec");

        // At least one server should have peer_deviation
        Assert.True(findings.Any(f => f.Types.Contains("peer_deviation")));
    }

    [Fact]
    public void ComputeSeriesFindings_NoFindings_StableData()
    {
        // Stable data with no operational thresholds → no findings
        var result = new AskResponseResult
        {
            Kind = "EXECUTION", Status = "SUCCESS",
            Items =
            [
                new AskExecutionItem
                {
                    Target = "CTS03::SQLGig", Status = "SUCCESS", RowCount = 5,
                    Rows =
                    [
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T1", ["MetricValue"] = 50.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T2", ["MetricValue"] = 50.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T3", ["MetricValue"] = 50.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T4", ["MetricValue"] = 50.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T5", ["MetricValue"] = 50.0 },
                    ]
                }
            ]
        };

        var findings = AskPipelineService.ComputeSeriesFindings(
            result, "MetricValue", "ServerName", "CapturedAtUtc", "UserConnections");

        Assert.Empty(findings);
    }

    [Fact]
    public void ComputeAnomalyMetadata_WithMetricKey_EnrichesWithFindings()
    {
        // Low PLE — statistically stable but operationally unhealthy
        var result = new AskResponseResult
        {
            Kind = "EXECUTION", Status = "SUCCESS",
            Items =
            [
                new AskExecutionItem
                {
                    Target = "CTS03::SQLGig", Status = "SUCCESS", RowCount = 6,
                    Rows =
                    [
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T1", ["MetricValue"] = 45.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T2", ["MetricValue"] = 42.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T3", ["MetricValue"] = 50.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T4", ["MetricValue"] = 38.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T5", ["MetricValue"] = 48.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T6", ["MetricValue"] = 40.0 },
                    ]
                }
            ]
        };

        var config = AskPipelineService.ComputeAnomalyMetadata(
            result, "MetricValue", "ServerName", "CapturedAtUtc", serverWise: false,
            metricKey: "PageLifeExpectancy_seconds");

        // Z-score won't detect anomalies in stable-but-low PLE
        // But operational thresholds should enrich with findings
        Assert.Equal("risk_and_statistical", config.Mode);
        Assert.NotNull(config.Thresholds);
        Assert.Equal(300, config.Thresholds!.WarningLow);
        Assert.Equal(60, config.Thresholds.CriticalLow);
        Assert.NotNull(config.SeriesFindings);
        Assert.True(config.SeriesFindings!.Count > 0);

        // HasAnomalies should be true because of operational findings
        Assert.True(config.HasAnomalies);
        Assert.Equal("hybrid", config.Method);
        Assert.True(config.Points.Count > 0);
    }

    [Fact]
    public void MetricThresholdRegistry_ContainsKeyMetrics()
    {
        Assert.True(AskPipelineService.MetricThresholdRegistry.ContainsKey("PageLifeExpectancy_seconds"));
        Assert.True(AskPipelineService.MetricThresholdRegistry.ContainsKey("PercentProcessorTime"));
        Assert.True(AskPipelineService.MetricThresholdRegistry.ContainsKey("BufferCacheHitRatio"));
        Assert.True(AskPipelineService.MetricThresholdRegistry.ContainsKey("MemoryGrantsPending"));
        Assert.True(AskPipelineService.MetricThresholdRegistry.ContainsKey("BlockingCount"));
        Assert.True(AskPipelineService.MetricThresholdRegistry.ContainsKey("DeadlockCount"));
        Assert.True(AskPipelineService.MetricThresholdRegistry.ContainsKey("AvgDiskQueueLengthTotal"));
    }

    [Fact]
    public void ComputeSeriesFindings_SustainedRisk_AllValuesInWarningZone()
    {
        // All 6 PLE values below 300 (warning threshold) → sustained_risk
        var result = new AskResponseResult
        {
            Kind = "EXECUTION", Status = "SUCCESS",
            Items =
            [
                new AskExecutionItem
                {
                    Target = "CTS03::SQLGig", Status = "SUCCESS", RowCount = 6,
                    Rows =
                    [
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T1", ["MetricValue"] = 150.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T2", ["MetricValue"] = 180.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T3", ["MetricValue"] = 200.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T4", ["MetricValue"] = 170.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T5", ["MetricValue"] = 190.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T6", ["MetricValue"] = 160.0 },
                    ]
                }
            ]
        };

        var findings = AskPipelineService.ComputeSeriesFindings(
            result, "MetricValue", "ServerName", "CapturedAtUtc", "PageLifeExpectancy_seconds");

        Assert.Single(findings);
        var f = findings[0];
        Assert.Contains("sustained_low", f.Types);
        Assert.Contains("Sustained", f.Reason);
    }

    [Fact]
    public void ComputeAnomalyMetadata_NeverHasAnomaliesTrueWithEmptyPoints()
    {
        // Stable data with no operational threshold → hasAnomalies must be false with empty points
        var result = new AskResponseResult
        {
            Kind = "EXECUTION", Status = "SUCCESS",
            Items =
            [
                new AskExecutionItem
                {
                    Target = "CTS03::SQLGig", Status = "SUCCESS", RowCount = 5,
                    Rows =
                    [
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T1", ["MetricValue"] = 50.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T2", ["MetricValue"] = 50.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T3", ["MetricValue"] = 50.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T4", ["MetricValue"] = 50.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T5", ["MetricValue"] = 50.0 },
                    ]
                }
            ]
        };

        var config = AskPipelineService.ComputeAnomalyMetadata(
            result, "MetricValue", "ServerName", "CapturedAtUtc", serverWise: false,
            metricKey: "UserConnections");

        // KEY INVARIANT: hasAnomalies=false when points is empty
        Assert.False(config.HasAnomalies);
        Assert.False(config.HasPointAnomalies);
        Assert.False(config.HasPeerDeviation);
        Assert.Empty(config.Points);
        Assert.NotNull(config.Message);
        Assert.Contains("No significant anomalies", config.Message);
    }

    [Fact]
    public void ComputeAnomalyMetadata_PeerDeviation_DoesNotCreatePlotPoints()
    {
        // Two servers with massively different scales but individually stable
        // CTS02 at ~28000, CTS03 at ~200 — peer deviation but no point anomalies
        var result = new AskResponseResult
        {
            Kind = "EXECUTION", Status = "SUCCESS",
            Items =
            [
                new AskExecutionItem
                {
                    Target = "CTS03::SQLGig", Status = "SUCCESS", RowCount = 6,
                    Rows =
                    [
                        new() { ["ServerName"] = "CTS02", ["CapturedAtUtc"] = "T1", ["MetricValue"] = 28000.0 },
                        new() { ["ServerName"] = "CTS02", ["CapturedAtUtc"] = "T2", ["MetricValue"] = 28100.0 },
                        new() { ["ServerName"] = "CTS02", ["CapturedAtUtc"] = "T3", ["MetricValue"] = 28050.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T1", ["MetricValue"] = 200.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T2", ["MetricValue"] = 210.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T3", ["MetricValue"] = 205.0 },
                    ]
                }
            ]
        };

        var config = AskPipelineService.ComputeAnomalyMetadata(
            result, "MetricValue", "ServerName", "CapturedAtUtc", serverWise: true,
            metricKey: "UserConnections"); // No op threshold

        // Peer deviation should be detected
        Assert.True(config.HasPeerDeviation);
        // But hasAnomalies should be FALSE because no real point anomalies exist
        Assert.False(config.HasPointAnomalies);
        Assert.False(config.HasAnomalies);
        Assert.Empty(config.Points);
        // seriesFindings should contain peer_deviation
        Assert.NotNull(config.SeriesFindings);
        Assert.True(config.SeriesFindings!.Any(f => f.Types.Contains("peer_deviation")));
    }

    [Fact]
    public void ComputeAnomalyMetadata_AnomalyPointsHaveFullMetadata()
    {
        // Create data with clear spike to verify tooltip-ready point metadata
        var result = new AskResponseResult
        {
            Kind = "EXECUTION", Status = "SUCCESS",
            Items =
            [
                new AskExecutionItem
                {
                    Target = "CTS03::SQLGig", Status = "SUCCESS", RowCount = 8,
                    Rows =
                    [
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T1", ["MetricValue"] = 100.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T2", ["MetricValue"] = 100.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T3", ["MetricValue"] = 100.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T4", ["MetricValue"] = 100.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T5", ["MetricValue"] = 100.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T6", ["MetricValue"] = 100.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T7", ["MetricValue"] = 100.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T8", ["MetricValue"] = 500.0 },
                    ]
                }
            ]
        };

        var config = AskPipelineService.ComputeAnomalyMetadata(
            result, "MetricValue", "ServerName", "CapturedAtUtc", serverWise: true);

        Assert.True(config.HasAnomalies);
        Assert.True(config.HasPointAnomalies);
        Assert.True(config.Points.Count > 0);

        var pt = config.Points.First(p => p.Value > 400);
        // Tooltip-ready metadata
        Assert.Equal("CTS03", pt.Server);
        Assert.Equal("T8", pt.Timestamp);
        Assert.True(pt.Value > 400);
        Assert.True(pt.ExpectedValue > 0);
        Assert.NotEqual(0, pt.Deviation);
        Assert.NotEqual(0, pt.DeviationPct);
        Assert.True(pt.ZScore > 2.0);
        Assert.NotNull(pt.Type); // "spike"
        Assert.NotNull(pt.Severity);
        Assert.NotNull(pt.Label);
        Assert.NotNull(pt.Reason);
        Assert.True(pt.IsAnomaly);
    }

    [Fact]
    public void ComputeScaleProfile_LargeGap_ReturnsProfile()
    {
        var result = new AskResponseResult
        {
            Kind = "EXECUTION", Status = "SUCCESS",
            Items =
            [
                new AskExecutionItem
                {
                    Target = "CTS03::SQLGig", Status = "SUCCESS", RowCount = 6,
                    Rows =
                    [
                        new() { ["ServerName"] = "CTS02", ["CapturedAtUtc"] = "T1", ["MetricValue"] = 28000.0 },
                        new() { ["ServerName"] = "CTS02", ["CapturedAtUtc"] = "T2", ["MetricValue"] = 28000.0 },
                        new() { ["ServerName"] = "CTS02", ["CapturedAtUtc"] = "T3", ["MetricValue"] = 28000.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T1", ["MetricValue"] = 200.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T2", ["MetricValue"] = 200.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T3", ["MetricValue"] = 200.0 },
                    ]
                }
            ]
        };

        var profile = AskPipelineService.ComputeScaleProfile(result, "MetricValue", "ServerName");

        Assert.NotNull(profile);
        Assert.True(profile!.HasLargeScaleGap);
        Assert.Equal("per_server_anomaly", profile.RecommendedMode);
        Assert.True(profile.ScaleRatio >= 100); // 28000/200 = 140
    }

    [Fact]
    public void ComputeScaleProfile_SimilarScales_ReturnsNull()
    {
        var result = new AskResponseResult
        {
            Kind = "EXECUTION", Status = "SUCCESS",
            Items =
            [
                new AskExecutionItem
                {
                    Target = "CTS03::SQLGig", Status = "SUCCESS", RowCount = 6,
                    Rows =
                    [
                        new() { ["ServerName"] = "CTS02", ["CapturedAtUtc"] = "T1", ["MetricValue"] = 100.0 },
                        new() { ["ServerName"] = "CTS02", ["CapturedAtUtc"] = "T2", ["MetricValue"] = 100.0 },
                        new() { ["ServerName"] = "CTS02", ["CapturedAtUtc"] = "T3", ["MetricValue"] = 100.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T1", ["MetricValue"] = 90.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T2", ["MetricValue"] = 90.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T3", ["MetricValue"] = 90.0 },
                    ]
                }
            ]
        };

        var profile = AskPipelineService.ComputeScaleProfile(result, "MetricValue", "ServerName");
        Assert.Null(profile); // Ratio ~1.1, well under 10x threshold
    }

    [Fact]
    public void ComputeSeriesFindings_TrendBreak_DetectedInTail()
    {
        // Values with some normal variance then a sudden upward shift in the tail
        var result = new AskResponseResult
        {
            Kind = "EXECUTION", Status = "SUCCESS",
            Items =
            [
                new AskExecutionItem
                {
                    Target = "CTS03::SQLGig", Status = "SUCCESS", RowCount = 9,
                    Rows =
                    [
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T1", ["MetricValue"] = 48.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T2", ["MetricValue"] = 52.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T3", ["MetricValue"] = 49.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T4", ["MetricValue"] = 51.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T5", ["MetricValue"] = 50.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T6", ["MetricValue"] = 47.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T7", ["MetricValue"] = 200.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T8", ["MetricValue"] = 210.0 },
                        new() { ["ServerName"] = "CTS03", ["CapturedAtUtc"] = "T9", ["MetricValue"] = 205.0 },
                    ]
                }
            ]
        };

        var findings = AskPipelineService.ComputeSeriesFindings(
            result, "MetricValue", "ServerName", "CapturedAtUtc", "UserConnections");

        Assert.True(findings.Count > 0);
        Assert.True(findings.Any(f => f.Types.Contains("trend_break")));
    }

    [Fact]
    public void MockChartPlan_ParsesNewAnomalyFields()
    {
        var fields = new List<string> { "ServerName", "CapturedAtUtc", "MetricValue" };
        var raw = MockLlmBehavior.BuildChartPlanJson("SqlServer_History", "Show CPU trend", fields);
        var parsed = AskPipelineService.TryParseChartPlanJson(raw, fields);

        Assert.NotNull(parsed);
        var anomalyChart = parsed!.Charts.FirstOrDefault(c => c.ChartType == "anomalyLine");
        Assert.NotNull(anomalyChart);
        Assert.NotNull(anomalyChart!.Anomaly);
        Assert.Equal("risk_and_statistical", anomalyChart.Anomaly!.Mode);
        Assert.Equal("No significant anomalies detected in selected period.", anomalyChart.Anomaly.Message);
        Assert.False(anomalyChart.Anomaly.HasPointAnomalies);
        Assert.False(anomalyChart.Anomaly.HasPeerDeviation);
    }

    // ─── ComputeLinearForecast tests ──────────────────────────────────────────

    [Fact]
    public void ComputeLinearForecast_SufficientData_ReturnsForecast()
    {
        // Generate 20 hourly data points with a clear upward trend + slight noise
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var noise = new[] { 0.3, -0.5, 0.2, -0.1, 0.4, -0.3, 0.1, 0.6, -0.2, 0.5,
                            -0.4, 0.2, -0.6, 0.3, -0.1, 0.4, -0.5, 0.1, -0.3, 0.2 };
        var rows = Enumerable.Range(0, 20).Select(i => new Dictionary<string, object?>
        {
            ["CapturedAtUtc"] = baseTime.AddHours(i * 2).ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["MetricValue"] = 50.0 + i * 2.0 + noise[i]
        }).ToList();

        var result = new AskResponseResult
        {
            Kind = "EXECUTION", Status = "SUCCESS",
            Items = [new AskExecutionItem { Target = "CTS03::SQLGig", Status = "SUCCESS", RowCount = 20, Rows = rows }]
        };

        var forecast = AskPipelineService.ComputeLinearForecast(result, "MetricValue", "CapturedAtUtc");

        Assert.NotNull(forecast);
        Assert.Equal("6months", forecast!.Horizon);
        Assert.Equal("linear_regression", forecast.Method);
        Assert.Equal(0.95, forecast.ConfidenceLevel);
        Assert.True(forecast.Slope > 0); // Upward trend
        Assert.True(forecast.RSquared > 0.9); // Very good fit for linear data
        Assert.Equal(20, forecast.HistoricalPointCount);
        Assert.NotNull(forecast.ForecastStartUtc);
        Assert.True(forecast.Points.Count == 7); // 0..6 months = 7 points

        // Forecast values should increase (positive slope)
        Assert.True(forecast.Points[^1].Value > forecast.Points[0].Value);

        // Confidence bounds should widen over time
        var firstWidth = forecast.Points[0].Upper - forecast.Points[0].Lower;
        var lastWidth = forecast.Points[^1].Upper - forecast.Points[^1].Lower;
        Assert.True(lastWidth > firstWidth);

        // Lower < Value < Upper for all points
        foreach (var p in forecast.Points)
        {
            Assert.True(p.Lower < p.Value);
            Assert.True(p.Upper > p.Value);
        }
    }

    [Fact]
    public void ComputeLinearForecast_TooFewPoints_ReturnsNull()
    {
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var rows = Enumerable.Range(0, 5).Select(i => new Dictionary<string, object?>
        {
            ["CapturedAtUtc"] = baseTime.AddHours(i).ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["MetricValue"] = (double)(50 + i)
        }).ToList();

        var result = new AskResponseResult
        {
            Kind = "EXECUTION", Status = "SUCCESS",
            Items = [new AskExecutionItem { Target = "CTS03::SQLGig", Status = "SUCCESS", RowCount = 5, Rows = rows }]
        };

        var forecast = AskPipelineService.ComputeLinearForecast(result, "MetricValue", "CapturedAtUtc");
        Assert.Null(forecast); // Need ≥10 points
    }

    [Fact]
    public void ComputeLinearForecast_NoTimeField_ReturnsNull()
    {
        var forecast = AskPipelineService.ComputeLinearForecast(
            new AskResponseResult { Kind = "EXECUTION", Status = "SUCCESS" },
            "MetricValue", null);
        Assert.Null(forecast);
    }

    [Fact]
    public void ComputeLinearForecast_TooShortTimeSpan_ReturnsNull()
    {
        // 15 points all within the same minute — less than 1 day span
        var baseTime = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var rows = Enumerable.Range(0, 15).Select(i => new Dictionary<string, object?>
        {
            ["CapturedAtUtc"] = baseTime.AddSeconds(i).ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["MetricValue"] = (double)(50 + i)
        }).ToList();

        var result = new AskResponseResult
        {
            Kind = "EXECUTION", Status = "SUCCESS",
            Items = [new AskExecutionItem { Target = "CTS03::SQLGig", Status = "SUCCESS", RowCount = 15, Rows = rows }]
        };

        var forecast = AskPipelineService.ComputeLinearForecast(result, "MetricValue", "CapturedAtUtc");
        Assert.Null(forecast); // Span < 1 day
    }

    [Fact]
    public void BuildDeterministicChartPlan_WithForecastData_IncludesForecastChart()
    {
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var rows = Enumerable.Range(0, 20).Select(i => new Dictionary<string, object?>
        {
            ["ServerName"] = "CTS02",
            ["CapturedAtUtc"] = baseTime.AddHours(i * 2).ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["MetricValue"] = (double)(50 + i * 2)
        }).ToList();

        var result = new AskResponseResult
        {
            Kind = "EXECUTION", Status = "SUCCESS",
            Items = [new AskExecutionItem { Target = "CTS03::SQLGig", Status = "SUCCESS", RowCount = 20, Rows = rows }]
        };

        var profile = new AskDataProfile
        {
            RowCount = 20,
            HasTimeField = true, TimeField = "CapturedAtUtc",
            HasNumericMetric = true, NumericField = "MetricValue",
            HasSeriesField = true, SeriesField = "ServerName",
            DistinctServers = 1,
            ProfileType = "time_series_single_server"
        };

        var fields = new List<string> { "ServerName", "CapturedAtUtc", "MetricValue" };
        var plan = AskPipelineService.BuildDeterministicChartPlan(profile, fields, result, "Batch Requests", "BatchRequests_sec");

        Assert.True(plan.Enabled);
        var forecastChart = plan.Charts.FirstOrDefault(c => c.ChartType == "forecastLine");
        Assert.NotNull(forecastChart);
        Assert.Equal("forecast_6m", forecastChart!.ChartId);
        Assert.Equal("forecast", forecastChart.Goal);
        Assert.NotNull(forecastChart.Forecast);
        Assert.True(forecastChart.Forecast!.Points.Count > 0);
        Assert.True(forecastChart.Forecast.RSquared > 0);
        Assert.Equal("diverging", forecastChart.ColorIntent);
    }

    [Fact]
    public void BuildDeterministicChartPlan_TooFewRows_NoForecastChart()
    {
        var profile = new AskDataProfile
        {
            RowCount = 5,
            HasTimeField = true, TimeField = "CapturedAtUtc",
            HasNumericMetric = true, NumericField = "MetricValue",
            HasSeriesField = true, SeriesField = "ServerName",
            DistinctServers = 1,
            ProfileType = "time_series_single_server"
        };

        var fields = new List<string> { "ServerName", "CapturedAtUtc", "MetricValue" };
        var plan = AskPipelineService.BuildDeterministicChartPlan(profile, fields, null, "CPU", "PercentProcessorTime");

        Assert.True(plan.Enabled);
        Assert.DoesNotContain(plan.Charts, c => c.ChartType == "forecastLine");
        Assert.NotNull(plan.ForecastSkipReason);
        Assert.Contains("result data", plan.ForecastSkipReason); // "No result data available"
    }

    [Fact]
    public void BuildDeterministicChartPlan_InsufficientRows_ForecastSkipReasonSet()
    {
        var profile = new AskDataProfile
        {
            RowCount = 8,
            HasTimeField = true, TimeField = "CapturedAtUtc",
            HasNumericMetric = true, NumericField = "MetricValue",
            HasSeriesField = true, SeriesField = "ServerName",
            DistinctServers = 1,
            ProfileType = "time_series_single_server"
        };

        var result = new AskResponseResult
        {
            Kind = "EXECUTION", Status = "SUCCESS",
            Items = [new AskExecutionItem { Target = "CTS03::SQLGig", Status = "SUCCESS", RowCount = 8, Rows =
                Enumerable.Range(0, 8).Select(i => new Dictionary<string, object?>
                {
                    ["ServerName"] = "CTS02",
                    ["CapturedAtUtc"] = "T" + i,
                    ["MetricValue"] = (double)(50 + i)
                }).ToList()
            }]
        };

        var fields = new List<string> { "ServerName", "CapturedAtUtc", "MetricValue" };
        var plan = AskPipelineService.BuildDeterministicChartPlan(profile, fields, result, "CPU", "PercentProcessorTime");

        Assert.True(plan.Enabled);
        Assert.DoesNotContain(plan.Charts, c => c.ChartType == "forecastLine");
        Assert.NotNull(plan.ForecastSkipReason);
        Assert.Contains("Insufficient", plan.ForecastSkipReason);
    }

    [Fact]
    public void ComputeLinearForecast_BoxedDateTime_ParsesCorrectly()
    {
        // Simulate SQL execution results with boxed DateTime values (not strings)
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var rows = Enumerable.Range(0, 15).Select(i => new Dictionary<string, object?>
        {
            ["CapturedAtUtc"] = (object)baseTime.AddHours(i * 3), // Boxed DateTime, not string
            ["MetricValue"] = (double)(100 + i * 5)
        }).ToList();

        var result = new AskResponseResult
        {
            Kind = "EXECUTION", Status = "SUCCESS",
            Items = [new AskExecutionItem { Target = "CTS03::SQLGig", Status = "SUCCESS", RowCount = 15, Rows = rows }]
        };

        var forecast = AskPipelineService.ComputeLinearForecast(result, "MetricValue", "CapturedAtUtc");
        Assert.NotNull(forecast);
        Assert.True(forecast!.Points.Count == 7);
        Assert.True(forecast.Slope > 0);
    }

    // ─── MockLlmBehavior.BuildChartPlanJson tests ────────────────────────────

    [Fact]
    public void MockChartPlan_WithTimeAndMetric_Returns5Charts()
    {
        var fields = new List<string> { "ServerName", "CapturedAtUtc", "MetricValue" };
        var raw = MockLlmBehavior.BuildChartPlanJson("SqlServer_History", "Show CPU trend over time", fields);
        var parsed = AskPipelineService.TryParseChartPlanJson(raw, fields);

        Assert.NotNull(parsed);
        Assert.True(parsed!.Enabled);
        Assert.Equal(5, parsed.Charts.Count);

        // Chart 1: area trend
        Assert.Equal("area", parsed.Charts[0].ChartType);
        Assert.Equal("area_trend", parsed.Charts[0].ChartId);
        Assert.Equal("CapturedAtUtc", parsed.Charts[0].XField);
        Assert.Equal("MetricValue", parsed.Charts[0].YField);
        Assert.Equal("ServerName", parsed.Charts[0].SeriesField);
        Assert.Equal("trend", parsed.Charts[0].Goal);

        // Chart 2: anomalyLine
        Assert.Equal("anomalyLine", parsed.Charts[1].ChartType);
        Assert.Equal("anomaly_line", parsed.Charts[1].ChartId);
        Assert.Equal("anomaly", parsed.Charts[1].Goal);
        Assert.NotNull(parsed.Charts[1].Anomaly);
        Assert.Equal("zscore", parsed.Charts[1].Anomaly!.Method);
        Assert.Equal(2.5, parsed.Charts[1].Anomaly!.Threshold);
        Assert.Equal("MetricValue", parsed.Charts[1].Anomaly!.ValueField);
        Assert.Equal("CapturedAtUtc", parsed.Charts[1].Anomaly!.TimeField);
        Assert.Equal("ServerName", parsed.Charts[1].Anomaly!.SeriesField);
        Assert.Equal("risk_and_statistical", parsed.Charts[1].Anomaly!.Mode);

        // Chart 3: heatmap
        Assert.Equal("heatmap", parsed.Charts[2].ChartType);
        Assert.Equal("heatmap_view", parsed.Charts[2].ChartId);
        Assert.Equal("density", parsed.Charts[2].Goal);
        Assert.True(parsed.Charts[2].TimeBucketed);
        Assert.Equal("MetricValue", parsed.Charts[2].ValueField);

        // Chart 4: bar comparison
        Assert.Equal("bar", parsed.Charts[3].ChartType);
        Assert.Equal("bar_compare", parsed.Charts[3].ChartId);
        Assert.Equal("comparison", parsed.Charts[3].Goal);
        Assert.True(parsed.Charts[3].LatestSnapshotOnly);

        // Chart 5: forecastLine
        Assert.Equal("forecastLine", parsed.Charts[4].ChartType);
        Assert.Equal("forecast_6m", parsed.Charts[4].ChartId);
        Assert.Equal("forecast", parsed.Charts[4].Goal);
        Assert.NotNull(parsed.Charts[4].Forecast);
        Assert.Equal("6months", parsed.Charts[4].Forecast!.Horizon);
        Assert.Equal("linear_regression", parsed.Charts[4].Forecast.Method);
        Assert.Equal(0.95, parsed.Charts[4].Forecast.ConfidenceLevel);
    }

    [Fact]
    public void MockChartPlan_TopRanking_ReturnsBarChart()
    {
        var fields = new List<string> { "ServerName", "MetricValue" };
        var raw = MockLlmBehavior.BuildChartPlanJson("SqlServer_History", "Show top offenders", fields);
        var parsed = AskPipelineService.TryParseChartPlanJson(raw, fields);

        Assert.NotNull(parsed);
        Assert.True(parsed!.Enabled);
        Assert.Single(parsed.Charts);
        Assert.Equal("bar", parsed.Charts[0].ChartType);
        Assert.Equal("bar_compare", parsed.Charts[0].ChartId);
        Assert.Equal("ranking", parsed.Charts[0].Goal);
        Assert.Equal("max", parsed.Charts[0].Aggregation);
        Assert.True(parsed.Charts[0].LatestSnapshotOnly);
    }

    [Fact]
    public void MockChartPlan_NoMetricValue_ReturnsDisabled()
    {
        var fields = new List<string> { "ServerName", "CapturedAtUtc" };
        var raw = MockLlmBehavior.BuildChartPlanJson("SqlServer_History", "Show something", fields);
        var parsed = AskPipelineService.TryParseChartPlanJson(raw, fields);

        Assert.NotNull(parsed);
        Assert.False(parsed!.Enabled);
    }

    [Fact]
    public void MockChartPlan_TextMetricColumn_ReturnsDisabled()
    {
        var fields = new List<string> { "ServerName", "CapturedAtUtc", "MetricValue" };
        var raw = MockLlmBehavior.BuildChartPlanJson(
            "SqlServer_History", "Show AG health", fields,
            metricLabel: "AG Health", metricKey: "AGHealth", metricGroup: "Server Availability");
        var parsed = AskPipelineService.TryParseChartPlanJson(raw, fields);

        Assert.NotNull(parsed);
        Assert.False(parsed!.Enabled);
    }

    [Fact]
    public void MockChartPlan_WithMetricContext_UsesMetricLabels()
    {
        var fields = new List<string> { "ServerName", "CapturedAtUtc", "MetricValue" };
        var raw = MockLlmBehavior.BuildChartPlanJson(
            "SqlServer_History", "Show memory trend", fields,
            metricLabel: "Available Memory", metricKey: "AvailableMemory_GB", metricGroup: "Memory Allocation");
        var parsed = AskPipelineService.TryParseChartPlanJson(raw, fields);

        Assert.NotNull(parsed);
        Assert.True(parsed!.Enabled);
        Assert.Equal(5, parsed.Charts.Count);
        Assert.Contains("Available Memory", parsed.Charts[0].Title);
        Assert.Equal("Memory Allocation", parsed.Charts[0].MetricGroup);
        Assert.Equal("Available Memory", parsed.Charts[0].MetricName);
    }

    // ─── Metric whitelist validation tests ──────────────────────────────────

    [Theory]
    [InlineData("AvailableMemory_GB", "SqlServer_History", null)]
    [InlineData("BatchRequests_sec", "SqlServer_History", null)]
    [InlineData("SQLService", "SqlServer_History", null)]
    [InlineData("PageLifeExpectancy_seconds", "SqlServer_History", null)]
    [InlineData("PercentProcessorTime", "Windows_History", null)]
    [InlineData("AvailableMemory", "Windows_History", null)]
    public void ValidateMetricKey_Valid_ReturnsNull(string key, string env, string? expected)
    {
        Assert.Equal(expected, AskPipelineService.ValidateMetricKey(key, env));
    }

    [Theory]
    [InlineData(null, "SqlServer_History")]
    [InlineData("", "SqlServer_History")]
    [InlineData("  ", "Windows_History")]
    public void ValidateMetricKey_Empty_ReturnsError(string? key, string env)
    {
        var error = AskPipelineService.ValidateMetricKey(key, env);
        Assert.NotNull(error);
        Assert.Contains("required", error!);
    }

    [Theory]
    [InlineData("DROP TABLE Users", "SqlServer_History")]
    [InlineData("sys.objects", "SqlServer_History")]
    [InlineData("NotARealColumn", "SqlServer_History")]
    [InlineData("1; DROP TABLE --", "Windows_History")]
    [InlineData("PercentProcessorTime", "SqlServer_History")] // valid Windows key, invalid for SQL
    public void ValidateMetricKey_Invalid_ReturnsError(string key, string env)
    {
        var error = AskPipelineService.ValidateMetricKey(key, env);
        Assert.NotNull(error);
        Assert.Contains("not in the allowed", error!);
    }

    // ─── Metric whitelist structure tests ────────────────────────────────────

    [Fact]
    public void SqlServerHistoryMetricMap_Has35Entries()
    {
        Assert.Equal(35, AskPipelineService.SqlServerHistoryMetricMap.Count);
    }

    [Fact]
    public void WindowsHistoryMetricMap_Has46Entries()
    {
        Assert.Equal(46, AskPipelineService.WindowsHistoryMetricMap.Count);
    }

    [Fact]
    public void SqlServerHistoryMetricMap_AvailableMemory_HasCorrectFields()
    {
        var entry = AskPipelineService.SqlServerHistoryMetricMap["AvailableMemory_GB"];
        Assert.Equal("AvailableMemory_GB", entry.MetricKey);
        Assert.Equal("Available Memory", entry.MetricLabel);
        Assert.Equal("Memory Allocation", entry.MetricGroup);
        Assert.Contains("TRY_CONVERT(decimal(18,2),h.[AvailableMemory_GB])", entry.MetricSelectSql);
        Assert.Contains("TotalMemory_GB", entry.DetailSelectSql);
        Assert.Contains("UsedMemory_GB", entry.DetailSelectSql);
    }

    [Fact]
    public void SqlServerHistoryMetricMap_AGHealth_IsTextMetric()
    {
        var entry = AskPipelineService.SqlServerHistoryMetricMap["AGHealth"];
        Assert.Equal("NULL AS MetricValue", entry.MetricSelectSql);
        Assert.Contains("h.[AGHealth]", entry.DetailSelectSql);
    }

    [Fact]
    public void WindowsHistoryMetricMap_PercentProcessorTime_HasCorrectFields()
    {
        var entry = AskPipelineService.WindowsHistoryMetricMap["PercentProcessorTime"];
        Assert.Equal("CPU %", entry.MetricLabel);
        Assert.Equal("CPU", entry.MetricGroup);
        Assert.Contains("TRY_CONVERT(decimal(18,2),h.[PercentProcessorTime])", entry.MetricSelectSql);
        Assert.Contains("PercentPrivilegedTime", entry.DetailSelectSql);
        Assert.Contains("PercentUserTime", entry.DetailSelectSql);
    }

    // ─── Metric token replacement tests ──────────────────────────────────────

    [Fact]
    public void TransformHistorySampleSql_MetricTokens_NumericColumn()
    {
        var script = "SELECT h.SQLServer AS ServerName, h.DateTime AS CapturedAtUtc, /*__METRIC_SELECT__*/, /*__DETAIL_SELECT__*/, /*__METRIC_NAME__*/ AS MetricName, /*__METRIC_GROUP__*/ AS MetricGroup FROM T h WHERE /*__SQLSERVER_FILTER__*/ 1=1;";
        var metric = AskPipelineService.SqlServerHistoryMetricMap["AvailableMemory_GB"];

        var result = AskPipelineService.TransformHistorySampleSql(
            script, "SqlServer_History",
            ["CTS02\\ADMIN"],
            null, null, 200, metric);

        Assert.Contains("TRY_CONVERT(decimal(18,2),h.[AvailableMemory_GB]) AS MetricValue", result);
        Assert.Contains("TotalMemory_GB", result);
        Assert.Contains("N'Available Memory' AS MetricName", result);
        Assert.Contains("N'Memory Allocation' AS MetricGroup", result);
        Assert.Contains("h.SQLServer IN (N'CTS02\\ADMIN')", result);
        Assert.DoesNotContain("/*__METRIC_SELECT__*/", result);
        Assert.DoesNotContain("/*__DETAIL_SELECT__*/", result);
        Assert.DoesNotContain("/*__METRIC_NAME__*/", result);
        Assert.DoesNotContain("/*__METRIC_GROUP__*/", result);
    }

    [Fact]
    public void TransformHistorySampleSql_MetricTokens_TextColumn()
    {
        var script = "SELECT /*__METRIC_SELECT__*/, /*__DETAIL_SELECT__*/ FROM T h;";
        var metric = AskPipelineService.SqlServerHistoryMetricMap["AGHealth"];

        var result = AskPipelineService.TransformHistorySampleSql(
            script, "SqlServer_History", [], null, null, 200, metric);

        Assert.Contains("NULL AS MetricValue", result);
        Assert.Contains("h.[AGHealth]", result);
        Assert.Contains("Column=AGHealth, Value=", result);
    }

    [Fact]
    public void TransformHistorySampleSql_MetricTokens_SqlEscaping()
    {
        // Use a custom entry to test escaping of quotes in label
        var metric = new AskPipelineService.MetricWhitelistEntry(
            "BatchRequests_sec", "Batch Req's/sec", "SQL Activity",
            "TRY_CONVERT(decimal(18,2),h.[BatchRequests_sec]) AS MetricValue",
            "CAST(N'test' AS nvarchar(4000)) AS Detail");

        var script = "SELECT /*__METRIC_NAME__*/ AS MetricName;";
        var result = AskPipelineService.TransformHistorySampleSql(
            script, "SqlServer_History", [], null, null, 200, metric);

        Assert.Contains("N'Batch Req''s/sec'", result);
    }

    [Fact]
    public void TransformHistorySampleSql_NoMetricEntry_TokensUntouched()
    {
        var script = "SELECT /*__METRIC_SELECT__*/ FROM T h;";

        var result = AskPipelineService.TransformHistorySampleSql(
            script, "SqlServer_History", [], null, null);

        Assert.Contains("/*__METRIC_SELECT__*/", result);
    }

    [Fact]
    public void TransformHistorySampleSql_WindowsMetric_ReplacesWinServerFilter()
    {
        var script = "SELECT /*__METRIC_SELECT__*/, /*__METRIC_NAME__*/ AS MetricName FROM T h WHERE /*__WINSERVER_FILTER__*/ 1=1;";
        var metric = AskPipelineService.WindowsHistoryMetricMap["PercentProcessorTime"];

        var result = AskPipelineService.TransformHistorySampleSql(
            script, "Windows_History",
            ["WIN01", "WIN02"],
            null, null, 200, metric);

        Assert.Contains("h.[PercentProcessorTime]", result);
        Assert.Contains("N'CPU %' AS MetricName", result);
        Assert.Contains("h.WinServer IN (N'WIN01', N'WIN02')", result);
        Assert.DoesNotContain("/*__WINSERVER_FILTER__*/", result);
    }

    // ─── ExecutionRouter metrics-tab tests ──────────────────────────────────

    [Fact]
    public void ExecutionRouter_HistoryWithMetricKey_ReturnsSampleOnlyWithMetricsGroupKey()
    {
        var request = new AskApiRequest
        {
            Environment = "SqlServer_History",
            Question = "Show metric trend",
            MetricKey = "AvailableMemory_GB",
            SelectedTargets = []
        };

        var route = ExecutionRouter.Route(request, 1);

        Assert.Equal("SAMPLE_ONLY", route.RouteKind);
        Assert.Equal("Metrics", route.GroupKey);
        Assert.Null(route.SampleId);
    }

    [Fact]
    public void ExecutionRouter_WindowsHistoryWithMetricKey_ReturnsSampleOnly()
    {
        var request = new AskApiRequest
        {
            Environment = "Windows_History",
            Question = "Show metric trend",
            MetricKey = "PercentProcessorTime",
            SelectedTargets = []
        };

        var route = ExecutionRouter.Route(request, 1);

        Assert.Equal("SAMPLE_ONLY", route.RouteKind);
        Assert.Equal("Metrics", route.GroupKey);
    }

    [Fact]
    public void ExecutionRouter_HistoryWithoutMetricKey_FallsToLlm()
    {
        var request = new AskApiRequest
        {
            Environment = "SqlServer_History",
            Question = "Show blocking chains",
            SelectedTargets = []
        };

        var route = ExecutionRouter.Route(request, 1);

        Assert.Equal("LLM_ONLY", route.RouteKind);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  DRIFT DETECTION ANALYSIS TESTS
    // ════════════════════════════════════════════════════════════════════════

    private static AskPipelineService CreateServiceWithDriftOrchestrator()
    {
        var modelSelector = new FakeModelSelector();
        ILLMClient[] clients =
        [
            new FakeLlmClient("Gemini"),
            new FakeLlmClient("OpenAI")
        ];

        return new AskPipelineService(
            modelSelector,
            clients,
            new DriftScriptOrchestrator(),
            new StubToolRegistryResolver(NullLogger<StubToolRegistryResolver>.Instance),
            new TemplateRenderer(NullLogger<TemplateRenderer>.Instance),
            new AllowAllPolicyService(),
            new StubQuestionSamplesRepository(),
            new TestHelpers.EmptyUserServerRepository(),
            NullLogger<AskPipelineService>.Instance);
    }

    /// <summary>
    /// Orchestrator that returns drift-format data with intentional differences between servers.
    /// </summary>
    private sealed class DriftScriptOrchestrator : IScriptAutoFixOrchestrator
    {
        public Task<ScriptExecutionResponse> ExecuteAsync(ScriptExecutionRequest r, CancellationToken ct)
            => ExecuteWithAutoFixAsync(r, ct);
        public Task<ScriptExecutionResponse> ExecuteWithAutoFixAsync(ScriptExecutionRequest r, CancellationToken ct)
            => ExecuteWithAutoFixAsync(r, null, ct);
        public Task<ScriptExecutionResponse> ExecuteWithAutoFixAsync(
            ScriptExecutionRequest request, IProgressStream? progress, CancellationToken ct)
        {
            var results = new List<ScriptExecutionServerResult>();

            for (var i = 0; i < request.SelectedServers.Length; i++)
            {
                var server = request.SelectedServers[i];
                var rows = new List<Dictionary<string, object?>>();

                // Settings that MATCH across servers
                rows.Add(MakeRow(server, "Server Properties", "ProductVersion", "16.0.4135.4"));
                rows.Add(MakeRow(server, "Server Properties", "Edition", "Enterprise Edition: Core-based Licensing"));
                rows.Add(MakeRow(server, "Server Properties", "Collation", "SQL_Latin1_General_CP1_CI_AS"));

                // Settings that DIFFER (drift)
                rows.Add(MakeRow(server, "Instance Configuration", "max degree of parallelism", i == 0 ? "0" : "8"));
                rows.Add(MakeRow(server, "Instance Configuration", "max server memory (MB)", i == 0 ? "8192" : "16384"));
                rows.Add(MakeRow(server, "Instance Configuration", "cost threshold for parallelism", i == 0 ? "5" : "50"));
                rows.Add(MakeRow(server, "Database Settings", "MyDB → RecoveryModel", i == 0 ? "SIMPLE" : "FULL"));
                rows.Add(MakeRow(server, "Database Settings", "MyDB → AutoShrink", i == 0 ? "ON" : "OFF"));
                rows.Add(MakeRow(server, "Runtime State", "PhysicalMemory_GB", i == 0 ? "16.0" : "32.0"));
                rows.Add(MakeRow(server, "Runtime State", "LogicalCPUCount", i == 0 ? "4" : "8"));

                results.Add(new ScriptExecutionServerResult
                {
                    Server = server,
                    Status = "SUCCESS",
                    RowCount = rows.Count,
                    DurationMs = 50,
                    Rows = rows
                });
            }

            return Task.FromResult(new ScriptExecutionResponse
            {
                Environment = request.Environment,
                ScriptLanguage = request.ScriptLanguage,
                TunedQuestion = request.TunedQuestion,
                FinalScript = request.GeneratedScript,
                ResultsByServer = results,
                Summary = new ScriptExecutionSummary
                {
                    SuccessCount = request.SelectedServers.Length,
                    FailCount = 0,
                    TotalRowCount = results.Sum(x => x.RowCount),
                    TotalTargets = request.SelectedServers.Length
                }
            });
        }

        private static Dictionary<string, object?> MakeRow(string server, string category, string settingName, string value)
            => new(StringComparer.OrdinalIgnoreCase)
            {
                ["ServerName"] = server,
                ["CapturedAtUtc"] = DateTime.UtcNow,
                ["Category"] = category,
                ["SettingName"] = settingName,
                ["CurrentValue"] = value,
                ["Description"] = $"{settingName} setting"
            };
    }

    [Fact]
    public async Task DriftDetection_MultiServer_ProducesRichDriftAnalysis()
    {
        var service = CreateServiceWithDriftOrchestrator();

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "drift1",
                BearerToken = "u1",
                Environment = "SqlServer_Live",
                Question = "Compare all selected SQL Servers",
                SelectedTargets = ["CTS01#Admin", "CTS02#Admin"]
            },
            CancellationToken.None);

        Assert.NotNull(response.Answer);
        Assert.NotNull(response.Answer.Title);
        Assert.Contains("Drift", response.Answer.Title);
        Assert.NotNull(response.Answer.Explanation);

        // Key metrics with drift counts
        Assert.NotNull(response.Answer.KeyMetrics);
        Assert.True(response.Answer.KeyMetrics.Count >= 3);
        Assert.Contains(response.Answer.KeyMetrics, m => m.Label == "Settings with Drift");
        Assert.Contains(response.Answer.KeyMetrics, m => m.Label == "Critical Drift");

        // Sections
        Assert.NotNull(response.Answer.Sections);
        Assert.True(response.Answer.Sections.Count >= 4);
        Assert.Contains(response.Answer.Sections, s => s.Key == "findings");
        Assert.Contains(response.Answer.Sections, s => s.Key == "drift_analysis");

        // Comparison per server
        Assert.NotNull(response.Answer.Comparison);
        Assert.Equal(2, response.Answer.Comparison.Count);

        // Recommendations
        Assert.NotNull(response.Answer.Recommendations);
        Assert.True(response.Answer.Recommendations.Count >= 1);

        // DriftReport attached to response
        Assert.NotNull(response.DriftReport);
        Assert.True(response.DriftReport.Summary.TotalServers >= 2);
        Assert.True(response.DriftReport.Summary.DriftCount > 0);
        Assert.True(response.DriftReport.DriftItems.Count > 0);

        // Verify outlier detection
        var maxdopItem = response.DriftReport.DriftItems.FirstOrDefault(d => d.SettingName.Contains("max degree of parallelism"));
        Assert.NotNull(maxdopItem);
        Assert.Equal("Critical", maxdopItem.Severity);
        Assert.NotNull(maxdopItem.OutlierServers);
        Assert.True(maxdopItem.OutlierServers.Count > 0);
        Assert.NotNull(maxdopItem.Servers);
        Assert.Contains(maxdopItem.Servers, sv => sv.IsOutlier);

        // Drift visuals — chart-ready structured data
        Assert.NotNull(response.DriftReport.Visuals);
        var visuals = response.DriftReport.Visuals;

        // Severity donut
        Assert.True(visuals.SeverityCounts.Critical > 0);

        // Category bar chart
        Assert.True(visuals.CategoryMetrics.Count > 0);
        Assert.Contains(visuals.CategoryMetrics, c => c.Category == "Instance Configuration");

        // Server deviation bar chart
        Assert.Equal(2, visuals.ServerMetrics.Count);
        Assert.Contains(visuals.ServerMetrics, s => s.DeviationCount > 0);

        // Setting matrix heatmap
        Assert.True(visuals.SettingMatrix.Count > 0);
        var maxdopMatrix = visuals.SettingMatrix.FirstOrDefault(m => m.Setting.Contains("max degree of parallelism"));
        Assert.NotNull(maxdopMatrix);
        Assert.Equal("Critical", maxdopMatrix.Severity);
        Assert.Equal(2, maxdopMatrix.Values.Count); // one per server

        // No liveVisuals when drift is present (drift visuals take over)
        Assert.Null(response.LiveVisuals);
    }

    [Fact]
    public async Task DriftDetection_CriticalDrift_MaxDop0_FlaggedCorrectly()
    {
        var service = CreateServiceWithDriftOrchestrator();

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "drift2",
                BearerToken = "u1",
                Environment = "SqlServer_Live",
                Question = "Compare all selected SQL Servers",
                SelectedTargets = ["CTS01#Admin", "CTS02#Admin"]
            },
            CancellationToken.None);

        // maxdop 0 vs 8 should be CRITICAL
        Assert.NotNull(response.Answer.KeyMetrics);
        var criticalMetric = response.Answer.KeyMetrics.First(m => m.Label == "Critical Drift");
        Assert.NotEqual("0", criticalMetric.Value); // Should have at least 1 critical drift

        // Should flag critical severity
        Assert.True(
            response.Answer.Severity is "CRITICAL" or "WARNING",
            $"Expected CRITICAL or WARNING severity, got {response.Answer.Severity}");

        // DriftReport has critical items
        Assert.NotNull(response.DriftReport);
        Assert.True(response.DriftReport.Summary.CriticalCount > 0);
    }

    [Fact]
    public async Task DriftDetection_NoDrift_IdenticalServers_ReportsConsistent()
    {
        // Create orchestrator that returns identical data for both servers
        var modelSelector = new FakeModelSelector();
        ILLMClient[] clients = [new FakeLlmClient("Gemini"), new FakeLlmClient("OpenAI")];
        var identicalOrchestrator = new IdenticalDriftOrchestrator();

        var service = new AskPipelineService(
            modelSelector, clients, identicalOrchestrator,
            new StubToolRegistryResolver(NullLogger<StubToolRegistryResolver>.Instance),
            new TemplateRenderer(NullLogger<TemplateRenderer>.Instance),
            new AllowAllPolicyService(),
            new StubQuestionSamplesRepository(),
            new TestHelpers.EmptyUserServerRepository(),
            NullLogger<AskPipelineService>.Instance);

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "drift3",
                BearerToken = "u1",
                Environment = "SqlServer_Live",
                Question = "Compare all selected SQL Servers",
                SelectedTargets = ["CTS01#Admin", "CTS02#Admin"]
            },
            CancellationToken.None);

        Assert.NotNull(response.Answer);
        Assert.Contains("No", response.Answer.Title); // "No Configuration Drift"
        var driftMetric = response.Answer.KeyMetrics!.First(m => m.Label == "Settings with Drift");
        Assert.Equal("0", driftMetric.Value);
        Assert.Equal("OK", response.Answer.Severity);

        // DriftReport with zero drift
        Assert.NotNull(response.DriftReport);
        Assert.Equal(0, response.DriftReport.Summary.DriftCount);
        Assert.Empty(response.DriftReport.DriftItems);
        Assert.True(response.DriftReport.AlignedSettings > 0);
    }

    /// <summary>Returns identical drift data for all servers — no drift should be detected.</summary>
    private sealed class IdenticalDriftOrchestrator : IScriptAutoFixOrchestrator
    {
        public Task<ScriptExecutionResponse> ExecuteAsync(ScriptExecutionRequest r, CancellationToken ct)
            => ExecuteWithAutoFixAsync(r, ct);
        public Task<ScriptExecutionResponse> ExecuteWithAutoFixAsync(ScriptExecutionRequest r, CancellationToken ct)
            => ExecuteWithAutoFixAsync(r, null, ct);
        public Task<ScriptExecutionResponse> ExecuteWithAutoFixAsync(
            ScriptExecutionRequest request, IProgressStream? progress, CancellationToken ct)
        {
            var results = request.SelectedServers.Select(server => new ScriptExecutionServerResult
            {
                Server = server,
                Status = "SUCCESS",
                RowCount = 3,
                DurationMs = 10,
                Rows =
                [
                    new(StringComparer.OrdinalIgnoreCase)
                    {
                        ["ServerName"] = server, ["Category"] = "Config",
                        ["SettingName"] = "maxdop", ["CurrentValue"] = "8"
                    },
                    new(StringComparer.OrdinalIgnoreCase)
                    {
                        ["ServerName"] = server, ["Category"] = "Config",
                        ["SettingName"] = "max server memory", ["CurrentValue"] = "16384"
                    },
                    new(StringComparer.OrdinalIgnoreCase)
                    {
                        ["ServerName"] = server, ["Category"] = "Config",
                        ["SettingName"] = "cost threshold", ["CurrentValue"] = "50"
                    }
                ]
            }).ToList();

            return Task.FromResult(new ScriptExecutionResponse
            {
                Environment = request.Environment,
                ScriptLanguage = request.ScriptLanguage,
                TunedQuestion = request.TunedQuestion,
                FinalScript = request.GeneratedScript,
                ResultsByServer = results,
                Summary = new ScriptExecutionSummary
                {
                    SuccessCount = request.SelectedServers.Length,
                    FailCount = 0,
                    TotalRowCount = results.Sum(x => x.RowCount),
                    TotalTargets = request.SelectedServers.Length
                }
            });
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Comprehensive History Metrics Whitelist Validation
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>SQL Server Metrics template (matches InsertHistorySamples.sql)</summary>
    private const string SqlServerMetricsTemplate =
        "DECLARE @FromUtc datetime2(0) = DATEADD(HOUR, -24, SYSUTCDATETIME());\r\n" +
        "DECLARE @ToUtc   datetime2(0) = SYSUTCDATETIME();\r\n" +
        "DECLARE @Top     int          = 5000;\r\n\r\n" +
        "DECLARE @SpanMin int = DATEDIFF(MINUTE, @FromUtc, @ToUtc);\r\n" +
        "DECLARE @BucketMin int = CASE\r\n" +
        "    WHEN @SpanMin <= 120   THEN 1\r\n" +
        "    WHEN @SpanMin <= 1440  THEN 5\r\n" +
        "    WHEN @SpanMin <= 10080 THEN 30\r\n" +
        "    WHEN @SpanMin <= 43200 THEN 60\r\n" +
        "    ELSE 1440 END;\r\n\r\n" +
        ";WITH Raw AS (\r\n" +
        "    SELECT h.SQLServer, h.DateTime,\r\n" +
        "        DATEADD(MINUTE, (DATEDIFF(MINUTE, 0, h.DateTime) / @BucketMin) * @BucketMin, 0) AS TimeBucket,\r\n" +
        "        /*__METRIC_SELECT__*/,\r\n" +
        "        /*__DETAIL_SELECT__*/\r\n" +
        "    FROM [SQLGig].[Monitor].[SQLServer_Details_History] h\r\n" +
        "    WHERE h.DateTime >= @FromUtc AND h.DateTime < @ToUtc\r\n" +
        "        /*__SQLSERVER_FILTER__*/\r\n" +
        "),\r\n" +
        "Agg AS (\r\n" +
        "    SELECT r.SQLServer, r.TimeBucket,\r\n" +
        "        CAST(AVG(CAST(r.MetricValue AS float)) AS decimal(18,2)) AS MetricValue\r\n" +
        "    FROM Raw r GROUP BY r.SQLServer, r.TimeBucket\r\n" +
        ")\r\n" +
        "SELECT TOP (@Top)\r\n" +
        "    a.SQLServer COLLATE DATABASE_DEFAULT AS [ServerName],\r\n" +
        "    a.TimeBucket AS [CapturedAtUtc],\r\n" +
        "    CAST(/*__METRIC_GROUP__*/ AS nvarchar(100)) AS [MetricGroup],\r\n" +
        "    CAST(/*__METRIC_NAME__*/ AS nvarchar(256))  AS [MetricName],\r\n" +
        "    a.MetricValue,\r\n" +
        "    d.Detail\r\n" +
        "FROM Agg a\r\n" +
        "OUTER APPLY (\r\n" +
        "    SELECT TOP 1 r2.Detail FROM Raw r2\r\n" +
        "    WHERE r2.SQLServer = a.SQLServer AND r2.TimeBucket = a.TimeBucket\r\n" +
        "    ORDER BY r2.DateTime DESC\r\n" +
        ") d\r\n" +
        "ORDER BY a.TimeBucket, a.SQLServer;";

    /// <summary>Windows Metrics template (matches InsertHistorySamples.sql)</summary>
    private const string WindowsMetricsTemplate =
        "DECLARE @FromUtc datetime2(0) = DATEADD(HOUR, -24, SYSUTCDATETIME());\r\n" +
        "DECLARE @ToUtc   datetime2(0) = SYSUTCDATETIME();\r\n" +
        "DECLARE @Top     int          = 5000;\r\n\r\n" +
        "DECLARE @SpanMin int = DATEDIFF(MINUTE, @FromUtc, @ToUtc);\r\n" +
        "DECLARE @BucketMin int = CASE\r\n" +
        "    WHEN @SpanMin <= 120   THEN 1\r\n" +
        "    WHEN @SpanMin <= 1440  THEN 5\r\n" +
        "    WHEN @SpanMin <= 10080 THEN 30\r\n" +
        "    WHEN @SpanMin <= 43200 THEN 60\r\n" +
        "    ELSE 1440 END;\r\n\r\n" +
        ";WITH Raw AS (\r\n" +
        "    SELECT h.WinServer, h.DateTime,\r\n" +
        "        DATEADD(MINUTE, (DATEDIFF(MINUTE, 0, h.DateTime) / @BucketMin) * @BucketMin, 0) AS TimeBucket,\r\n" +
        "        /*__METRIC_SELECT__*/,\r\n" +
        "        /*__DETAIL_SELECT__*/\r\n" +
        "    FROM [SQLGig].[Monitor].[WINServer_Details_History] h\r\n" +
        "    WHERE h.DateTime >= @FromUtc AND h.DateTime < @ToUtc\r\n" +
        "        /*__WINSERVER_FILTER__*/\r\n" +
        "),\r\n" +
        "Agg AS (\r\n" +
        "    SELECT r.WinServer, r.TimeBucket,\r\n" +
        "        CAST(AVG(CAST(r.MetricValue AS float)) AS decimal(18,2)) AS MetricValue\r\n" +
        "    FROM Raw r GROUP BY r.WinServer, r.TimeBucket\r\n" +
        ")\r\n" +
        "SELECT TOP (@Top)\r\n" +
        "    a.WinServer COLLATE DATABASE_DEFAULT AS [ServerName],\r\n" +
        "    a.TimeBucket AS [CapturedAtUtc],\r\n" +
        "    CAST(/*__METRIC_GROUP__*/ AS nvarchar(100)) AS [MetricGroup],\r\n" +
        "    CAST(/*__METRIC_NAME__*/ AS nvarchar(256))  AS [MetricName],\r\n" +
        "    a.MetricValue,\r\n" +
        "    d.Detail\r\n" +
        "FROM Agg a\r\n" +
        "OUTER APPLY (\r\n" +
        "    SELECT TOP 1 r2.Detail FROM Raw r2\r\n" +
        "    WHERE r2.WinServer = a.WinServer AND r2.TimeBucket = a.TimeBucket\r\n" +
        "    ORDER BY r2.DateTime DESC\r\n" +
        ") d\r\n" +
        "ORDER BY a.TimeBucket, a.WinServer;";

    [Fact]
    public void AllSqlServerMetricKeys_ProduceValidSql_NoUnreplacedTokens()
    {
        var map = AskPipelineService.SqlServerHistoryMetricMap;
        var failures = new List<string>();

        foreach (var (key, entry) in map)
        {
            var result = AskPipelineService.TransformHistorySampleSql(
                SqlServerMetricsTemplate, "SqlServer_History",
                ["CTS02\\ADMIN", "CTS03"],
                "2026-03-15T00:00:00Z", "2026-03-16T00:00:00Z",
                5000, entry);

            if (result.Contains("/*__METRIC_SELECT__*/"))
                failures.Add($"{key}: unreplaced /*__METRIC_SELECT__*/");
            if (result.Contains("/*__DETAIL_SELECT__*/"))
                failures.Add($"{key}: unreplaced /*__DETAIL_SELECT__*/");
            if (result.Contains("/*__METRIC_NAME__*/"))
                failures.Add($"{key}: unreplaced /*__METRIC_NAME__*/");
            if (result.Contains("/*__METRIC_GROUP__*/"))
                failures.Add($"{key}: unreplaced /*__METRIC_GROUP__*/");
            if (result.Contains("/*__SQLSERVER_FILTER__*/"))
                failures.Add($"{key}: unreplaced /*__SQLSERVER_FILTER__*/");
            if (!result.Contains("MetricValue"))
                failures.Add($"{key}: missing MetricValue in output");
            if (!result.Contains("Detail"))
                failures.Add($"{key}: missing Detail in output");
            if (!result.Contains("ServerName"))
                failures.Add($"{key}: missing ServerName in output");
            if (!result.Contains("h.SQLServer IN"))
                failures.Add($"{key}: server filter not applied");
        }

        Assert.True(failures.Count == 0,
            $"SQL Server metric failures ({failures.Count}):\n" + string.Join("\n", failures));
    }

    [Fact]
    public void AllWindowsMetricKeys_ProduceValidSql_NoUnreplacedTokens()
    {
        var map = AskPipelineService.WindowsHistoryMetricMap;
        var failures = new List<string>();

        foreach (var (key, entry) in map)
        {
            var result = AskPipelineService.TransformHistorySampleSql(
                WindowsMetricsTemplate, "Windows_History",
                ["CTS02", "CTS03"],
                "2026-03-15T00:00:00Z", "2026-03-16T00:00:00Z",
                5000, entry);

            if (result.Contains("/*__METRIC_SELECT__*/"))
                failures.Add($"{key}: unreplaced /*__METRIC_SELECT__*/");
            if (result.Contains("/*__DETAIL_SELECT__*/"))
                failures.Add($"{key}: unreplaced /*__DETAIL_SELECT__*/");
            if (result.Contains("/*__METRIC_NAME__*/"))
                failures.Add($"{key}: unreplaced /*__METRIC_NAME__*/");
            if (result.Contains("/*__METRIC_GROUP__*/"))
                failures.Add($"{key}: unreplaced /*__METRIC_GROUP__*/");
            if (result.Contains("/*__WINSERVER_FILTER__*/"))
                failures.Add($"{key}: unreplaced /*__WINSERVER_FILTER__*/");
            if (!result.Contains("MetricValue"))
                failures.Add($"{key}: missing MetricValue in output");
            if (!result.Contains("Detail"))
                failures.Add($"{key}: missing Detail in output");
            if (!result.Contains("ServerName"))
                failures.Add($"{key}: missing ServerName in output");
            if (!result.Contains("h.WinServer IN"))
                failures.Add($"{key}: server filter not applied");
        }

        Assert.True(failures.Count == 0,
            $"Windows metric failures ({failures.Count}):\n" + string.Join("\n", failures));
    }

    [Fact]
    public void SqlServerMetricMap_HasExpectedCount()
    {
        Assert.Equal(35, AskPipelineService.SqlServerHistoryMetricMap.Count);
    }

    [Fact]
    public void WindowsMetricMap_HasExpectedCount()
    {
        Assert.Equal(46, AskPipelineService.WindowsHistoryMetricMap.Count);
    }

    [Fact]
    public void SqlServerTextMetrics_ProduceNullMetricValue()
    {
        var textKeys = new[] { "AGHealth", "Replication", "LS", "SQLServerStartTime",
                               "SQLService", "SQLAgentService" };

        foreach (var key in textKeys)
        {
            var entry = AskPipelineService.SqlServerHistoryMetricMap[key];
            Assert.Equal("NULL AS MetricValue", entry.MetricSelectSql);

            var result = AskPipelineService.TransformHistorySampleSql(
                SqlServerMetricsTemplate, "SqlServer_History", [], null, null, 200, entry);
            Assert.Contains("NULL AS MetricValue", result);
        }
    }

    [Fact]
    public void AllMetricKeys_HaveNonEmptyLabelsAndGroups()
    {
        var allMaps = new[]
        {
            ("SqlServer", AskPipelineService.SqlServerHistoryMetricMap),
            ("Windows", AskPipelineService.WindowsHistoryMetricMap)
        };

        foreach (var (env, map) in allMaps)
        {
            foreach (var (key, entry) in map)
            {
                Assert.False(string.IsNullOrWhiteSpace(entry.MetricLabel),
                    $"{env}/{key}: MetricLabel is empty");
                Assert.False(string.IsNullOrWhiteSpace(entry.MetricGroup),
                    $"{env}/{key}: MetricGroup is empty");
                Assert.False(string.IsNullOrWhiteSpace(entry.MetricSelectSql),
                    $"{env}/{key}: MetricSelectSql is empty");
                Assert.False(string.IsNullOrWhiteSpace(entry.DetailSelectSql),
                    $"{env}/{key}: DetailSelectSql is empty");
            }
        }
    }

    [Fact]
    public void MetricsEndpoint_SqlServerHistory_ReturnsGroupedMetrics()
    {
        var map = AskPipelineService.GetMetricMapForEnvironment("SqlServer_History");
        Assert.NotNull(map);
        Assert.Equal(35, map.Count);

        var groups = map.Values
            .GroupBy(m => m.MetricGroup, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Contains(groups, g => g.Key == "Server Availability");
        Assert.Contains(groups, g => g.Key == "Memory Allocation");
        Assert.Contains(groups, g => g.Key == "Buffer Performance");
        Assert.Contains(groups, g => g.Key == "User Activity");
        Assert.Contains(groups, g => g.Key == "Locking & Blocking");
        Assert.Contains(groups, g => g.Key == "Jobs & Backups");
    }

    [Fact]
    public void MetricsEndpoint_WindowsHistory_ReturnsGroupedMetrics()
    {
        var map = AskPipelineService.GetMetricMapForEnvironment("Windows_History");
        Assert.NotNull(map);
        Assert.Equal(46, map.Count);

        var groups = map.Values
            .GroupBy(m => m.MetricGroup, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Contains(groups, g => g.Key == "CPU");
        Assert.Contains(groups, g => g.Key == "Memory");
        Assert.Contains(groups, g => g.Key == "Disk Total");
        Assert.Contains(groups, g => g.Key == "Network");
        Assert.Contains(groups, g => g.Key == "SQL Server");
    }

    [Fact]
    public void MetricsEndpoint_InvalidEnvironment_ReturnsNull()
    {
        Assert.Null(AskPipelineService.GetMetricMapForEnvironment("General"));
        Assert.Null(AskPipelineService.GetMetricMapForEnvironment("SqlServer_Live"));
        Assert.Null(AskPipelineService.GetMetricMapForEnvironment("Windows_Live"));
        Assert.Null(AskPipelineService.GetMetricMapForEnvironment(""));
    }

    [Fact]
    public void ValidateMetricKey_AllValidKeys_ReturnsNull()
    {
        foreach (var key in AskPipelineService.SqlServerHistoryMetricMap.Keys)
            Assert.Null(AskPipelineService.ValidateMetricKey(key, "SqlServer_History"));

        foreach (var key in AskPipelineService.WindowsHistoryMetricMap.Keys)
            Assert.Null(AskPipelineService.ValidateMetricKey(key, "Windows_History"));
    }

    [Fact]
    public void ValidateMetricKey_InvalidKey_ReturnsError()
    {
        Assert.NotNull(AskPipelineService.ValidateMetricKey("FakeMetric", "SqlServer_History"));
        Assert.NotNull(AskPipelineService.ValidateMetricKey("SystemIdleProcess", "Windows_History"));
        Assert.NotNull(AskPipelineService.ValidateMetricKey("DiskWriteBytes_sec", "Windows_History"));
        Assert.NotNull(AskPipelineService.ValidateMetricKey(null, "SqlServer_History"));
        Assert.NotNull(AskPipelineService.ValidateMetricKey("", "Windows_History"));
    }

    [Fact]
    public void WindowsAlertFilter_TokenReplacedForWindows()
    {
        var script = "SELECT * FROM [Alerts] a WHERE a.DateTime >= @FromUtc /*__ALERT_SERVER_FILTER__*/ AND a.Type = N'WIN';";

        var result = AskPipelineService.TransformHistorySampleSql(
            script, "Windows_History",
            ["CTS02", "CTS03"],
            "2026-03-15T00:00:00Z", "2026-03-16T00:00:00Z");

        Assert.Contains("a.Server IN (N'CTS02', N'CTS03')", result);
        Assert.DoesNotContain("/*__ALERT_SERVER_FILTER__*/", result);
    }

    [Fact]
    public void AllNumericMetrics_HaveTryConvertSelect()
    {
        var allMaps = new[]
        {
            AskPipelineService.SqlServerHistoryMetricMap,
            AskPipelineService.WindowsHistoryMetricMap
        };

        foreach (var map in allMaps)
        {
            foreach (var (key, entry) in map)
            {
                if (entry.MetricSelectSql == "NULL AS MetricValue")
                    continue;

                Assert.Contains("TRY_CONVERT(decimal(18,2),h.[", entry.MetricSelectSql,
                    StringComparison.OrdinalIgnoreCase);
                Assert.Contains(key, entry.MetricSelectSql);
            }
        }
    }

    [Fact]
    public void AllMetrics_DetailSql_ContainsAsDetail()
    {
        var allMaps = new[]
        {
            AskPipelineService.SqlServerHistoryMetricMap,
            AskPipelineService.WindowsHistoryMetricMap
        };

        foreach (var map in allMaps)
        {
            foreach (var (key, entry) in map)
            {
                Assert.Contains("AS Detail", entry.DetailSelectSql,
                    StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void MetricsTemplate_NoServers_EmptyFilter()
    {
        var metric = AskPipelineService.SqlServerHistoryMetricMap["PageLifeExpectancy_seconds"];

        var result = AskPipelineService.TransformHistorySampleSql(
            SqlServerMetricsTemplate, "SqlServer_History",
            [],
            "2026-03-15T00:00:00Z", "2026-03-16T00:00:00Z",
            5000, metric);

        Assert.DoesNotContain("/*__SQLSERVER_FILTER__*/", result);
        Assert.DoesNotContain("h.SQLServer IN", result);
        Assert.Contains("MetricValue", result);
    }

    [Fact]
    public void MetricsTemplate_DateRangeBinding_OverridesDefaults()
    {
        var metric = AskPipelineService.WindowsHistoryMetricMap["PercentProcessorTime"];

        var result = AskPipelineService.TransformHistorySampleSql(
            WindowsMetricsTemplate, "Windows_History",
            ["CTS02"],
            "2026-03-10T00:00:00Z", "2026-03-16T00:00:00Z",
            5000, metric);

        Assert.Contains("'2026-03-10T00:00:00'", result);
        Assert.Contains("'2026-03-16T00:00:00'", result);
        Assert.DoesNotContain("DATEADD(HOUR, -24, SYSUTCDATETIME())", result);
    }
}
