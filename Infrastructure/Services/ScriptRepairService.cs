using System.Text.Json;
using System.Text.RegularExpressions;
using Application.Common.Interfaces;
using Application.Common.Models;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

/// <summary>
/// Sends a failed script + error to the LLM for minimal repair.
/// Wraps the LLM client call with the ScriptRepair prompt template.
/// </summary>
internal sealed class ScriptRepairService(
    IModelSelector modelSelector,
    IEnumerable<ILLMClient> llmClients,
    ILogger<ScriptRepairService> logger) : IScriptRepairService
{
    private readonly IModelSelector _modelSelector = modelSelector;
    private readonly ILogger<ScriptRepairService> _logger = logger;

    private readonly IReadOnlyDictionary<string, ILLMClient> _clientsByProvider =
        llmClients.ToDictionary(client => client.Provider, StringComparer.OrdinalIgnoreCase);

    public async Task<string> RepairAsync(
        string environment,
        string tunedQuestion,
        string scriptLanguage,
        string failedScript,
        string errorMessage,
        CancellationToken ct = default)
    {
        var model = _modelSelector.SelectGenerateModel();
        var client = ResolveClient(model.Provider);

        var prompt = PromptTemplates.ScriptRepair
            .Replace("{{$environment}}", environment, StringComparison.Ordinal)
            .Replace("{{$tunedQuestion}}", tunedQuestion, StringComparison.Ordinal)
            .Replace("{{$errorMessage}}", errorMessage, StringComparison.Ordinal)
            .Replace("{{$failedScript}}", failedScript ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{$safetyJson}}", BuildSafetyPolicy(environment), StringComparison.Ordinal);

        var repaired = await client.GenerateAsync(
            prompt,
            tunedQuestion,
            environment,
            model.ModelKey,
            ct);

        // Strip code fences if LLM wrapped the output
        repaired = StripCodeFences(repaired ?? string.Empty);

        _logger.LogInformation(
            "LLM script repair completed. Environment={Environment}, Language={Language}",
            environment, scriptLanguage);

        return repaired;
    }

    private ILLMClient ResolveClient(string provider)
    {
        if (_clientsByProvider.TryGetValue(provider, out var client))
            return client;

        throw new InvalidOperationException($"No ILLMClient registered for provider '{provider}'.");
    }

    private static string StripCodeFences(string script)
    {
        var text = script.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        text = Regex.Replace(text, @"^\s*```[\w-]*\s*\r?\n", string.Empty, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\r?\n\s*```\s*$", string.Empty, RegexOptions.IgnoreCase);
        return text.Trim();
    }

    private static string BuildSafetyPolicy(string environment)
    {
        if (EnvironmentRules.IsSqlServer(environment))
        {
            var policy = new
            {
                readOnly = true,
                blocked = new[]
                {
                    "INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE", "DROP", "ALTER", "CREATE",
                    "GRANT", "REVOKE", "DENY", "KILL", "RECONFIGURE", "BACKUP", "RESTORE",
                    "xp_cmdshell", "sp_configure", "sp_OACreate", "OPENROWSET", "OPENDATASOURCE",
                    "sp_add_job", "sp_update_job", "sp_delete_job", "sp_add_jobstep", "sp_update_jobstep",
                    "sp_delete_jobstep", "sp_add_jobschedule", "sp_update_jobschedule", "sp_delete_jobschedule"
                }
            };
            return JsonSerializer.Serialize(policy);
        }

        var windowsPolicy = new
        {
            readOnly = true,
            blocked = new[]
            {
                "Remove-Item", "Set-ItemProperty", "New-ItemProperty",
                "Restart-Computer", "Stop-Computer",
                "Start-Service", "Stop-Service", "Restart-Service",
                "Stop-Process", "taskkill",
                "Format-Volume", "Clear-EventLog",
                "Disable-NetAdapter",
                "New-NetFirewallRule", "Set-NetFirewallRule", "Remove-NetFirewallRule",
                "Install-*", "Uninstall-*"
            }
        };
        return JsonSerializer.Serialize(windowsPolicy);
    }
}
