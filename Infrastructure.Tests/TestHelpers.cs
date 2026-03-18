using Application.Common.Interfaces;
using Application.Common.Models;
using Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.Tests;

/// <summary>Shared test fakes for AskPipelineService tests.</summary>
internal static class TestHelpers
{
    internal static AskPipelineService CreateStandardService()
    {
        var modelSelector = new SharedFakeModelSelector();
        ILLMClient[] clients =
        [
            new SharedFakeLlmClient("Gemini"),
            new SharedFakeLlmClient("OpenAI")
        ];

        return new AskPipelineService(
            modelSelector,
            clients,
            new SharedFakeOrchestrator(),
            new StubToolRegistryResolver(NullLogger<StubToolRegistryResolver>.Instance),
            new TemplateRenderer(NullLogger<TemplateRenderer>.Instance),
            new SharedAllowAllPolicy(),
            new SharedStubSamplesRepo(),
            new EmptyUserServerRepository(),
            NullLogger<AskPipelineService>.Instance);
    }

    internal sealed class SharedAllowAllPolicy : IRequestPolicyService
    {
        public PolicyDecision Evaluate(AskApiRequest request, string tunedQuestionOrRaw)
            => PolicyDecision.Allow();
    }

    internal sealed class SharedFakeOrchestrator : IScriptAutoFixOrchestrator
    {
        public Task<ScriptExecutionResponse> ExecuteAsync(
            ScriptExecutionRequest request, CancellationToken ct)
            => ExecuteWithAutoFixAsync(request, null, ct);

        public Task<ScriptExecutionResponse> ExecuteWithAutoFixAsync(
            ScriptExecutionRequest request, CancellationToken ct)
            => ExecuteWithAutoFixAsync(request, null, ct);

        public Task<ScriptExecutionResponse> ExecuteWithAutoFixAsync(
            ScriptExecutionRequest request, IProgressStream? progress, CancellationToken ct)
        {
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

    internal sealed class SharedFakeModelSelector : IModelSelector
    {
        public LlmModelDefinition SelectTuneModel() => new()
        {
            ModelId = 1, DisplayName = "Tune", ModelKey = "gemini-2.5-flash-lite",
            Provider = "Gemini", UseForTune = true
        };

        public LlmModelDefinition SelectPlanModel() => new()
        {
            ModelId = 1, DisplayName = "Plan", ModelKey = "gemini-2.5-flash-lite",
            Provider = "Gemini", UseForTune = true
        };

        public LlmModelDefinition SelectTemplateFindModel() => new()
        {
            ModelId = 2, DisplayName = "TemplateFind", ModelKey = "gpt-5-mini", Provider = "OpenAI"
        };

        public LlmModelDefinition SelectValidateModel() => new()
        {
            ModelId = 2, DisplayName = "Validate", ModelKey = "gpt-5-mini", Provider = "OpenAI"
        };

        public LlmModelDefinition SelectGenerateModel() => new()
        {
            ModelId = 2, DisplayName = "Generate", ModelKey = "gpt-5-mini",
            Provider = "OpenAI", Generator = 1
        };

        public LlmModelDefinition SelectExplainModel() => new()
        {
            ModelId = 1, DisplayName = "Explain", ModelKey = "gemini-2.5-flash-lite",
            Provider = "Gemini", UseForExplain = true
        };
    }

    internal sealed class SharedFakeLlmClient(string provider) : ILLMClient
    {
        public string Provider { get; } = provider;

        public Task<string> TuneAsync(
            string promptTemplate, string rawQuestion, string environmentTag,
            string routedQueryCode, string modelKey, CancellationToken ct,
            string? apiKey = null)
        {
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
            string promptTemplate, string tunedQuestion, string environmentTag,
            string modelKey, CancellationToken ct,
            string? apiKey = null)
        {
            if (promptTemplate.Contains("DataBot Explain", StringComparison.Ordinal))
            {
                return Task.FromResult("""
{"title":"Execution Results Summary","explanation":"All targets executed successfully. No anomalies or errors detected across the monitored servers.","anomaly":null,"analysis":"All values are within normal operating ranges across all targets.","suggestion":null,"rootCause":null,"impact":null,"summary":["All targets executed successfully with no errors.","No anomalies detected in the collected data.","All monitored values are within normal ranges.","No immediate action required."],"keyMetrics":[{"label":"Execution Status","value":"Success","unit":null,"status":"ok"},{"label":"Targets Queried","value":"1","unit":"count","status":"ok"}],"recommendations":[{"text":"Continue routine monitoring. All values appear within normal ranges.","priority":"low"}],"comparison":null,"sections":[{"key":"findings","title":"Key Findings","icon":"Search","tone":"ok","bullets":["All targets returned data successfully.","No errors or failures detected during execution.","Query results are complete and consistent."]},{"key":"server_breakdown","title":"Per-Server Breakdown","icon":"Database","tone":"info","bullets":["All queried servers responded within expected timeframes.","Data collection completed across all targets."]},{"key":"health_check","title":"Health Status","icon":"ShieldCheck","tone":"ok","bullets":["All monitored values are within normal operating ranges.","No thresholds exceeded on any target."]},{"key":"action_items","title":"Next Steps","icon":"Lightbulb","tone":"ok","steps":["Review the detailed data in the results panel.","Schedule follow-up checks if monitoring specific trends.","Adjust query parameters for more targeted analysis."]}]}
""");
            }

            if (environmentTag.Equals("General", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult("The Moon is Earth's natural satellite.");

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

            // Delegate to MockLlmBehavior for script generation
            return Task.FromResult(MockLlmBehavior.BuildGenerateScript(environmentTag, tunedQuestion));
        }

        public Task<string> ValidateTemplateAsync(
            string promptTemplate, string tunedQuestion, string environmentTag,
            string modelKey, CancellationToken ct,
            string? apiKey = null)
            => Task.FromResult(MockLlmBehavior.BuildValidateTemplateJson(environmentTag, tunedQuestion, promptTemplate));
    }

    internal sealed class SharedStubSamplesRepo : IQuestionSamplesRepository
    {
        public Task<List<QuestionSampleRow>> GetByEnvironmentAsync(
            string environment, CancellationToken ct) => Task.FromResult(new List<QuestionSampleRow>());

        public Task<QuestionSampleRow?> GetByIdAsync(
            int sampleId, string environment, CancellationToken ct) => Task.FromResult<QuestionSampleRow?>(null);

        public Task<QuestionSampleRow?> GetByGroupKeyAsync(
            string groupKey, string environment, CancellationToken ct) => Task.FromResult<QuestionSampleRow?>(null);
    }

    /// <summary>Stub that returns empty server lists — ports don't matter in tests.</summary>
    internal sealed class EmptyUserServerRepository : IUserServerRepository
    {
        public Task<List<UserServerEntry>> GetSqlServersAsync(string userId, CancellationToken ct)
            => Task.FromResult(new List<UserServerEntry>());

        public Task<List<UserServerEntry>> GetWinServersAsync(string userId, CancellationToken ct)
            => Task.FromResult(new List<UserServerEntry>());
    }
}
