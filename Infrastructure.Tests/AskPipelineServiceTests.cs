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
                UserId = "u1",
                Environment = "General",
                Question = "list all moon",
                SelectedServers = []
            },
            CancellationToken.None);

        Assert.Equal("GENERAL_TUNE_ONLY", response.Resolution);
        Assert.Null(response.Script);
        Assert.Null(response.ScriptLanguage);
        Assert.Null(response.QueryCode);
        Assert.Null(response.ScriptTemplate);
        Assert.Null(response.RenderedScript);
        Assert.Equal("GENERAL", response.ExecutionPayload?.ExecutionMode);
        Assert.Equal("ANSWER_ONLY", response.Result.Kind);
        Assert.NotNull(response.Result.AnswerText);
    }

    [Fact]
    public async Task SqlServerLive_ListAllDatabase_GreaterThan10Gb_Returns_Size_Filter_Script()
    {
        var service = CreateService();

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "s1",
                UserId = "u2",
                Environment = "SqlServer_Live",
                Question = "list all database with >10GB",
                SelectedServers = ["SQL01"]
            },
            CancellationToken.None);

        Assert.Equal("EXECUTION_READY", response.Resolution);
        Assert.Equal("EXECUTION", response.Result.Kind);
        Assert.Equal("NOT_EXECUTED", response.Result.Execution?.Status);
        Assert.Equal("SQL", response.ScriptLanguage);
        Assert.NotNull(response.Script);
        Assert.Contains("sys.master_files", response.Script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WITH db_size", response.Script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("> 10", response.Script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SqlServerLive_DropDatabaseTest_Returns_Stopped_Blocked()
    {
        var service = CreateService();

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "s2",
                UserId = "u3",
                Environment = "SqlServer_Live",
                Question = "drop database test",
                SelectedServers = ["SQL01"]
            },
            CancellationToken.None);

        Assert.Equal("STOPPED", response.Resolution);
        Assert.StartsWith("BLOCKED:DANGEROUS_COMMAND:", response.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Null(response.Script);
    }

    [Fact]
    public async Task WindowsLive_ListServicesStopped_Returns_Stopped_Service_Script()
    {
        var service = CreateService();

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "w1",
                UserId = "u4",
                Environment = "Windows_Live",
                Question = "list services stopped",
                SelectedServers = ["WIN01"]
            },
            CancellationToken.None);

        Assert.Equal("LLM_ONLY", response.Resolution);
        Assert.Equal("LLM_ONLY", response.ExecutionPayload?.ExecutionMode);
        Assert.Equal("PS", response.ScriptLanguage);
        Assert.NotNull(response.Script);
        Assert.Contains("Get-Service | Where-Object { $_.Status -eq 'Stopped' }", response.Script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WindowsLive_ListAllWindowsVersion_Returns_Ps_Script_Without_Stop()
    {
        var service = CreateService();

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "w2",
                UserId = "u5",
                Environment = "Windows_Live",
                Question = "list all Windows Version",
                SelectedServers = ["CTS03", "CTS02"]
            },
            CancellationToken.None);

        Assert.Equal("LLM_ONLY", response.Resolution);
        Assert.Equal("LLM_ONLY", response.ExecutionPayload?.ExecutionMode);
        Assert.Equal("PS", response.ScriptLanguage);
        Assert.NotNull(response.Script);
        Assert.Null(response.Message);
        Assert.Contains("Get-CimInstance Win32_OperatingSystem", response.Script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WindowsLive_ListAllDrivesNameLikeC_Returns_Ps_Script_With_Drive_Filter()
    {
        var service = CreateService();

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "w3",
                UserId = "u6",
                Environment = "Windows_Live",
                Question = "List all drives name like C",
                SelectedServers = ["CTS03"]
            },
            CancellationToken.None);

        Assert.NotEqual("STOPPED", response.Resolution);
        Assert.Equal("LLM_ONLY", response.Resolution);
        Assert.Equal("PS", response.ScriptLanguage);
        Assert.NotNull(response.Script);
        Assert.True(
            response.Script.Contains("Get-CimInstance Win32_Volume", StringComparison.OrdinalIgnoreCase)
            || response.Script.Contains("Get-CimInstance Win32_LogicalDisk", StringComparison.OrdinalIgnoreCase));
        Assert.True(
            response.Script.Contains("C:", StringComparison.OrdinalIgnoreCase)
            || response.Script.Contains("-like 'C", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WindowsLive_RestartServer_Returns_Stopped_Blocked()
    {
        var service = CreateService();

        var response = await service.ExecuteAsync(
            new AskApiRequest
            {
                ConversationId = "w4",
                UserId = "u7",
                Environment = "Windows_Live",
                Question = "restart server CTS03",
                SelectedServers = ["CTS03"]
            },
            CancellationToken.None);

        Assert.Equal("STOPPED", response.Resolution);
        Assert.StartsWith("BLOCKED", response.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Null(response.Script);
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
            NullLogger<AskPipelineService>.Instance);
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
                UseForGenerate = true
            };
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

            if (environmentTag.Equals("Windows_Live", StringComparison.OrdinalIgnoreCase) &&
                rawQuestion.Contains("restart server", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(
                    $"BLOCKED: DANGEROUS_REQUEST - Only safe read-only diagnostics and inventory questions are allowed.||{routedQueryCode}");
            }

            if (environmentTag.Equals("Windows_Live", StringComparison.OrdinalIgnoreCase) &&
                rawQuestion.Contains("windows version", StringComparison.OrdinalIgnoreCase))
            {
                // No querycode delimiter on purpose: parse should be best-effort and still continue.
                return Task.FromResult("List Windows versions from operating system inventory");
            }

            var tuned = string.IsNullOrWhiteSpace(rawQuestion) ? "MISMATCH: EMPTY_OR_UNCLEAR - Please ask a clear question." : rawQuestion.Trim();
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

            if (promptTemplate.Contains("strict planner for a READ-ONLY SQL Server diagnostic script", StringComparison.Ordinal))
            {
                if (tunedQuestion.Contains("drop database", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult("""
{
  "readOnly": true,
  "environment": "SqlServer_Live",
  "scriptLanguage": "SQL",
  "intent": "drop database",
  "filters": [],
  "timeWindow": null,
  "needsClarification": false,
  "clarificationQuestion": null,
  "confidence": 0.98
}
""");
                }

                return Task.FromResult("""
{
  "readOnly": true,
  "environment": "SqlServer_Live",
  "scriptLanguage": "SQL",
  "intent": "list databases over size threshold",
  "filters": [
    {"field":"databaseSizeGb","op":">","value":10,"unit":"GB"}
  ],
  "timeWindow": null,
  "needsClarification": false,
  "clarificationQuestion": null,
  "confidence": 0.97
}
""");
            }

            if (promptTemplate.Contains("strict planner for a READ-ONLY Windows diagnostics PowerShell script", StringComparison.Ordinal))
            {
                if (tunedQuestion.Contains("windows version", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult("""
{
  "readOnly": true,
  "environment": "Windows_Live",
  "scriptLanguage": "PS",
  "intent": "list windows operating system versions",
  "filters": [],
  "timeWindow": null,
  "needsClarification": false,
  "clarificationQuestion": null,
  "confidence": 0.96
}
""");
                }

                return Task.FromResult("""
{
  "readOnly": true,
  "environment": "Windows_Live",
  "scriptLanguage": "PS",
  "intent": "list stopped services",
  "filters": [
    {"field":"status","op":"=","value":"Stopped"}
  ],
  "timeWindow": null,
  "needsClarification": false,
  "clarificationQuestion": null,
  "confidence": 0.97
}
""");
            }

            if (promptTemplate.Contains("Generate a READ-ONLY T-SQL script for SQL Server.", StringComparison.Ordinal))
            {
                if (tunedQuestion.Contains("drop database", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult("""
SET NOCOUNT ON;
DROP DATABASE [test];
""");
                }

                return Task.FromResult("""
SET NOCOUNT ON;
WITH db_size AS (
    SELECT
        DB_NAME(mf.database_id) AS [DatabaseName],
        SUM(CASE WHEN mf.type_desc = 'ROWS' THEN mf.size ELSE 0 END) * 8.0 / 1024 / 1024 AS [DataSizeGB]
    FROM sys.master_files AS mf
    GROUP BY mf.database_id
)
SELECT
    @@SERVERNAME as [Server],
    ds.[DatabaseName],
    ds.[DataSizeGB]
FROM db_size AS ds
WHERE ds.[DataSizeGB] > 10
ORDER BY ds.[DataSizeGB] DESC;
""");
            }

            if (promptTemplate.Contains("Generate a READ-ONLY PowerShell script for Windows server diagnostics.", StringComparison.Ordinal))
            {
                if (tunedQuestion.Contains("restart server", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult("""
Restart-Computer -ComputerName CTS03 -Force
""");
                }

                if (tunedQuestion.Contains("drives", StringComparison.OrdinalIgnoreCase) &&
                    tunedQuestion.Contains("like c", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult("""
EXPLANATION: listing drives filtered by C
```powershell
$server = $env:COMPUTERNAME
Get-CimInstance Win32_LogicalDisk |
    Where-Object { $_.DeviceID -like 'C:*' } |
    Select-Object @{Name='Server';Expression={$server}}, DeviceID, VolumeName, Size, FreeSpace
```
""");
                }

                if (tunedQuestion.Contains("windows version", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult("""
PLAN: query OS versions
```powershell
PLAN: pull Win32_OperatingSystem
$server = $env:COMPUTERNAME
Get-CimInstance Win32_OperatingSystem |
    Select-Object @{Name='Server';Expression={$server}}, Caption, Version, BuildNumber
```
""");
                }

                return Task.FromResult("""
$server = $env:COMPUTERNAME
Get-Service | Where-Object { $_.Status -eq 'Stopped' } |
    Select-Object @{Name='Server';Expression={$server}}, Name, Status
""");
            }

            if (environmentTag.Equals("General", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult("The Moon is Earth's natural satellite.");

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
}
