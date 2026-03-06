using System.Security.Cryptography;
using System.Text;
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
    IScriptValidationService validationService,
    IScriptRepairService repairService,
    ISqlExecutor sqlExecutor,
    IPowerShellExecutor powerShellExecutor,
    IOptions<ScriptExecutionOptions> options,
    ILogger<ScriptAutoFixOrchestrator> logger) : IScriptAutoFixOrchestrator
{
    private readonly IModelSelector _modelSelector = modelSelector;
    private readonly ScriptSafetyScanner _safetyScanner = safetyScanner;
    private readonly IScriptValidationService _validationService = validationService;
    private readonly IScriptRepairService _repairService = repairService;
    private readonly ISqlExecutor _sqlExecutor = sqlExecutor;
    private readonly IPowerShellExecutor _powerShellExecutor = powerShellExecutor;
    private readonly ScriptExecutionOptions _options = options.Value;
    private readonly ILogger<ScriptAutoFixOrchestrator> _logger = logger;

    /// <summary>Maximum LLM repair calls (so total attempts = MaxRepairs + 1).</summary>
    private const int MaxRepairs = 3;

    private readonly IReadOnlyDictionary<string, ILLMClient> _clientsByProvider =
        llmClients.ToDictionary(client => client.Provider, StringComparer.OrdinalIgnoreCase);

    public Task<ScriptExecutionResponse> ExecuteAsync(
        ScriptExecutionRequest request,
        CancellationToken cancellationToken)
    {
        return ExecuteWithAutoFixAsync(request, cancellationToken);
    }

    public async Task<ScriptExecutionResponse> ExecuteWithAutoFixAsync(
        ScriptExecutionRequest request,
        CancellationToken cancellationToken)
    {
        return await ExecuteWithAutoFixAsync(request, null, cancellationToken);
    }

    public async Task<ScriptExecutionResponse> ExecuteWithAutoFixAsync(
        ScriptExecutionRequest request,
        IProgressStream? progress,
        CancellationToken cancellationToken)
    {
        var prog = progress ?? NullProgressStream.Instance;
        var response = new ScriptExecutionResponse
        {
            Environment = request.Environment,
            ScriptLanguage = request.ScriptLanguage,
            TunedQuestion = request.TunedQuestion
        };

        if (!IsValidRequest(request, out var validationError))
        {
            response.Attempts.Add(BuildAttempt(
                attemptNumber: 1,
                script: request.GeneratedScript ?? string.Empty,
                target: request.SelectedServers.FirstOrDefault() ?? string.Empty,
                phase: "VALIDATE",
                status: "FAILED",
                errorType: null,
                error: validationError,
                repairedByLlm: false));
            FinalizeSummary(response, request.SelectedServers.Length);
            return response;
        }

        var firstTarget = request.SelectedServers[0];
        var candidateScript = request.GeneratedScript;
        var repairsUsed = 0;
        var isSql = EnvironmentRules.IsSqlServer(request.Environment)
                    || string.Equals(request.ScriptLanguage, "SQL", StringComparison.OrdinalIgnoreCase);

        // ── VALIDATE / REPAIR LOOP ──────────────────────────────────────────────
        while (true)
        {
            var attemptNumber = response.Attempts.Count + 1;

            // Safety scan
            var scan = _safetyScanner.Scan(request.Environment, request.ScriptLanguage, candidateScript);
            if (!scan.IsSafe)
            {
                response.Attempts.Add(BuildAttempt(
                    attemptNumber, scan.SanitizedScript, firstTarget,
                    phase: "VALIDATE", status: "BLOCKED",
                    errorType: null,
                    error: $"BLOCKED:DANGEROUS_COMMAND:{scan.BlockedToken}",
                    repairedByLlm: repairsUsed > 0));
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

            candidateScript = scan.SanitizedScript;

            // Compile/parse validation
            await prog.EmitAsync(ProgressEvent.PhaseStart("VALIDATE", $"Attempt {attemptNumber}"), cancellationToken);

            ScriptValidationResult validationResult;
            if (isSql)
            {
                validationResult = await _validationService.ValidateSqlAsync(
                    candidateScript, firstTarget, cancellationToken);
            }
            else
            {
                validationResult = await _validationService.ValidatePowerShellAsync(
                    candidateScript, cancellationToken);
            }

            await prog.EmitAsync(ProgressEvent.PhaseDone("VALIDATE",
                validationResult.IsValid ? "passed" : $"failed: {validationResult.ErrorType}"), cancellationToken);

            if (validationResult.IsValid)
            {
                // Validation passed — record success attempt and proceed to execution
                response.Attempts.Add(BuildAttempt(
                    attemptNumber, candidateScript, firstTarget,
                    phase: "VALIDATE",
                    status: "SUCCESS",
                    errorType: null,
                    error: null,
                    repairedByLlm: repairsUsed > 0));
                response.FinalScript = candidateScript;
                break;
            }

            // CONNECTION error: not a syntax problem — skip repair, proceed to execution with best script
            if (string.Equals(validationResult.ErrorType, "CONNECTION", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Validation connection error on target {Target}; skipping repair. Error={Error}",
                    firstTarget, validationResult.ErrorMessage);

                response.Attempts.Add(BuildAttempt(
                    attemptNumber, candidateScript, firstTarget,
                    phase: "VALIDATE",
                    status: "FAILED",
                    errorType: "CONNECTION",
                    error: validationResult.ErrorMessage,
                    repairedByLlm: repairsUsed > 0));

                // Cannot validate, but script might work on other targets.
                // Record first target as failed and proceed with remaining targets.
                response.FinalScript = candidateScript;
                response.ResultsByServer.Add(new ScriptExecutionServerResult
                {
                    Server = firstTarget,
                    Status = "FAILED",
                    RowCount = 0,
                    Error = validationResult.ErrorMessage,
                    DurationMs = 0
                });
                break;
            }

            // SYNTAX error — record the failed attempt
            response.Attempts.Add(BuildAttempt(
                attemptNumber, candidateScript, firstTarget,
                phase: "VALIDATE",
                status: "FAILED",
                errorType: "SYNTAX",
                error: validationResult.ErrorMessage,
                repairedByLlm: repairsUsed > 0));

            // Attempt LLM repair if budget remains
            if (repairsUsed < MaxRepairs)
            {
                repairsUsed++;
                _logger.LogInformation(
                    "Syntax error on attempt {Attempt}; requesting LLM repair #{Repair}. Error={Error}",
                    attemptNumber, repairsUsed, validationResult.ErrorMessage);

                await prog.EmitAsync(ProgressEvent.PhaseStart("REPAIR", $"Repair #{repairsUsed}"), cancellationToken);

                candidateScript = await _repairService.RepairAsync(
                    request.Environment,
                    request.TunedQuestion,
                    request.ScriptLanguage,
                    candidateScript,
                    validationResult.ErrorMessage ?? "Unknown error",
                    cancellationToken);

                await prog.EmitAsync(ProgressEvent.PhaseDone("REPAIR"), cancellationToken);
                continue;
            }

            // Budget exhausted — return FAILED
            _logger.LogWarning(
                "Repair budget exhausted after {Repairs} repairs. Returning FAILED.", repairsUsed);
            response.FinalScript = null;
            response.ResultsByServer.Add(new ScriptExecutionServerResult
            {
                Server = firstTarget,
                Status = "FAILED",
                RowCount = 0,
                Error = $"Compile validation failed after {repairsUsed} repair attempts: {validationResult.ErrorMessage}",
                DurationMs = 0
            });
            FinalizeSummary(response, request.SelectedServers.Length);
            return response;
        }

        // ── EXECUTION ──────────────────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(response.FinalScript))
        {
            FinalizeSummary(response, request.SelectedServers.Length);
            return response;
        }

        await prog.EmitAsync(ProgressEvent.PhaseStart("EXECUTE"), cancellationToken);

        // Check if first target already has a result (e.g. connection error during validation)
        var firstTargetHasResult = response.ResultsByServer.Any(
            r => string.Equals(r.Server, firstTarget, StringComparison.OrdinalIgnoreCase));

        var allResults = await ExecuteAcrossTargetsAsync(
            request.Environment,
            request.ScriptLanguage,
            request.SelectedServers,
            response.FinalScript,
            firstTargetHasResult ? firstTarget : null, // skip first target if already has result
            prog,
            cancellationToken);

        // Merge: keep existing results + add new ones
        foreach (var r in allResults)
        {
            if (!response.ResultsByServer.Any(
                    existing => string.Equals(existing.Server, r.Server, StringComparison.OrdinalIgnoreCase)))
            {
                response.ResultsByServer.Add(r);
            }
        }

        await prog.EmitAsync(ProgressEvent.PhaseDone("EXECUTE"), cancellationToken);

        FinalizeSummary(response, request.SelectedServers.Length);
        return response;
    }

    private async Task<List<ScriptExecutionServerResult>> ExecuteAcrossTargetsAsync(
        string environment,
        string scriptLanguage,
        IReadOnlyList<string> targets,
        string script,
        string? skipTarget,
        IProgressStream progress,
        CancellationToken cancellationToken)
    {
        var results = new List<ScriptExecutionServerResult>();
        var maxDegree = _options.MaxDegreeOfParallelism <= 0 ? 5 : _options.MaxDegreeOfParallelism;
        using var gate = new SemaphoreSlim(maxDegree, maxDegree);

        var targetsToRun = skipTarget is null
            ? targets
            : targets.Where(t => !string.Equals(t, skipTarget, StringComparison.OrdinalIgnoreCase)).ToList();

        var tasks = targetsToRun.Select(async target =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                await progress.EmitAsync(ProgressEvent.TargetStart(target), cancellationToken);

                var result = await ExecuteSingleTargetAsync(
                    environment, scriptLanguage, target, script, cancellationToken);

                var serverResult = ToServerResult(result);

                if (result.Success)
                    await progress.EmitAsync(ProgressEvent.TargetDone(target, result.Rows.Count), cancellationToken);
                else
                    await progress.EmitAsync(ProgressEvent.TargetError(target, result.Error ?? "Failed"), cancellationToken);

                return serverResult;
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        var completed = await Task.WhenAll(tasks);
        results.AddRange(completed);
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

    private ILLMClient ResolveClient(string provider)
    {
        if (_clientsByProvider.TryGetValue(provider, out var client))
            return client;

        throw new InvalidOperationException($"No ILLMClient implementation registered for provider '{provider}'.");
    }

    private static ScriptExecutionAttempt BuildAttempt(
        int attemptNumber,
        string script,
        string target,
        string phase,
        string status,
        string? errorType,
        string? error,
        bool repairedByLlm)
    {
        var preview = script.Length > 200 ? script[..200] : script;
        return new ScriptExecutionAttempt
        {
            Attempt = attemptNumber,
            Script = script,
            Target = target,
            Phase = phase,
            Status = status,
            ErrorType = errorType,
            Error = error,
            RepairedByLlm = repairedByLlm,
            ScriptHash = ComputeHash(script),
            ScriptPreview = preview,
            TsUtc = DateTime.UtcNow.ToString("O")
        };
    }

    private static string ComputeHash(string script)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(script ?? string.Empty));
        return Convert.ToHexString(bytes)[..8];
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
        var totalRowCount = response.ResultsByServer.Sum(r => r.RowCount);

        if (response.ResultsByServer.Count == 0 && response.Attempts.Any(a => !string.Equals(a.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase)))
            failCount = 1;

        response.Summary = new ScriptExecutionSummary
        {
            SuccessCount = successCount,
            FailCount = failCount,
            TotalRowCount = totalRowCount,
            TotalTargets = totalTargets
        };
    }
}
