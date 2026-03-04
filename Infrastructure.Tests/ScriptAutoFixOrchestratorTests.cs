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
    public async Task Sql_CodeFences_Are_Stripped_Before_Execution()
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

        Assert.Single(sql.Calls);
        Assert.DoesNotContain("```", sql.Calls[0].Script, StringComparison.Ordinal);
        Assert.Contains("SELECT @@SERVERNAME AS [Server];", sql.Calls[0].Script, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("SUCCESS", response.ResultsByServer[0].Status);
    }

    [Fact]
    public async Task First_Target_Failure_Triggers_Fix_Prompt()
    {
        var sql = new FakeSqlExecutor((server, script, callIndex) =>
        {
            if (callIndex == 1)
            {
                return Task.FromResult(new ExecutorRunResult
                {
                    Server = server,
                    Success = false,
                    IsRetryableCompileError = true,
                    Error = "Number=102; State=1; Line=1; Message=Incorrect syntax near 'FROM'.",
                    DurationMs = 10
                });
            }

            return Task.FromResult(Success(server, script));
        });

        var llm = new RecordingLlmClient();
        var orchestrator = CreateOrchestrator(sql, new FakePowerShellExecutor(), llm);

        var response = await orchestrator.ExecuteAsync(
            new ScriptExecutionRequest
            {
                Environment = "SqlServer_Live",
                ScriptLanguage = "SQL",
                TunedQuestion = "list databases",
                SelectedServers = ["CTS03"],
                GeneratedScript = "SELECT FROM sys.databases;"
            },
            CancellationToken.None);

        Assert.Equal(2, response.Attempts.Count);
        Assert.Equal(1, llm.FixPromptCalls);
        Assert.Equal("SUCCESS", response.Attempts[1].Status);
    }

    [Fact]
    public async Task Retry_Count_Never_Exceeds_Three()
    {
        var sql = new FakeSqlExecutor((server, _, _) => Task.FromResult(new ExecutorRunResult
        {
            Server = server,
            Success = false,
            IsRetryableCompileError = true,
            Error = "Number=207; State=1; Line=3; Message=Invalid column name 'BadColumn'.",
            DurationMs = 10
        }));

        var llm = new RecordingLlmClient();
        var orchestrator = CreateOrchestrator(sql, new FakePowerShellExecutor(), llm);

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

        Assert.Equal(3, response.Attempts.Count);
        Assert.Equal(3, sql.Calls.Count);
        Assert.Equal(2, llm.GenerateCalls);
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

    private static ScriptAutoFixOrchestrator CreateOrchestrator(
        ISqlExecutor sqlExecutor,
        IPowerShellExecutor powerShellExecutor,
        RecordingLlmClient llm)
    {
        return new ScriptAutoFixOrchestrator(
            new FakeModelSelector(),
            [llm],
            new ScriptSafetyScanner(),
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

    private sealed class FakeSqlExecutor(Func<string, string, int, Task<ExecutorRunResult>> behavior) : ISqlExecutor
    {
        private readonly Func<string, string, int, Task<ExecutorRunResult>> _behavior = behavior;
        private int _callIndex;

        public List<(string Server, string Script)> Calls { get; } = [];

        public Task<ExecutorRunResult> ExecuteAsync(string server, string script, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            Calls.Add((server, script));
            _callIndex++;
            return _behavior(server, script, _callIndex);
        }
    }

    private sealed class FakePowerShellExecutor : IPowerShellExecutor
    {
        public Task<ExecutorRunResult> ExecuteAsync(string server, string script, CancellationToken cancellationToken)
        {
            _ = script;
            _ = cancellationToken;
            return Task.FromResult(new ExecutorRunResult
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

        private static LlmModelDefinition Base(string provider, string key) =>
            new()
            {
                ModelId = 1,
                DisplayName = key,
                Provider = provider,
                ModelKey = key,
                UseForGenerate = true
            };
    }

    private sealed class RecordingLlmClient : ILLMClient
    {
        public string Provider => "OpenAI";
        public int GenerateCalls { get; private set; }
        public int FixPromptCalls { get; private set; }

        public Task<string> TuneAsync(
            string promptTemplate,
            string rawQuestion,
            string environmentTag,
            string routedQueryCode,
            string modelKey,
            CancellationToken cancellationToken)
        {
            _ = promptTemplate;
            _ = rawQuestion;
            _ = environmentTag;
            _ = routedQueryCode;
            _ = modelKey;
            _ = cancellationToken;
            return Task.FromResult(string.Empty);
        }

        public Task<string> GenerateAsync(
            string promptTemplate,
            string tunedQuestion,
            string environmentTag,
            string modelKey,
            CancellationToken cancellationToken)
        {
            _ = tunedQuestion;
            _ = environmentTag;
            _ = modelKey;
            _ = cancellationToken;

            GenerateCalls++;

            if (promptTemplate.Contains("strict script patcher", StringComparison.OrdinalIgnoreCase))
            {
                FixPromptCalls++;
                return Task.FromResult("SELECT @@SERVERNAME AS [Server], name FROM sys.databases;");
            }

            if (promptTemplate.Contains("strict script generator", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult("SELECT @@SERVERNAME AS [Server], name FROM sys.databases;");

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
            return Task.FromResult(string.Empty);
        }
    }
}
