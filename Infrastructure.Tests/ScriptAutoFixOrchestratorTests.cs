using Application.Common.Interfaces;
using Application.Common.Models;
using Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Infrastructure.Tests;

public sealed class ScriptAutoFixOrchestratorTests
{
    [Fact]
    public async Task Sql_CodeFences_Are_Stripped_Before_Validation()
    {
        var sql = new FakeSqlExecutor((server, script, _) => Task.FromResult(Success(server, script)));
        var orchestrator = CreateOrchestrator(sql, new FakePowerShellExecutor(), new RecordingLlmClient());

        var response = await orchestrator.ExecuteAsync(
            new ScriptExecutionRequest
            {
                Environment = "SqlServer_Live",
                ScriptLanguage = "SQL",
                TunedQuestion = "list databases",
                SelectedServers = ["CTS03"],
                GeneratedScript = """
```sql
SELECT @@SERVERNAME AS [Server];
```
"""
            },
            CancellationToken.None);

        // Code fences are stripped by SafetyScanner.StripCodeFences before validation
        Assert.NotNull(response.FinalScript);
        Assert.DoesNotContain("```", response.FinalScript, StringComparison.Ordinal);
        Assert.Contains("SELECT @@SERVERNAME AS [Server];", response.FinalScript, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("SUCCESS", response.ResultsByServer[0].Status);
    }

    [Fact]
    public async Task Validation_Failure_Triggers_LLM_Repair()
    {
        // Validation fails on first attempt, succeeds after repair
        var validationCallCount = 0;
        var validator = new FakeValidationService((script, target) =>
        {
            validationCallCount++;
            if (validationCallCount == 1)
                return Task.FromResult(ScriptValidationResult.SyntaxError(
                    "Number=207; State=1; Line=3; Message=Invalid column name 'BadColumn'.", 207, 3));
            return Task.FromResult(ScriptValidationResult.Success());
        });

        var sql = new FakeSqlExecutor((server, script, _) => Task.FromResult(Success(server, script)));
        var llm = new RecordingLlmClient();
        var orchestrator = CreateOrchestrator(sql, new FakePowerShellExecutor(), llm, validator);

        var response = await orchestrator.ExecuteAsync(
            new ScriptExecutionRequest
            {
                Environment = "SqlServer_Live",
                ScriptLanguage = "SQL",
                TunedQuestion = "list databases",
                SelectedServers = ["CTS03"],
                GeneratedScript = "SELECT BadColumn FROM sys.databases;"
            },
            CancellationToken.None);

        // Two validation attempts: first fails, repair called, second passes
        Assert.Equal(2, response.Attempts.Count);
        Assert.Equal("VALIDATE", response.Attempts[0].Phase);
        Assert.Equal("FAILED", response.Attempts[0].Status);
        Assert.Equal("SYNTAX", response.Attempts[0].ErrorType);
        Assert.Equal("VALIDATE", response.Attempts[1].Phase);
        Assert.Equal("SUCCESS", response.Attempts[1].Status);
        Assert.True(response.Attempts[1].RepairedByLlm);

        // One LLM repair call
        Assert.Equal(1, llm.RepairCalls);

        // Execution happened
        Assert.NotNull(response.FinalScript);
        Assert.Single(response.ResultsByServer);
        Assert.Equal("SUCCESS", response.ResultsByServer[0].Status);
    }

    [Fact]
    public async Task Invalid_Column_Triggers_Repair()
    {
        // Validation fails with invalid column on first 2 attempts, succeeds on 3rd
        var validationCallCount = 0;
        var validator = new FakeValidationService((script, target) =>
        {
            validationCallCount++;
            if (validationCallCount <= 2)
                return Task.FromResult(ScriptValidationResult.SyntaxError(
                    "Number=207; State=1; Line=5; Message=Invalid column name 'NonExistent'.", 207, 5));
            return Task.FromResult(ScriptValidationResult.Success());
        });

        var sql = new FakeSqlExecutor((server, script, _) => Task.FromResult(Success(server, script)));
        var llm = new RecordingLlmClient();
        var orchestrator = CreateOrchestrator(sql, new FakePowerShellExecutor(), llm, validator);

        var response = await orchestrator.ExecuteAsync(
            new ScriptExecutionRequest
            {
                Environment = "SqlServer_Live",
                ScriptLanguage = "SQL",
                TunedQuestion = "show backup status",
                SelectedServers = ["CTS03"],
                GeneratedScript = "SELECT NonExistent FROM sys.databases;"
            },
            CancellationToken.None);

        Assert.Equal(3, response.Attempts.Count);
        Assert.Equal(2, llm.RepairCalls);
        Assert.Equal("SUCCESS", response.Attempts[2].Status);
        Assert.NotNull(response.FinalScript);
    }

    [Fact]
    public async Task Connection_Error_Falls_Through_All_Servers_Then_Executes()
    {
        // All servers return connection errors during validation — no LLM repair.
        // Execution still proceeds on all servers (connection issue may be transient).
        var validator = new FakeValidationService((script, target) =>
            Task.FromResult(ScriptValidationResult.ConnectionError(
                "A network-related or instance-specific error occurred. error: 40")));

        var sql = new FakeSqlExecutor((server, script, _) => Task.FromResult(Success(server, script)));
        var llm = new RecordingLlmClient();
        var orchestrator = CreateOrchestrator(sql, new FakePowerShellExecutor(), llm, validator);

        var response = await orchestrator.ExecuteAsync(
            new ScriptExecutionRequest
            {
                Environment = "SqlServer_Live",
                ScriptLanguage = "SQL",
                TunedQuestion = "list databases",
                SelectedServers = ["CTS01", "CTS02", "CTS03"],
                GeneratedScript = "SELECT @@SERVERNAME AS [ServerName], GETDATE() AS [CapturedAt], name FROM sys.databases;"
            },
            CancellationToken.None);

        // No LLM repair for connection errors
        Assert.Equal(0, llm.RepairCalls);

        // Validation attempt recorded as CONNECTION (all servers tried)
        Assert.Single(response.Attempts);
        Assert.Equal("CONNECTION", response.Attempts[0].ErrorType);

        // All 3 targets still executed (connection may have been transient)
        Assert.Equal(3, response.ResultsByServer.Count);
        Assert.Equal(0, response.Summary.FailCount);
        Assert.Equal(3, response.Summary.SuccessCount);
    }

    [Fact]
    public async Task Connection_Error_On_First_Server_Falls_Through_To_Second_For_Validation()
    {
        // Server[0] has connection error, server[1] connects and validates successfully.
        // All servers still execute.
        var validator = new FakeValidationService((script, target) =>
        {
            if (string.Equals(target, "CTS01", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(ScriptValidationResult.ConnectionError(
                    "A network-related or instance-specific error occurred. error: 40"));
            return Task.FromResult(ScriptValidationResult.Success());
        });

        var sql = new FakeSqlExecutor((server, script, _) => Task.FromResult(Success(server, script)));
        var llm = new RecordingLlmClient();
        var orchestrator = CreateOrchestrator(sql, new FakePowerShellExecutor(), llm, validator);

        var response = await orchestrator.ExecuteAsync(
            new ScriptExecutionRequest
            {
                Environment = "SqlServer_Live",
                ScriptLanguage = "SQL",
                TunedQuestion = "list databases",
                SelectedServers = ["CTS01", "CTS02", "CTS03"],
                GeneratedScript = "SELECT @@SERVERNAME AS [ServerName], GETDATE() AS [CapturedAt], name FROM sys.databases;"
            },
            CancellationToken.None);

        // No LLM repair needed — validation passed on CTS02
        Assert.Equal(0, llm.RepairCalls);

        // Validation passed (on CTS02 after CTS01 connection error)
        Assert.Single(response.Attempts);
        Assert.Equal("SUCCESS", response.Attempts[0].Status);
        Assert.Equal("CTS02", response.Attempts[0].Target);

        // All 3 targets executed
        Assert.Equal(3, response.ResultsByServer.Count);
        Assert.Equal(3, response.Summary.SuccessCount);
    }

    [Fact]
    public async Task PS_Parse_Error_Triggers_Repair()
    {
        // PS validation fails once, succeeds after repair
        var psValidationCallCount = 0;
        var validator = new FakeValidationService(
            sqlHandler: null,
            psHandler: (script) =>
            {
                psValidationCallCount++;
                if (psValidationCallCount == 1)
                    return Task.FromResult(ScriptValidationResult.SyntaxError(
                        "ParserError: Unexpected token '}' in expression or statement."));
                return Task.FromResult(ScriptValidationResult.Success());
            });

        var ps = new FakePowerShellExecutor((server, script) => Task.FromResult(Success(server, script)));
        var llm = new RecordingLlmClient();
        var orchestrator = CreateOrchestrator(new FakeSqlExecutor(), ps, llm, validator);

        var response = await orchestrator.ExecuteAsync(
            new ScriptExecutionRequest
            {
                Environment = "Windows_Live",
                ScriptLanguage = "PS",
                TunedQuestion = "show disk space",
                SelectedServers = ["SRV01"],
                GeneratedScript = "param([string]$TargetServer)\n$Result = @(}\n$Result"
            },
            CancellationToken.None);

        Assert.Equal(2, response.Attempts.Count);
        Assert.Equal("SYNTAX", response.Attempts[0].ErrorType);
        Assert.Equal("SUCCESS", response.Attempts[1].Status);
        Assert.Equal(1, llm.RepairCalls);
        Assert.NotNull(response.FinalScript);
    }

    [Fact]
    public async Task Retry_Count_Never_Exceeds_Four_Attempts()
    {
        // All validations fail — should be initial + 3 repairs = 4 attempts total
        var validator = new FakeValidationService((script, target) =>
            Task.FromResult(ScriptValidationResult.SyntaxError(
                "Number=207; State=1; Line=3; Message=Invalid column name 'BadColumn'.", 207, 3)));

        var sql = new FakeSqlExecutor((server, _, _) => Task.FromResult(Success(server, "ignored")));
        var llm = new RecordingLlmClient();
        var orchestrator = CreateOrchestrator(sql, new FakePowerShellExecutor(), llm, validator);

        var response = await orchestrator.ExecuteAsync(
            new ScriptExecutionRequest
            {
                Environment = "SqlServer_Live",
                ScriptLanguage = "SQL",
                TunedQuestion = "list databases",
                SelectedServers = ["CTS03"],
                GeneratedScript = "SELECT BadColumn FROM sys.databases;"
            },
            CancellationToken.None);

        // 4 attempts: initial + 3 repairs
        Assert.Equal(4, response.Attempts.Count);
        Assert.Equal(3, llm.RepairCalls);
        Assert.Null(response.FinalScript);
    }

    [Fact]
    public async Task Unsafe_Script_Blocks_Execution()
    {
        var sql = new FakeSqlExecutor((server, script, _) => Task.FromResult(Success(server, script)));
        var orchestrator = CreateOrchestrator(sql, new FakePowerShellExecutor(), new RecordingLlmClient());

        var response = await orchestrator.ExecuteAsync(
            new ScriptExecutionRequest
            {
                Environment = "SqlServer_Live",
                ScriptLanguage = "SQL",
                TunedQuestion = "drop db",
                SelectedServers = ["CTS03"],
                GeneratedScript = "DROP DATABASE test;"
            },
            CancellationToken.None);

        Assert.Single(response.Attempts);
        Assert.Equal("BLOCKED", response.Attempts[0].Status);
        Assert.StartsWith("BLOCKED:DANGEROUS_COMMAND:", response.Attempts[0].Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(sql.Calls);
    }

    [Fact]
    public async Task Consolidated_Results_Are_Grouped_By_Server()
    {
        // Validation passes, execution has mixed results
        var sql = new FakeSqlExecutor((server, script, _) =>
        {
            if (string.Equals(server, "CTS02", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new ExecutorRunResult
                {
                    Server = server,
                    Success = false,
                    IsRetryableCompileError = false,
                    Error = "Timeout expired.",
                    DurationMs = 50
                });
            }

            return Task.FromResult(new ExecutorRunResult
            {
                Server = server,
                Success = true,
                DurationMs = 20,
                Rows =
                [
                    new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Server"] = server,
                        ["Name"] = "row1"
                    }
                ],
                RawOutput = script
            });
        });

        var orchestrator = CreateOrchestrator(sql, new FakePowerShellExecutor(), new RecordingLlmClient());

        var response = await orchestrator.ExecuteAsync(
            new ScriptExecutionRequest
            {
                Environment = "SqlServer_Live",
                ScriptLanguage = "SQL",
                TunedQuestion = "list all db",
                SelectedServers = ["CTS03", "CTS02", "CTS01"],
                GeneratedScript = "SELECT @@SERVERNAME AS [Server], name FROM sys.databases;"
            },
            CancellationToken.None);

        Assert.Equal(3, response.ResultsByServer.Count);
        Assert.Equal(2, response.Summary.SuccessCount);
        Assert.Equal(1, response.Summary.FailCount);
        Assert.Equal(3, response.Summary.TotalTargets);
        Assert.Contains(response.ResultsByServer, x => x.Server == "CTS03");
        Assert.Contains(response.ResultsByServer, x => x.Server == "CTS02" && x.Status == "FAILED");
        Assert.Contains(response.ResultsByServer, x => x.Server == "CTS01");
    }

    [Fact]
    public async Task Validation_Success_Emits_Progress_Events()
    {
        var validator = new FakeValidationService((script, target) =>
            Task.FromResult(ScriptValidationResult.Success()));

        var sql = new FakeSqlExecutor((server, script, _) => Task.FromResult(Success(server, script)));
        var progress = new RecordingProgressStream();
        var orchestrator = CreateOrchestrator(sql, new FakePowerShellExecutor(), new RecordingLlmClient(), validator);

        await orchestrator.ExecuteWithAutoFixAsync(
            new ScriptExecutionRequest
            {
                Environment = "SqlServer_Live",
                ScriptLanguage = "SQL",
                TunedQuestion = "list databases",
                SelectedServers = ["CTS03"],
                GeneratedScript = "SELECT @@SERVERNAME AS [ServerName], GETDATE() AS [CapturedAt], name FROM sys.databases;"
            },
            progress,
            CancellationToken.None);

        // Should have VALIDATE start/done and EXECUTE start/done events
        Assert.Contains(progress.Events, e => e.Type == "phase.start" && e.Phase == "VALIDATE");
        Assert.Contains(progress.Events, e => e.Type == "phase.done" && e.Phase == "VALIDATE");
        Assert.Contains(progress.Events, e => e.Type == "phase.start" && e.Phase == "EXECUTE");
        Assert.Contains(progress.Events, e => e.Type == "phase.done" && e.Phase == "EXECUTE");
    }

    [Fact]
    public async Task Repair_Emits_Repair_Phase_Events()
    {
        var validationCallCount = 0;
        var validator = new FakeValidationService((script, target) =>
        {
            validationCallCount++;
            if (validationCallCount == 1)
                return Task.FromResult(ScriptValidationResult.SyntaxError("Number=102; Syntax error"));
            return Task.FromResult(ScriptValidationResult.Success());
        });

        var sql = new FakeSqlExecutor((server, script, _) => Task.FromResult(Success(server, script)));
        var progress = new RecordingProgressStream();
        var llm = new RecordingLlmClient();
        var orchestrator = CreateOrchestrator(sql, new FakePowerShellExecutor(), llm, validator);

        await orchestrator.ExecuteWithAutoFixAsync(
            new ScriptExecutionRequest
            {
                Environment = "SqlServer_Live",
                ScriptLanguage = "SQL",
                TunedQuestion = "list databases",
                SelectedServers = ["CTS03"],
                GeneratedScript = "SELECT FROM sys.databases;"
            },
            progress,
            CancellationToken.None);

        // Should have REPAIR start/done events
        Assert.Contains(progress.Events, e => e.Type == "phase.start" && e.Phase == "REPAIR");
        Assert.Contains(progress.Events, e => e.Type == "phase.done" && e.Phase == "REPAIR");
    }

    // ── Factory ──────────────────────────────────────────────────────────────

    private static ScriptAutoFixOrchestrator CreateOrchestrator(
        ISqlExecutor sqlExecutor,
        IPowerShellExecutor powerShellExecutor,
        RecordingLlmClient llm,
        IScriptValidationService? validator = null)
    {
        // Default validator: always passes
        validator ??= new FakeValidationService((_, _) =>
            Task.FromResult(ScriptValidationResult.Success()));

        var repairService = new FakeRepairService(llm);

        return new ScriptAutoFixOrchestrator(
            new FakeModelSelector(),
            [llm],
            new ScriptSafetyScanner(),
            validator,
            repairService,
            sqlExecutor,
            powerShellExecutor,
            Options.Create(new ScriptExecutionOptions
            {
                CommandTimeoutSeconds = 30,
                MaxDegreeOfParallelism = 3
            }),
            NullLogger<ScriptAutoFixOrchestrator>.Instance);
    }

    private static ExecutorRunResult Success(string server, string script)
    {
        return new ExecutorRunResult
        {
            Server = server,
            Success = true,
            DurationMs = 10,
            Rows =
            [
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Server"] = server,
                    ["Script"] = script
                }
            ],
            RawOutput = script
        };
    }

    // ── Test Fakes ───────────────────────────────────────────────────────────

    private sealed class FakeValidationService : IScriptValidationService
    {
        private readonly Func<string, string, Task<ScriptValidationResult>>? _sqlHandler;
        private readonly Func<string, Task<ScriptValidationResult>>? _psHandler;

        public FakeValidationService(
            Func<string, string, Task<ScriptValidationResult>>? sqlHandler = null,
            Func<string, Task<ScriptValidationResult>>? psHandler = null)
        {
            _sqlHandler = sqlHandler;
            _psHandler = psHandler;
        }

        public Task<ScriptValidationResult> ValidateSqlAsync(string script, string firstTarget, CancellationToken ct = default)
            => _sqlHandler?.Invoke(script, firstTarget)
               ?? Task.FromResult(ScriptValidationResult.Success());

        public Task<ScriptValidationResult> ValidatePowerShellAsync(string script, CancellationToken ct = default)
            => _psHandler?.Invoke(script)
               ?? Task.FromResult(ScriptValidationResult.Success());
    }

    private sealed class FakeRepairService(RecordingLlmClient llm) : IScriptRepairService
    {
        public Task<string> RepairAsync(
            string environment, string tunedQuestion, string scriptLanguage,
            string failedScript, string errorMessage, CancellationToken ct = default)
        {
            llm.RepairCalls++;
            return Task.FromResult(
                "SELECT @@SERVERNAME AS [ServerName], GETDATE() AS [CapturedAt], name FROM sys.databases;");
        }
    }

    private sealed class FakeSqlExecutor : ISqlExecutor
    {
        private readonly Func<string, string, int, Task<ExecutorRunResult>>? _behavior;
        private int _callIndex;

        public FakeSqlExecutor(Func<string, string, int, Task<ExecutorRunResult>>? behavior = null)
        {
            _behavior = behavior;
        }

        public List<(string Server, string Script)> Calls { get; } = [];

        public Task<ExecutorRunResult> ExecuteAsync(string server, string script, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            Calls.Add((server, script));
            _callIndex++;
            return _behavior?.Invoke(server, script, _callIndex)
                   ?? Task.FromResult(new ExecutorRunResult
                   {
                       Server = server,
                       Success = true,
                       DurationMs = 10,
                       Rows = []
                   });
        }
    }

    private sealed class FakePowerShellExecutor : IPowerShellExecutor
    {
        private readonly Func<string, string, Task<ExecutorRunResult>>? _behavior;

        public FakePowerShellExecutor(Func<string, string, Task<ExecutorRunResult>>? behavior = null)
        {
            _behavior = behavior;
        }

        public Task<ExecutorRunResult> ExecuteAsync(string server, string script, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return _behavior?.Invoke(server, script)
                   ?? Task.FromResult(new ExecutorRunResult
                   {
                       Server = server,
                       Success = true,
                       DurationMs = 10,
                       Rows = []
                   });
        }
    }

    private sealed class FakeModelSelector : IModelSelector
    {
        public LlmModelDefinition SelectTuneModel() => Base("Gemini", "tune");
        public LlmModelDefinition SelectPlanModel() => Base("Gemini", "plan");
        public LlmModelDefinition SelectTemplateFindModel() => Base("OpenAI", "unused-template");
        public LlmModelDefinition SelectValidateModel() => Base("OpenAI", "unused-validate");
        public LlmModelDefinition SelectGenerateModel() => Base("OpenAI", "gpt-5-mini");
        public LlmModelDefinition SelectExplainModel() => Base("Gemini", "tune");

        private static LlmModelDefinition Base(string provider, string key) =>
            new()
            {
                ModelId = 1,
                DisplayName = key,
                Provider = provider,
                ModelKey = key,
                Generator = 1
            };
    }

    internal sealed class RecordingLlmClient : ILLMClient
    {
        public string Provider => "OpenAI";
        public int RepairCalls { get; set; }

        public Task<string> TuneAsync(
            string promptTemplate, string rawQuestion, string environmentTag,
            string routedQueryCode, string modelKey, CancellationToken cancellationToken,
            string? apiKey = null)
            => Task.FromResult(string.Empty);

        public Task<string> GenerateAsync(
            string promptTemplate, string tunedQuestion, string environmentTag,
            string modelKey, CancellationToken cancellationToken,
            string? apiKey = null)
        {
            if (promptTemplate.Contains("DataBot Script Repair", StringComparison.OrdinalIgnoreCase))
            {
                RepairCalls++;
                return Task.FromResult(
                    "SELECT @@SERVERNAME AS [ServerName], GETDATE() AS [CapturedAt], name FROM sys.databases;");
            }

            if (promptTemplate.Contains("DataBot Script Fixer", StringComparison.OrdinalIgnoreCase))
            {
                RepairCalls++;
                return Task.FromResult(
                    "SELECT @@SERVERNAME AS [ServerName], GETDATE() AS [CapturedAt], name FROM sys.databases;");
            }

            if (promptTemplate.Contains("strict script generator", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(
                    "SELECT @@SERVERNAME AS [ServerName], GETDATE() AS [CapturedAt], name FROM sys.databases;");

            return Task.FromResult(string.Empty);
        }

        public Task<string> ValidateTemplateAsync(
            string promptTemplate, string tunedQuestion, string environmentTag,
            string modelKey, CancellationToken cancellationToken,
            string? apiKey = null)
            => Task.FromResult(string.Empty);
    }

    private sealed class RecordingProgressStream : IProgressStream
    {
        public List<ProgressEvent> Events { get; } = [];

        public ValueTask EmitAsync(ProgressEvent e, CancellationToken ct = default)
        {
            Events.Add(e);
            return ValueTask.CompletedTask;
        }
    }
}
