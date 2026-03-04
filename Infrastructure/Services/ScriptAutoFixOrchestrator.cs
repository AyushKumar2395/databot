using System.Text.Json;
using Application.Common.Interfaces;
using Application.Common.Models;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

internal sealed class ScriptAutoFixOrchestrator(
    IModelSelector modelSelector,
    IEnumerable<ILLMClient> llmClients,
    ScriptSafetyScanner safetyScanner,
    ISqlExecutor sqlExecutor,
    IPowerShellExecutor powerShellExecutor,
    IOptions<ScriptExecutionOptions> options,
    ILogger<ScriptAutoFixOrchestrator> logger) : IScriptAutoFixOrchestrator
{
    private readonly IModelSelector _modelSelector = modelSelector;
    private readonly ScriptSafetyScanner _safetyScanner = safetyScanner;
    private readonly ISqlExecutor _sqlExecutor = sqlExecutor;
    private readonly IPowerShellExecutor _powerShellExecutor = powerShellExecutor;
    private readonly ScriptExecutionOptions _options = options.Value;
    private readonly ILogger<ScriptAutoFixOrchestrator> _logger = logger;

    private readonly IReadOnlyDictionary<string, ILLMClient> _clientsByProvider =
        llmClients.ToDictionary(client => client.Provider, StringComparer.OrdinalIgnoreCase);

    public async Task<ScriptExecutionResponse> ExecuteAsync(
        ScriptExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var response = new ScriptExecutionResponse
        {
            Environment = request.Environment,
            ScriptLanguage = request.ScriptLanguage,
            TunedQuestion = request.TunedQuestion
        };

        if (!IsValidRequest(request, out var validationError))
        {
            response.Attempts.Add(new ScriptExecutionAttempt
            {
                Attempt = 1,
                Script = request.GeneratedScript ?? string.Empty,
                Target = request.SelectedServers.FirstOrDefault() ?? string.Empty,
                Status = "FAILED",
                Error = validationError
            });
            FinalizeSummary(response, request.SelectedServers.Length);
            return response;
        }

        var firstTarget = request.SelectedServers[0];
        var candidateScript = request.GeneratedScript;
        ExecutorRunResult? firstAttemptSuccessResult = null;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var scan = _safetyScanner.Scan(request.Environment, request.ScriptLanguage, candidateScript);
            if (!scan.IsSafe)
            {
                response.Attempts.Add(new ScriptExecutionAttempt
                {
                    Attempt = attempt,
                    Script = scan.SanitizedScript,
                    Target = firstTarget,
                    Status = "BLOCKED",
                    Error = $"BLOCKED:DANGEROUS_COMMAND:{scan.BlockedToken}"
                });
                response.FinalScript = null;
                response.ResultsByServer.Add(new ScriptExecutionServerResult
                {
                    Server = firstTarget,
                    Status = "FAILED",
                    RowCount = 0,
                    Error = $"BLOCKED:DANGEROUS_COMMAND:{scan.BlockedToken}",
                    DurationMs = 0
                });
                FinalizeSummary(response, request.SelectedServers.Length);
                return response;
            }

            var execution = await ExecuteSingleTargetAsync(
                request.Environment,
                request.ScriptLanguage,
                firstTarget,
                scan.SanitizedScript,
                cancellationToken);

            response.Attempts.Add(new ScriptExecutionAttempt
            {
                Attempt = attempt,
                Script = scan.SanitizedScript,
                Target = firstTarget,
                Status = execution.Success ? "SUCCESS" : "FAILED",
                Error = execution.Success ? null : execution.Error
            });

            if (execution.Success)
            {
                firstAttemptSuccessResult = execution;
                response.FinalScript = scan.SanitizedScript;
                break;
            }

            if (attempt >= 3 || !execution.IsRetryableCompileError)
            {
                response.FinalScript = null;
                response.ResultsByServer.Add(ToServerResult(execution));
                FinalizeSummary(response, request.SelectedServers.Length);
                return response;
            }

            candidateScript = attempt == 1
                ? await BuildPatchedScriptAsync(
                    request.Environment,
                    request.TunedQuestion,
                    request.ScriptLanguage,
                    scan.SanitizedScript,
                    execution.Error ?? "Unknown error",
                    cancellationToken)
                : await BuildRegeneratedScriptAsync(
                    request.Environment,
                    request.TunedQuestion,
                    request.ScriptLanguage,
                    execution.Error ?? "Unknown error",
                    cancellationToken);
        }

        if (firstAttemptSuccessResult is null || string.IsNullOrWhiteSpace(response.FinalScript))
        {
            FinalizeSummary(response, request.SelectedServers.Length);
            return response;
        }

        var allResults = await ExecuteAcrossTargetsAsync(
            request.Environment,
            request.ScriptLanguage,
            request.SelectedServers,
            response.FinalScript,
            firstAttemptSuccessResult,
            cancellationToken);

        response.ResultsByServer = allResults;
        FinalizeSummary(response, request.SelectedServers.Length);
        return response;
    }

    private async Task<List<ScriptExecutionServerResult>> ExecuteAcrossTargetsAsync(
        string environment,
        string scriptLanguage,
        IReadOnlyList<string> targets,
        string script,
        ExecutorRunResult firstResult,
        CancellationToken cancellationToken)
    {
        var results = new List<ScriptExecutionServerResult> { ToServerResult(firstResult) };
        if (targets.Count <= 1)
            return results;

        var maxDegree = _options.MaxDegreeOfParallelism <= 0 ? 5 : _options.MaxDegreeOfParallelism;
        using var gate = new SemaphoreSlim(maxDegree, maxDegree);

        var tasks = targets
            .Where(target => !string.Equals(target, firstResult.Server, StringComparison.OrdinalIgnoreCase))
            .Select(async target =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    var result = await ExecuteSingleTargetAsync(
                        environment,
                        scriptLanguage,
                        target,
                        script,
                        cancellationToken);
                    return ToServerResult(result);
                }
                finally
                {
                    gate.Release();
                }
            })
            .ToArray();

        var rest = await Task.WhenAll(tasks);
        results.AddRange(rest);
        return results;
    }

    private async Task<ExecutorRunResult> ExecuteSingleTargetAsync(
        string environment,
        string scriptLanguage,
        string target,
        string script,
        CancellationToken cancellationToken)
    {
        if (EnvironmentRules.IsSqlServer(environment) || string.Equals(scriptLanguage, "SQL", StringComparison.OrdinalIgnoreCase))
            return await _sqlExecutor.ExecuteAsync(target, script, cancellationToken);

        return await _powerShellExecutor.ExecuteAsync(target, script, cancellationToken);
    }

    private async Task<string> BuildPatchedScriptAsync(
        string environment,
        string tunedQuestion,
        string scriptLanguage,
        string currentScript,
        string errorText,
        CancellationToken cancellationToken)
    {
        var model = _modelSelector.SelectGenerateModel();
        var client = ResolveClient(model.Provider);
        var prompt = PromptTemplates.FixScriptFromError
            .Replace("{{$environment}}", environment, StringComparison.Ordinal)
            .Replace("{{$tunedQuestion}}", tunedQuestion, StringComparison.Ordinal)
            .Replace("{{$scriptLanguage}}", scriptLanguage, StringComparison.Ordinal)
            .Replace("{{$currentScript}}", currentScript ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{$errorText}}", errorText ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{$safetyPolicy}}", BuildSafetyPolicy(environment), StringComparison.Ordinal);

        var fixedScript = await client.GenerateAsync(
            prompt,
            tunedQuestion,
            environment,
            model.ModelKey,
            cancellationToken);

        _logger.LogInformation("Patch-mode script fix attempted for environment {Environment}.", environment);
        return fixedScript ?? string.Empty;
    }

    private async Task<string> BuildRegeneratedScriptAsync(
        string environment,
        string tunedQuestion,
        string scriptLanguage,
        string errorText,
        CancellationToken cancellationToken)
    {
        var model = _modelSelector.SelectGenerateModel();
        var client = ResolveClient(model.Provider);
        var prompt = PromptTemplates.RegenerateScript
            .Replace("{{$environment}}", environment, StringComparison.Ordinal)
            .Replace("{{$tunedQuestion}}", tunedQuestion, StringComparison.Ordinal)
            .Replace("{{$scriptLanguage}}", scriptLanguage, StringComparison.Ordinal)
            .Replace("{{$errorText}}", errorText ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{$safetyPolicy}}", BuildSafetyPolicy(environment), StringComparison.Ordinal);

        var regeneratedScript = await client.GenerateAsync(
            prompt,
            tunedQuestion,
            environment,
            model.ModelKey,
            cancellationToken);

        _logger.LogInformation("Regen-mode script fix attempted for environment {Environment}.", environment);
        return regeneratedScript ?? string.Empty;
    }

    private ILLMClient ResolveClient(string provider)
    {
        if (_clientsByProvider.TryGetValue(provider, out var client))
            return client;

        throw new InvalidOperationException($"No ILLMClient implementation registered for provider '{provider}'.");
    }

    private static bool IsValidRequest(ScriptExecutionRequest request, out string error)
    {
        if (request.SelectedServers is null || request.SelectedServers.Length == 0)
        {
            error = "selectedServers must contain at least one server.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.GeneratedScript))
        {
            error = "generatedScript is required.";
            return false;
        }

        if (!EnvironmentRules.IsSqlServer(request.Environment) && !EnvironmentRules.IsWindows(request.Environment))
        {
            error = "environment must be SqlServer_Live or Windows_Live.";
            return false;
        }

        if (EnvironmentRules.IsSqlServer(request.Environment) &&
            !string.Equals(request.ScriptLanguage, "SQL", StringComparison.OrdinalIgnoreCase))
        {
            error = "scriptLanguage must be SQL for SqlServer_Live.";
            return false;
        }

        if (EnvironmentRules.IsWindows(request.Environment) &&
            !string.Equals(request.ScriptLanguage, "PS", StringComparison.OrdinalIgnoreCase))
        {
            error = "scriptLanguage must be PS for Windows_Live.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static ScriptExecutionServerResult ToServerResult(ExecutorRunResult result)
    {
        return new ScriptExecutionServerResult
        {
            Server = result.Server,
            Status = result.Success ? "SUCCESS" : "FAILED",
            RowCount = result.Rows.Count,
            Rows = result.Success ? result.Rows : null,
            DurationMs = result.DurationMs,
            Error = result.Success ? null : result.Error,
            RawOutput = result.RawOutput
        };
    }

    private static void FinalizeSummary(ScriptExecutionResponse response, int totalTargets)
    {
        var successCount = response.ResultsByServer.Count(r => string.Equals(r.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase));
        var failCount = response.ResultsByServer.Count(r => !string.Equals(r.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase));

        // If execution never reached all targets, keep fail count at least one when we have failed attempts.
        if (response.ResultsByServer.Count == 0 && response.Attempts.Any(a => !string.Equals(a.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase)))
            failCount = 1;

        response.Summary = new ScriptExecutionSummary
        {
            SuccessCount = successCount,
            FailCount = failCount,
            TotalTargets = totalTargets
        };
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
                    "DELETE", "UPDATE", "INSERT", "MERGE", "DROP", "ALTER", "TRUNCATE", "EXEC",
                    "xp_cmdshell", "sp_configure", "RECONFIGURE", "KILL"
                }
            };
            return JsonSerializer.Serialize(policy);
        }

        var windowsPolicy = new
        {
            readOnly = true,
            blocked = new[]
            {
                "Remove-Item",
                "Set-ItemProperty",
                "Restart-Computer",
                "Stop-Computer",
                "Stop-Process",
                "Format-Volume",
                "Disable-*",
                "Enable-*",
                "New-*",
                "Set-*"
            }
        };
        return JsonSerializer.Serialize(windowsPolicy);
    }
}

