using Application.Common.Interfaces;
using Application.Common.Models;
using Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.Tests;

public sealed class AskPipelineServiceTests
{
    [Fact]
    public async Task General_ListAllMoon_Returns_AnswerOnly_With_No_Script()
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
        Assert.NotNull(response.Answer.Suggestion);
        Assert.Null(response.Answer.Anomaly);
        Assert.Null(response.Answer.Analysis);
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
            CancellationToken cancellationToken)
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
            CancellationToken cancellationToken)
            => new FakeLlmClient(Provider).GenerateAsync(
                promptTemplate, tunedQuestion, environmentTag, modelKey, cancellationToken);

        public Task<string> ValidateTemplateAsync(
            string promptTemplate,
            string tunedQuestion,
            string environmentTag,
            string modelKey,
            CancellationToken cancellationToken)
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
            CancellationToken cancellationToken)
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
            CancellationToken cancellationToken)
        {
            _ = modelKey;
            _ = cancellationToken;

            if (promptTemplate.Contains("DataBot Explain", StringComparison.Ordinal))
            {
                return Task.FromResult("""
{"explanation":"All targets executed successfully.","anomaly":null,"analysis":null,"suggestion":null}
""");
            }

            if (environmentTag.Equals("General", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult("The Moon is Earth's natural satellite.");

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

            if (promptTemplate.Contains("DataBot-Windows generating READ-ONLY PowerShell", StringComparison.Ordinal))
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
            CancellationToken cancellationToken)
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
            CancellationToken cancellationToken)
            => new FakeLlmClient(Provider).TuneAsync(
                promptTemplate, rawQuestion, environmentTag, routedQueryCode, modelKey, cancellationToken);

        public Task<string> GenerateAsync(
            string promptTemplate,
            string tunedQuestion,
            string environmentTag,
            string modelKey,
            CancellationToken cancellationToken)
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
            CancellationToken cancellationToken)
            => Task.FromResult("{}");
    }
}
