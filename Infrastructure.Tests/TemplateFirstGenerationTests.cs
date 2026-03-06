using Application.Common.Interfaces;
using Application.Common.Models;
using Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Tests for Generator==2 template-first path (ToolRegistry lookup → LLM fallback).
/// </summary>
public sealed class TemplateFirstGenerationTests
{
    // ──────────────────────────────────────────────────────────────────────────
    // Generator == 2, SQL template hit
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Generator2_SqlServer_TemplateHit_Source_Is_Template()
    {
        var service = CreateService(resolver: new HitResolver("SQL_AGENT_JOBS_UNIFIED", "SQL",
            "SELECT @@SERVERNAME AS [ServerName], GETDATE() AS [CapturedAt], j.[name] AS [JobName] FROM msdb.dbo.sysjobs AS j ORDER BY j.[name]"));

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "t1",
                BearerToken = "bt1",
                Environment = "SqlServer_Live",
                Question = "show sql agent job history last 2 hours",
                SelectedTargets = ["SQL01"]
            },
            CancellationToken.None);

        Assert.Equal("TEMPLATE_OR_LLM", response.Plan.GeneratorMode);
        Assert.True(response.Plan.TemplateHit);
        Assert.Equal("SQL_AGENT_JOBS_UNIFIED", response.Plan.QueryCode);
        Assert.NotNull(response.Script);
        Assert.Equal("TEMPLATE", response.Script!.Source);
    }

    [Fact]
    public async Task Generator2_SqlServer_TemplateHit_Executes_And_Returns_Success()
    {
        var service = CreateService(resolver: new HitResolver("SQL_AGENT_JOBS_UNIFIED", "SQL",
            "SELECT @@SERVERNAME AS [ServerName], GETDATE() AS [CapturedAt], j.[name] AS [JobName] FROM msdb.dbo.sysjobs AS j ORDER BY j.[name]"));

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "t2",
                BearerToken = "bt2",
                Environment = "SqlServer_Live",
                Question = "list sql agent jobs",
                SelectedTargets = ["SQL01", "SQL02"]
            },
            CancellationToken.None);

        Assert.Equal("EXECUTION", response.Result.Kind);
        Assert.Equal("SUCCESS", response.Result.Status);
        Assert.Equal(2, response.Result.Items!.Count);
    }

    [Fact]
    public async Task Generator2_SqlServer_TemplateHit_BoundParameters_Exposed()
    {
        var boundParams = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Minutes"] = 120,
            ["OnlyFailed"] = 1
        };

        var service = CreateService(resolver: new HitResolver("SQL_AGENT_JOBS_UNIFIED", "SQL",
            "SELECT @@SERVERNAME AS [ServerName], GETDATE() AS [CapturedAt], j.[name] AS [JobName] FROM msdb.dbo.sysjobs AS j",
            boundParams: boundParams));

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "t3",
                BearerToken = "bt3",
                Environment = "SqlServer_Live",
                Question = "show sql agent job history last 2 hours",
                SelectedTargets = ["SQL01"]
            },
            CancellationToken.None);

        Assert.NotNull(response.Script?.Parameters);
        Assert.Equal(120, response.Script!.Parameters!["Minutes"]);
        Assert.Equal(1, response.Script.Parameters["OnlyFailed"]);
    }

    [Fact]
    public async Task Generator2_SqlServer_TemplateMiss_FallsBack_To_Llm()
    {
        // StubToolRegistryResolver always returns Found=false
        var service = CreateService(resolver: new MissResolver());

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "t4",
                BearerToken = "bt4",
                Environment = "SqlServer_Live",
                Question = "list all failed jobs",
                SelectedTargets = ["SQL01"]
            },
            CancellationToken.None);

        Assert.Equal("TEMPLATE_OR_LLM", response.Plan.GeneratorMode);
        Assert.False(response.Plan.TemplateHit);
        Assert.Equal("EXECUTION", response.Result.Kind);
        Assert.NotNull(response.Script);
        Assert.Equal("LLM", response.Script!.Source);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Generator == 2, Windows template hit
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Generator2_Windows_TemplateHit_Source_Is_Template()
    {
        var service = CreateService(resolver: new HitResolver("WIN_DISK_DRIVES_UNIFIED", "PS",
            "param([string]$TargetServer)\n$Result = Get-PSDrive | Select-Object Name, Used, Free\n$Result"));

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "t5",
                BearerToken = "bt5",
                Environment = "Windows_Live",
                Question = "list disks below 10% free",
                SelectedTargets = ["WEB01"]
            },
            CancellationToken.None);

        Assert.Equal("TEMPLATE_OR_LLM", response.Plan.GeneratorMode);
        Assert.True(response.Plan.TemplateHit);
        Assert.Equal("WIN_DISK_DRIVES_UNIFIED", response.Plan.QueryCode);
        Assert.Equal("TEMPLATE", response.Script!.Source);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Generator == 2, render failure → LLM fallback
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Generator2_RenderFailure_FallsBack_To_Llm()
    {
        // Template hit but with UNRESOLVED placeholder so render returns Success=false
        var service = CreateService(resolver: new HitResolver("SQL_AGENT_JOBS_UNIFIED", "SQL",
            "SELECT @@SERVERNAME AS [ServerName], GETDATE() AS [CapturedAt], {{UnresolvedParam}} FROM sys.databases"));

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "t6",
                BearerToken = "bt6",
                Environment = "SqlServer_Live",
                Question = "list sql agent jobs",
                SelectedTargets = ["SQL01"]
            },
            CancellationToken.None);

        // Falls back to LLM — source must be LLM, not TEMPLATE
        Assert.Equal("TEMPLATE_OR_LLM", response.Plan.GeneratorMode);
        Assert.False(response.Plan.TemplateHit);
        Assert.Equal("LLM", response.Script?.Source);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Generator == 1 → template resolver is never called
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Generator1_Always_Uses_Llm_Never_Calls_Resolver()
    {
        var trackingResolver = new TrackingResolver();
        var service = CreateService(
            resolver: trackingResolver,
            modelSelector: new Generator1ModelSelector());

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "t7",
                BearerToken = "bt7",
                Environment = "SqlServer_Live",
                Question = "list all failed jobs",
                SelectedTargets = ["SQL01"]
            },
            CancellationToken.None);

        Assert.False(trackingResolver.WasCalled, "ToolRegistry resolver must NOT be called when Generator==1.");
        Assert.Equal("LLM_ONLY", response.Plan.GeneratorMode);
        Assert.Equal("LLM", response.Script?.Source);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Generator == 2, template blocked token → STOPPED
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Generator2_TemplateHit_BlockedToken_Returns_Stopped()
    {
        var service = CreateService(resolver: new HitResolver("EVIL_TOOL", "SQL",
            "DROP TABLE sys.databases; SELECT @@SERVERNAME AS [ServerName], GETDATE() AS [CapturedAt]"));

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "t8",
                BearerToken = "bt8",
                Environment = "SqlServer_Live",
                Question = "list databases",
                SelectedTargets = ["SQL01"]
            },
            CancellationToken.None);

        Assert.Equal("STOPPED", response.Result.Status);
        Assert.Null(response.Script);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    private static AskPipelineService CreateService(
        IToolRegistryResolver? resolver = null,
        IModelSelector? modelSelector = null)
    {
        var selector = modelSelector ?? new Generator2ModelSelector();
        ILLMClient[] clients =
        [
            new FakeGen2LlmClient("Gemini"),
            new FakeGen2LlmClient("OpenAI")
        ];

        return new AskPipelineService(
            selector,
            clients,
            new FakeOrchestrator(),
            resolver ?? new MissResolver(),
            new TemplateRenderer(NullLogger<TemplateRenderer>.Instance),
            new AllowAllPolicyService(),
            NullLogger<AskPipelineService>.Instance);
    }

    // ── Model selectors ───────────────────────────────────────────────────────

    /// <summary>Returns Generator=2 for all model selections.</summary>
    private sealed class Generator2ModelSelector : IModelSelector
    {
        private static LlmModelDefinition Make(string provider, string key) =>
            new() { ModelId = 1, DisplayName = "Test", ModelKey = key, Provider = provider, Generator = 2, IsEnabled = true };

        public LlmModelDefinition SelectTuneModel() => Make("Gemini", "test-tune");
        public LlmModelDefinition SelectPlanModel() => Make("Gemini", "test-plan");
        public LlmModelDefinition SelectTemplateFindModel() => Make("Gemini", "test-tf");
        public LlmModelDefinition SelectValidateModel() => Make("Gemini", "test-val");
        public LlmModelDefinition SelectGenerateModel() => Make("Gemini", "test-gen");
        public LlmModelDefinition SelectExplainModel() => Make("Gemini", "test-exp");
    }

    /// <summary>Returns Generator=1 for GenerateModel; LLM-only path.</summary>
    private sealed class Generator1ModelSelector : IModelSelector
    {
        private static LlmModelDefinition Make(string provider, string key, int? gen = null) =>
            new() { ModelId = 1, DisplayName = "Test", ModelKey = key, Provider = provider, Generator = gen, IsEnabled = true };

        public LlmModelDefinition SelectTuneModel() => Make("Gemini", "test-tune");
        public LlmModelDefinition SelectPlanModel() => Make("Gemini", "test-plan");
        public LlmModelDefinition SelectTemplateFindModel() => Make("Gemini", "test-tf");
        public LlmModelDefinition SelectValidateModel() => Make("Gemini", "test-val");
        public LlmModelDefinition SelectGenerateModel() => Make("Gemini", "test-gen", gen: 1);
        public LlmModelDefinition SelectExplainModel() => Make("Gemini", "test-exp");
    }

    // ── Resolvers ────────────────────────────────────────────────────────────

    /// <summary>Always returns Found=false (template miss).</summary>
    private sealed class MissResolver : IToolRegistryResolver
    {
        public Task<ToolResolutionResult> ResolveBestToolAsync(
            string environment, string tunedQuestion, CancellationToken cancellationToken) =>
            Task.FromResult(new ToolResolutionResult { Found = false, SelectionMethod = "TEST_MISS" });
    }

    /// <summary>Returns a single template hit with configurable script and optional bound params.</summary>
    private sealed class HitResolver(
        string queryCode,
        string scriptLanguage,
        string scriptTemplate,
        Dictionary<string, object?>? boundParams = null) : IToolRegistryResolver
    {
        public Task<ToolResolutionResult> ResolveBestToolAsync(
            string environment, string tunedQuestion, CancellationToken cancellationToken) =>
            Task.FromResult(new ToolResolutionResult
            {
                Found = true,
                ToolId = 99,
                QueryCode = queryCode,
                ToolName = "Test Tool",
                Environment = environment,
                ScriptLanguage = scriptLanguage,
                // Embed bound params in template using a wrapper that pre-fills them
                ScriptTemplate = scriptTemplate,
                ParameterSchema = BuildSchema(boundParams),
                Score = 1500,
                Confidence = 0.99,
                SelectionMethod = "TEST_HIT"
            });

        private static string? BuildSchema(Dictionary<string, object?>? bp)
        {
            if (bp is null || bp.Count == 0)
                return """{"parameters":[],"safety":{"readOnly":true}}""";

            var paramDefs = string.Join(",", bp.Select(kv =>
            {
                var type = kv.Value is int ? "int" : "string";
                var defaultVal = kv.Value is int iv ? iv.ToString() : $"\"{kv.Value}\"";
                return $$$"""{"name":"{{{kv.Key}}}","type":"{{{type}}}","required":false,"default":{{{defaultVal}}}}""";
            }));

            return $$$"""{"parameters":[{{{paramDefs}}}],"safety":{"readOnly":true}}""";
        }
    }

    /// <summary>Tracks whether ResolveBestToolAsync was called.</summary>
    private sealed class TrackingResolver : IToolRegistryResolver
    {
        public bool WasCalled { get; private set; }

        public Task<ToolResolutionResult> ResolveBestToolAsync(
            string environment, string tunedQuestion, CancellationToken cancellationToken)
        {
            WasCalled = true;
            return Task.FromResult(new ToolResolutionResult { Found = false, SelectionMethod = "TRACKING_MISS" });
        }
    }

    // ── LLM client ───────────────────────────────────────────────────────────

    private sealed class FakeGen2LlmClient(string provider) : ILLMClient
    {
        public string Provider { get; } = provider;

        public Task<string> TuneAsync(
            string promptTemplate, string rawQuestion, string environmentTag,
            string routedQueryCode, string modelKey, CancellationToken cancellationToken)
        {
            if (rawQuestion.Contains("restart server", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult($"BLOCKED: STATE_CHANGING_REQUEST||{routedQueryCode}");

            var tuned = rawQuestion.Trim();
            if (!tuned.EndsWith(".", StringComparison.Ordinal))
                tuned += ".";
            return Task.FromResult($"{tuned}||{routedQueryCode}");
        }

        public Task<string> GenerateAsync(
            string promptTemplate, string tunedQuestion, string environmentTag,
            string modelKey, CancellationToken cancellationToken)
        {
            if (promptTemplate.Contains("DataBot Explain", StringComparison.Ordinal))
                return Task.FromResult("""{"explanation":"Done.","anomaly":null,"analysis":null,"suggestion":null}""");

            if (environmentTag.Equals("SqlServer_Live", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult("""
SELECT @@SERVERNAME AS [ServerName], GETDATE() AS [CapturedAt], j.[name] AS [JobName]
FROM msdb.dbo.sysjobs AS j
ORDER BY j.[name];
""");

            // Windows / PS
            return Task.FromResult("""
param([string]$TargetServer)
$Result = @([PSCustomObject]@{ ServerName=$TargetServer; CapturedAt=Get-Date; Status='OK'; ErrorMessage=$null })
$Result
""");
        }

        public Task<string> ValidateTemplateAsync(
            string promptTemplate, string tunedQuestion, string environmentTag,
            string modelKey, CancellationToken cancellationToken) =>
            Task.FromResult("{}");
    }

    // ── Policy ─────────────────────────────────────────────────────────────

    private sealed class AllowAllPolicyService : IRequestPolicyService
    {
        public PolicyDecision Evaluate(AskApiRequest request, string tunedQuestionOrRaw)
            => PolicyDecision.Allow();
    }

    // ── Orchestrator ─────────────────────────────────────────────────────────

    private sealed class FakeOrchestrator : IScriptAutoFixOrchestrator
    {
        public Task<ScriptExecutionResponse> ExecuteAsync(
            ScriptExecutionRequest request, CancellationToken cancellationToken) =>
            ExecuteWithAutoFixAsync(request, cancellationToken);

        public Task<ScriptExecutionResponse> ExecuteWithAutoFixAsync(
            ScriptExecutionRequest request, CancellationToken cancellationToken) =>
            ExecuteWithAutoFixAsync(request, null, cancellationToken);

        public Task<ScriptExecutionResponse> ExecuteWithAutoFixAsync(
            ScriptExecutionRequest request, IProgressStream? progress, CancellationToken cancellationToken)
        {
            var results = request.SelectedServers
                .Select(server => new ScriptExecutionServerResult
                {
                    Server = server,
                    Status = "SUCCESS",
                    RowCount = 1,
                    DurationMs = 5,
                    Rows =
                    [
                        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["ServerName"] = server,
                            ["CapturedAt"] = DateTime.UtcNow,
                            ["JobName"] = "TestJob"
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
                    SuccessCount = results.Count,
                    FailCount = 0,
                    TotalRowCount = results.Count,
                    TotalTargets = request.SelectedServers.Length
                }
            });
        }
    }
}
