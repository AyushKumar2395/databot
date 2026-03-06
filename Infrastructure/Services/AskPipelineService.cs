using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Application.Common.Interfaces;
using Application.Common.Models;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

/// <summary>
/// Implements strict LLM-only orchestration:
/// tune -> answer (General) OR tune -> plan -> generate (SQL/Windows).
/// </summary>
public sealed class AskPipelineService(
    IModelSelector modelSelector,
    IEnumerable<ILLMClient> llmClients,
    IScriptAutoFixOrchestrator scriptAutoFixOrchestrator,
    IToolRegistryResolver toolRegistryResolver,
    ITemplateRenderer templateRenderer,
    IRequestPolicyService requestPolicyService,
    ILogger<AskPipelineService> logger) : IAskPipelineService
{
    // Detects alias in: FROM sys.databases [AS] alias
    private static readonly Regex SysDatabasesAliasPattern = new(
        @"\bFROM\s+sys\.databases\b(?:\s+(?:AS\s+)?(\w+))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Matches DB_NAME() used as a column value (with optional alias)
    private static readonly Regex DbNameColumnPattern = new(
        @"\bDB_NAME\s*\(\s*\)(\s+AS\s+\[[^\]]+\]|\s+AS\s+\w+)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] SqlRescueKeywords =
    [
        "sql", "database", "databases", "table", "index", "view", "query", "stored procedure", "agent", "deadlock",
        "blocking", "wait stats", "backup", "restore", "instance", "job", "dmv", "sys."
    ];

    private static readonly string[] WindowsRescueKeywords =
    [
        "windows", "server", "disk", "drives", "drive", "event log", "service", "process", "cpu", "memory",
        "port", "firewall", "volume", "iis", "cluster", "patch"
    ];

    private const string BlockedStateChangingRequestMessage =
        "BLOCKED: STATE_CHANGING_REQUEST - Only read-only diagnostics/inventory queries are allowed.";

    private static readonly string[] SqlBlockedProcedureTokens =
    [
        "xp_cmdshell",
        "sp_configure",
        "sp_OACreate",
        "OPENROWSET",
        "OPENDATASOURCE",
        "sp_add_job",
        "sp_update_job",
        "sp_delete_job",
        "sp_add_jobstep",
        "sp_update_jobstep",
        "sp_delete_jobstep",
        "sp_add_jobschedule",
        "sp_update_jobschedule",
        "sp_delete_jobschedule"
    ];

    private static readonly string[] WindowsBlockedCommands =
    [
        "Restart-Computer",
        "Stop-Computer",
        "shutdown",
        "Start-Service",
        "Stop-Service",
        "Restart-Service",
        "Stop-Process",
        "taskkill",
        "Set-ItemProperty",
        "New-ItemProperty",
        "Remove-Item",
        "Format-Volume",
        "Clear-EventLog",
        "Disable-NetAdapter",
        "New-NetFirewallRule",
        "Set-NetFirewallRule",
        "Remove-NetFirewallRule"
    ];

    private readonly IModelSelector _modelSelector = modelSelector;
    private readonly IScriptAutoFixOrchestrator _scriptAutoFixOrchestrator = scriptAutoFixOrchestrator;
    private readonly IToolRegistryResolver _toolRegistryResolver = toolRegistryResolver;
    private readonly ITemplateRenderer _templateRenderer = templateRenderer;
    private readonly IRequestPolicyService _requestPolicyService = requestPolicyService;
    private readonly ILogger<AskPipelineService> _logger = logger;

    private readonly IReadOnlyDictionary<string, ILLMClient> _clientsByProvider =
        llmClients.ToDictionary(client => client.Provider, StringComparer.OrdinalIgnoreCase);

    public async Task<AskApiResponse> ExecuteAsync(AskApiRequest request, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Ask pipeline started. ConversationId={ConversationId}, BearerToken={BearerToken}, Environment={Environment}",
            request.ConversationId,
            request.BearerToken,
            request.Environment);

        // ── REQUEST_POLICY_CHECK (before any LLM call) ─────────────────────
        var policyDecision = _requestPolicyService.Evaluate(request, request.Question);
        if (!policyDecision.Allowed)
        {
            return CreatePolicyBlockedResponse(request, policyDecision);
        }

        var routedQueryCode = string.Empty;
        var tuneModel = _modelSelector.SelectTuneModel();
        var planModel = _modelSelector.SelectPlanModel();
        var generateModel = _modelSelector.SelectGenerateModel();

        var tuneClient = ResolveClient(tuneModel.Provider);
        var tuningPrompt = PromptTemplates.Tuning
            .Replace("{{$rawUserQuestion}}", request.Question, StringComparison.Ordinal)
            .Replace("{{$environmentTag}}", request.Environment, StringComparison.Ordinal)
            .Replace("{{$routedQueryCode}}", routedQueryCode, StringComparison.Ordinal);

        var tunedLine = await tuneClient.TuneAsync(
            tuningPrompt,
            request.Question,
            request.Environment,
            routedQueryCode,
            tuneModel.ModelKey,
            cancellationToken);

        var (leftText, _) = ParseTunedLine(tunedLine);
        if (TryRecoverFalseMismatch(leftText, request.Question, request.Environment, out var recoveredLeftText))
        {
            _logger.LogWarning(
                "Recovered false mismatch from tuning model. Environment={Environment}, Before='{Before}', After='{After}'",
                request.Environment,
                leftText,
                recoveredLeftText);
            leftText = recoveredLeftText;
        }

        if (EnvironmentRules.IsGeneral(request.Environment))
        {
            if (IsStopped(leftText))
                return CreateStoppedResponse(request, leftText, leftText, tuneModel, planModel, generateModel);

            var generalTunedQuestion = TunedQuestionRefiner.Refine(leftText, request.Environment);
            return await BuildGeneralAnswerResponseAsync(
                request,
                generalTunedQuestion,
                tuneModel,
                planModel,
                generateModel,
                cancellationToken);
        }

        if (!EnvironmentRules.IsSqlServer(request.Environment) && !EnvironmentRules.IsWindows(request.Environment))
        {
            return CreateStoppedResponse(
                request,
                leftText,
                "MISMATCH: NEEDS_CLARIFICATION - Unsupported environment. Use General, SqlServer_Live, or Windows_Live.",
                tuneModel,
                planModel,
                generateModel);
        }

        if (EnvironmentRules.IsWindows(request.Environment))
        {
            return generateModel.Generator == 2
                ? await BuildTemplateFirstWindowsAsync(request, leftText, tuneModel, planModel, generateModel, cancellationToken)
                : await BuildLlmOnlyWindowsAsync(request, leftText, tuneModel, planModel, generateModel, cancellationToken);
        }

        return generateModel.Generator == 2
            ? await BuildTemplateFirstSqlAsync(request, leftText, tuneModel, planModel, generateModel, cancellationToken)
            : await BuildLlmOnlySqlAsync(request, leftText, tuneModel, planModel, generateModel, cancellationToken);
    }

    public async Task<AskApiResponse> ExecuteWithProgressAsync(
        AskApiRequest request,
        IProgressStream progress,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Ask SSE pipeline started. ConversationId={ConversationId}, BearerToken={BearerToken}, Environment={Environment}",
            request.ConversationId,
            request.BearerToken,
            request.Environment);

        // ── REQUEST_POLICY_CHECK (before any LLM call) ─────────────────────
        await progress.EmitAsync(ProgressEvent.PhaseStart("POLICY"), cancellationToken);
        var policyDecision = _requestPolicyService.Evaluate(request, request.Question);
        if (!policyDecision.Allowed)
        {
            var policyVerdict = policyDecision.NeedsClarification
                ? $"NEEDS_CLARIFICATION: {policyDecision.Message}"
                : $"BLOCKED: {policyDecision.ReasonCode}";
            await progress.EmitAsync(ProgressEvent.PhaseDone("POLICY", policyVerdict), cancellationToken);
            var blockedResponse = CreatePolicyBlockedResponse(request, policyDecision);
            await progress.EmitAsync(ProgressEvent.Final(blockedResponse), cancellationToken);
            return blockedResponse;
        }
        await progress.EmitAsync(ProgressEvent.PhaseDone("POLICY"), cancellationToken);

        // ── TUNING ───────────────────────────────────────────────────────────
        await progress.EmitAsync(ProgressEvent.PhaseStart("TUNING"), cancellationToken);

        var routedQueryCode = string.Empty;
        var tuneModel = _modelSelector.SelectTuneModel();
        var planModel = _modelSelector.SelectPlanModel();
        var generateModel = _modelSelector.SelectGenerateModel();

        var tuneClient = ResolveClient(tuneModel.Provider);
        var tuningPrompt = PromptTemplates.Tuning
            .Replace("{{$rawUserQuestion}}", request.Question, StringComparison.Ordinal)
            .Replace("{{$environmentTag}}", request.Environment, StringComparison.Ordinal)
            .Replace("{{$routedQueryCode}}", routedQueryCode, StringComparison.Ordinal);

        var tunedLine = await tuneClient.TuneAsync(
            tuningPrompt,
            request.Question,
            request.Environment,
            routedQueryCode,
            tuneModel.ModelKey,
            cancellationToken);

        var (leftText, _) = ParseTunedLine(tunedLine);
        if (TryRecoverFalseMismatch(leftText, request.Question, request.Environment, out var recoveredLeftText))
        {
            _logger.LogWarning(
                "Recovered false mismatch. Environment={Environment}, Before='{Before}', After='{After}'",
                request.Environment, leftText, recoveredLeftText);
            leftText = recoveredLeftText;
        }

        await progress.EmitAsync(ProgressEvent.PhaseDone("TUNING"), cancellationToken);

        // ── INTENT ───────────────────────────────────────────────────────────
        await progress.EmitAsync(ProgressEvent.PhaseStart("INTENT"), cancellationToken);
        await progress.EmitAsync(ProgressEvent.PhaseDone("INTENT"), cancellationToken);

        // ── MODEL_SELECT ─────────────────────────────────────────────────────
        await progress.EmitAsync(ProgressEvent.PhaseStart("MODEL_SELECT"), cancellationToken);
        await progress.EmitAsync(ProgressEvent.Info(
            $"tuneModel={tuneModel.ModelKey}, planModel={planModel.ModelKey}, generateModel={generateModel.ModelKey}",
            "MODEL_SELECT"), cancellationToken);
        await progress.EmitAsync(ProgressEvent.PhaseDone("MODEL_SELECT"), cancellationToken);

        // ── Route to builder — each emits GENERATE + EXECUTE + ANSWER ────────
        AskApiResponse response;

        if (EnvironmentRules.IsGeneral(request.Environment))
        {
            if (IsStopped(leftText))
            {
                response = CreateStoppedResponse(request, leftText, leftText, tuneModel, planModel, generateModel);
                await progress.EmitAsync(ProgressEvent.Final(response), cancellationToken);
                return response;
            }

            var generalTunedQuestion = TunedQuestionRefiner.Refine(leftText, request.Environment);
            response = await BuildGeneralAnswerResponseAsync(
                request, generalTunedQuestion, tuneModel, planModel, generateModel, cancellationToken, progress);
        }
        else if (!EnvironmentRules.IsSqlServer(request.Environment) && !EnvironmentRules.IsWindows(request.Environment))
        {
            response = CreateStoppedResponse(
                request, leftText,
                "MISMATCH: NEEDS_CLARIFICATION - Unsupported environment. Use General, SqlServer_Live, or Windows_Live.",
                tuneModel, planModel, generateModel);
        }
        else if (EnvironmentRules.IsWindows(request.Environment))
        {
            response = generateModel.Generator == 2
                ? await BuildTemplateFirstWindowsAsync(request, leftText, tuneModel, planModel, generateModel, cancellationToken, progress)
                : await BuildLlmOnlyWindowsAsync(request, leftText, tuneModel, planModel, generateModel, cancellationToken, progress);
        }
        else
        {
            response = generateModel.Generator == 2
                ? await BuildTemplateFirstSqlAsync(request, leftText, tuneModel, planModel, generateModel, cancellationToken, progress)
                : await BuildLlmOnlySqlAsync(request, leftText, tuneModel, planModel, generateModel, cancellationToken, progress);
        }

        await progress.EmitAsync(ProgressEvent.Final(response), cancellationToken);
        return response;
    }

    private async Task<AskApiResponse> BuildGeneralAnswerResponseAsync(
        AskApiRequest request,
        string tunedQuestion,
        LlmModelDefinition tuneModel,
        LlmModelDefinition planModel,
        LlmModelDefinition generateModel,
        CancellationToken cancellationToken,
        IProgressStream? progress = null)
    {
        await (progress ?? NullProgressStream.Instance).EmitAsync(ProgressEvent.PhaseStart("GENERATE"), cancellationToken);

        var explainModel = tuneModel;
        var explainClient = ResolveClient(explainModel.Provider);
        var answerPrompt = PromptTemplates.AnswerOnly
            .Replace("{{$question}}", tunedQuestion, StringComparison.Ordinal);

        var answer = await explainClient.GenerateAsync(
            answerPrompt,
            tunedQuestion,
            request.Environment,
            explainModel.ModelKey,
            cancellationToken);

        var answerText = string.IsNullOrWhiteSpace(answer)
            ? MockLlmBehavior.BuildGeneralAnswer(tunedQuestion)
            : answer.Trim();

        var response = CreateBaseResponse(request, tunedQuestion, tuneModel, planModel, generateModel);
        response.Plan.Mode = "GENERAL";
        response.Result.Kind = "ANSWER_ONLY";
        response.Result.Status = "NOT_EXECUTED";
        response.Result.AnswerText = answerText;

        await (progress ?? NullProgressStream.Instance).EmitAsync(ProgressEvent.PhaseDone("GENERATE"), cancellationToken);
        return response;
    }

    private async Task<AskApiResponse> BuildLlmOnlySqlAsync(
        AskApiRequest request,
        string tunedLineLeftText,
        LlmModelDefinition tuneModel,
        LlmModelDefinition planModel,
        LlmModelDefinition generateModel,
        CancellationToken cancellationToken,
        IProgressStream? progress = null)
    {
        var prog = progress ?? NullProgressStream.Instance;
        await prog.EmitAsync(ProgressEvent.PhaseStart("GENERATE"), cancellationToken);

        // Recover from false-positive BLOCKED: if our own safety check says the question is safe,
        // the LLM gate was too aggressive — proceed with the raw question as the tuned question.
        if (tunedLineLeftText.StartsWith("BLOCKED:", StringComparison.OrdinalIgnoreCase)
            && !IsClearlyDangerousSqlRequest(request.Question))
        {
            _logger.LogWarning(
                "LLM incorrectly blocked safe SQL question; recovering. Question={Question}", request.Question);
            tunedLineLeftText = request.Question;
        }

        var tunedQuestion = ResolveSqlTunedQuestion(tunedLineLeftText, request.Question, request.Environment);
        if (IsStopped(tunedLineLeftText))
        {
            var message = tunedLineLeftText.StartsWith("BLOCKED:", StringComparison.OrdinalIgnoreCase)
                ? BlockedStateChangingRequestMessage
                : tunedLineLeftText;

            await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "stopped"), cancellationToken);
            return CreateStoppedResponse(request, tunedQuestion, message, tuneModel, planModel, generateModel);
        }

        if (IsClearlyDangerousSqlRequest(request.Question) || IsClearlyDangerousSqlRequest(tunedQuestion))
        {
            await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "blocked"), cancellationToken);
            return CreateStoppedResponse(
                request, tunedQuestion, BlockedStateChangingRequestMessage, tuneModel, planModel, generateModel);
        }

        // Generator=1: LLM is 100% responsible for script generation. No templates, no fallbacks.
        var generateClient = ResolveClient(generateModel.Provider);
        var generatePrompt = BuildSqlGeneratePrompt(tunedQuestion, request.Environment);
        var generatedScriptRaw = await generateClient.GenerateAsync(
            generatePrompt,
            tunedQuestion,
            request.Environment,
            generateModel.ModelKey,
            cancellationToken);

        var sanitizedScript = SanitizeGeneratedScript(generatedScriptRaw);
        var generatedScript = NormalizeGeneratedScript(request.Environment, sanitizedScript);

        if (TryFindDangerousCommand(request.Environment, generatedScript, out var blockedToken))
        {
            await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "blocked"), cancellationToken);
            return CreateStoppedResponse(
                request, tunedQuestion,
                $"{BlockedStateChangingRequestMessage} Token={blockedToken}",
                tuneModel, planModel, generateModel);
        }

        if (string.IsNullOrWhiteSpace(generatedScript))
        {
            _logger.LogWarning("SQL generation returned empty script. TunedQuestion={TunedQuestion}", tunedQuestion);
            await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "empty-script"), cancellationToken);
            return CreateStoppedResponse(
                request, tunedQuestion,
                "STOPPED: LLM_GENERATE returned empty script — cannot execute.",
                tuneModel, planModel, generateModel);
        }

        await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE"), cancellationToken);
        return await BuildExecutionResponseAsync(
            request, tunedQuestion, generatedScript,
            "SQL", "EXECUTION_READY", "LLM_GENERATE",
            tuneModel, planModel, generateModel, cancellationToken, prog);
    }

    private async Task<AskApiResponse> BuildLlmOnlyWindowsAsync(
        AskApiRequest request,
        string tunedLineLeftText,
        LlmModelDefinition tuneModel,
        LlmModelDefinition planModel,
        LlmModelDefinition generateModel,
        CancellationToken cancellationToken,
        IProgressStream? progress = null)
    {
        var prog = progress ?? NullProgressStream.Instance;
        await prog.EmitAsync(ProgressEvent.PhaseStart("GENERATE"), cancellationToken);

        // Recover from false-positive BLOCKED: if our own safety check says the question is safe,
        // the LLM gate was too aggressive — proceed with the raw question as the tuned question.
        if (tunedLineLeftText.StartsWith("BLOCKED:", StringComparison.OrdinalIgnoreCase)
            && !IsClearlyDangerousWindowsRequest(request.Question))
        {
            _logger.LogWarning(
                "LLM incorrectly blocked safe Windows question; recovering. Question={Question}", request.Question);
            tunedLineLeftText = request.Question;
        }

        var tunedQuestion = ResolveWindowsTunedQuestion(tunedLineLeftText, request.Question, request.Environment);
        if (IsStopped(tunedLineLeftText))
        {
            var message = tunedLineLeftText.StartsWith("BLOCKED:", StringComparison.OrdinalIgnoreCase)
                ? BlockedStateChangingRequestMessage
                : tunedLineLeftText;
            await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "stopped"), cancellationToken);
            return CreateStoppedResponse(request, tunedQuestion, message, tuneModel, planModel, generateModel);
        }

        if (IsClearlyDangerousWindowsRequest(request.Question) ||
            IsClearlyDangerousWindowsRequest(tunedQuestion))
        {
            await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "blocked"), cancellationToken);
            return CreateStoppedResponse(
                request, tunedQuestion, BlockedStateChangingRequestMessage, tuneModel, planModel, generateModel);
        }

        // Generator=1: LLM is 100% responsible for script generation. No templates, no fallbacks.
        var generateClient = ResolveClient(generateModel.Provider);
        var generatePrompt = BuildWindowsGeneratePrompt(tunedQuestion);
        var generatedScriptRaw = await generateClient.GenerateAsync(
            generatePrompt,
            tunedQuestion,
            request.Environment,
            generateModel.ModelKey,
            cancellationToken);

        var sanitizedScript = SanitizeWindowsGeneratedScript(generatedScriptRaw);
        if (!LooksLikePowerShell(sanitizedScript))
        {
            var retryPrompt =
                $"{generatePrompt}{Environment.NewLine}{Environment.NewLine}Your previous output contained non-script text. Return ONLY raw PowerShell code.";
            var retryScriptRaw = await generateClient.GenerateAsync(
                retryPrompt,
                tunedQuestion,
                request.Environment,
                generateModel.ModelKey,
                cancellationToken);
            sanitizedScript = SanitizeWindowsGeneratedScript(retryScriptRaw);
        }

        sanitizedScript = EnsureWindowsScriptContract(sanitizedScript, tunedQuestion);

        if (TryFindDangerousCommand(request.Environment, sanitizedScript, out var blockedToken))
        {
            await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "blocked"), cancellationToken);
            return CreateStoppedResponse(
                request, tunedQuestion,
                $"{BlockedStateChangingRequestMessage} Token={blockedToken}",
                tuneModel, planModel, generateModel);
        }

        if (string.IsNullOrWhiteSpace(sanitizedScript))
        {
            _logger.LogWarning("Windows PS generation returned empty script. TunedQuestion={TunedQuestion}", tunedQuestion);
            await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "empty-script"), cancellationToken);
            return CreateStoppedResponse(
                request, tunedQuestion,
                "STOPPED: LLM_GENERATE returned empty script — cannot execute.",
                tuneModel, planModel, generateModel);
        }

        await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE"), cancellationToken);
        return await BuildExecutionResponseAsync(
            request, tunedQuestion, sanitizedScript,
            "PS", "EXECUTION_READY", "LLM_GENERATE",
            tuneModel, planModel, generateModel, cancellationToken, prog);
    }

    private async Task<AskApiResponse> BuildTemplateFirstSqlAsync(
        AskApiRequest request,
        string tunedLineLeftText,
        LlmModelDefinition tuneModel,
        LlmModelDefinition planModel,
        LlmModelDefinition generateModel,
        CancellationToken cancellationToken,
        IProgressStream? progress = null)
    {
        var prog = progress ?? NullProgressStream.Instance;
        await prog.EmitAsync(ProgressEvent.PhaseStart("GENERATE"), cancellationToken);

        if (tunedLineLeftText.StartsWith("BLOCKED:", StringComparison.OrdinalIgnoreCase)
            && !IsClearlyDangerousSqlRequest(request.Question))
        {
            _logger.LogWarning(
                "LLM incorrectly blocked safe SQL question; recovering. Question={Question}", request.Question);
            tunedLineLeftText = request.Question;
        }

        var tunedQuestion = ResolveSqlTunedQuestion(tunedLineLeftText, request.Question, request.Environment);
        if (IsStopped(tunedLineLeftText))
        {
            var message = tunedLineLeftText.StartsWith("BLOCKED:", StringComparison.OrdinalIgnoreCase)
                ? BlockedStateChangingRequestMessage
                : tunedLineLeftText;
            await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "stopped"), cancellationToken);
            return CreateStoppedResponse(request, tunedQuestion, message, tuneModel, planModel, generateModel);
        }

        if (IsClearlyDangerousSqlRequest(request.Question) || IsClearlyDangerousSqlRequest(tunedQuestion))
        {
            await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "blocked"), cancellationToken);
            return CreateStoppedResponse(
                request, tunedQuestion, BlockedStateChangingRequestMessage, tuneModel, planModel, generateModel);
        }

        if (HealthScriptGenerator.IsHealthIntent(tunedQuestion) || HealthScriptGenerator.IsHealthIntent(request.Question))
        {
            _logger.LogInformation("Health intent detected for SQL. TunedQuestion={TunedQuestion}", tunedQuestion);
            await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "health-script"), cancellationToken);
            var healthResp = await BuildExecutionResponseAsync(
                request, tunedQuestion, HealthScriptGenerator.GetSqlHealthScript(),
                "SQL", "HEALTH", "UNIFIED_HEALTH", tuneModel, planModel, generateModel, cancellationToken, prog);
            healthResp.Plan.GeneratorMode = "TEMPLATE_OR_LLM";
            if (healthResp.Script is not null)
                healthResp.Script.Source = "LLM";
            return healthResp;
        }

        // ── Template lookup ──────────────────────────────────────────────────
        var toolResult = await _toolRegistryResolver.ResolveBestToolAsync(
            request.Environment, tunedQuestion, cancellationToken);

        if (toolResult.Found
            && !string.IsNullOrWhiteSpace(toolResult.ScriptTemplate)
            && !string.IsNullOrWhiteSpace(toolResult.ScriptLanguage))
        {
            _logger.LogInformation(
                "Template hit: QueryCode={QueryCode}, ToolName={ToolName}, Score={Score}",
                toolResult.QueryCode, toolResult.ToolName, toolResult.Score);

            var renderResult = _templateRenderer.Render(new TemplateRenderRequest
            {
                QueryCode = toolResult.QueryCode,
                Environment = request.Environment,
                ScriptLanguage = toolResult.ScriptLanguage,
                ScriptTemplate = toolResult.ScriptTemplate,
                ParameterSchemaJson = toolResult.ParameterSchema,
                RawQuestion = request.Question,
                TunedQuestion = tunedQuestion
            });

            if (renderResult.Success && !string.IsNullOrWhiteSpace(renderResult.RenderedScript))
            {
                if (TryFindDangerousCommand(request.Environment, renderResult.RenderedScript, out var blockedToken))
                {
                    _logger.LogWarning(
                        "Template script blocked. QueryCode={QueryCode}, Token={Token}",
                        toolResult.QueryCode, blockedToken);
                    await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "blocked"), cancellationToken);
                    return CreateStoppedResponse(
                        request, tunedQuestion,
                        $"{BlockedStateChangingRequestMessage} Token={blockedToken}",
                        tuneModel, planModel, generateModel);
                }

                await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "template-hit"), cancellationToken);
                var templateResp = await BuildExecutionResponseAsync(
                    request, tunedQuestion, renderResult.RenderedScript,
                    toolResult.ScriptLanguage, "EXECUTION_READY", "TEMPLATE",
                    tuneModel, planModel, generateModel, cancellationToken, prog);
                templateResp.Plan.GeneratorMode = "TEMPLATE_OR_LLM";
                templateResp.Plan.TemplateHit = true;
                templateResp.Plan.QueryCode = toolResult.QueryCode;
                if (templateResp.Script is not null)
                {
                    templateResp.Script.Source = "TEMPLATE";
                    templateResp.Script.Parameters = renderResult.BoundParameters.Count > 0
                        ? new Dictionary<string, object?>(renderResult.BoundParameters, StringComparer.OrdinalIgnoreCase)
                        : null;
                }
                return templateResp;
            }

            _logger.LogWarning(
                "Template render failed for QueryCode={QueryCode}, ErrorCode={ErrorCode}. Falling back to LLM.",
                toolResult.QueryCode, renderResult.ErrorCode);
        }
        else
        {
            _logger.LogInformation(
                "Template miss. SelectionMethod={SelectionMethod}. Falling back to LLM.",
                toolResult.SelectionMethod);
        }

        // ── LLM fallback ──────────────────────────────────────────────────────
        var generateClient = ResolveClient(generateModel.Provider);
        var generatePrompt = BuildSqlGeneratePrompt(tunedQuestion, request.Environment);
        var generatedScriptRaw = await generateClient.GenerateAsync(
            generatePrompt, tunedQuestion, request.Environment, generateModel.ModelKey, cancellationToken);

        var sanitizedScript = SanitizeGeneratedScript(generatedScriptRaw);
        if (!LooksLikeSqlScript(sanitizedScript))
            sanitizedScript = BuildFallbackSqlScript(tunedQuestion);

        var generatedScript = NormalizeGeneratedScript(request.Environment, sanitizedScript);
        if (!LooksLikeSqlScript(generatedScript)
            || !HasSqlMandatoryOutputColumns(generatedScript)
            || !IsSqlShapeSafe(generatedScript))
        {
            generatedScript = BuildFallbackSqlScript(tunedQuestion);
        }

        if (TryFindDangerousCommand(request.Environment, generatedScript, out var llmBlockedToken))
        {
            await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "blocked"), cancellationToken);
            return CreateStoppedResponse(
                request, tunedQuestion,
                $"{BlockedStateChangingRequestMessage} Token={llmBlockedToken}",
                tuneModel, planModel, generateModel);
        }

        if (string.IsNullOrWhiteSpace(generatedScript))
        {
            _logger.LogWarning("SQL generation returned empty script. TunedQuestion={TunedQuestion}", tunedQuestion);
            await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "empty-script"), cancellationToken);
            return CreateStoppedResponse(
                request, tunedQuestion,
                "STOPPED: LLM_GENERATE returned empty script — cannot execute.",
                tuneModel, planModel, generateModel);
        }

        await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE"), cancellationToken);
        var llmResp = await BuildExecutionResponseAsync(
            request, tunedQuestion, generatedScript,
            "SQL", "EXECUTION_READY", "LLM_GENERATE",
            tuneModel, planModel, generateModel, cancellationToken, prog);
        llmResp.Plan.GeneratorMode = "TEMPLATE_OR_LLM";
        if (llmResp.Script is not null)
            llmResp.Script.Source = "LLM";
        return llmResp;
    }

    private async Task<AskApiResponse> BuildTemplateFirstWindowsAsync(
        AskApiRequest request,
        string tunedLineLeftText,
        LlmModelDefinition tuneModel,
        LlmModelDefinition planModel,
        LlmModelDefinition generateModel,
        CancellationToken cancellationToken,
        IProgressStream? progress = null)
    {
        var prog = progress ?? NullProgressStream.Instance;
        await prog.EmitAsync(ProgressEvent.PhaseStart("GENERATE"), cancellationToken);

        if (tunedLineLeftText.StartsWith("BLOCKED:", StringComparison.OrdinalIgnoreCase)
            && !IsClearlyDangerousWindowsRequest(request.Question))
        {
            _logger.LogWarning(
                "LLM incorrectly blocked safe Windows question; recovering. Question={Question}", request.Question);
            tunedLineLeftText = request.Question;
        }

        var tunedQuestion = ResolveWindowsTunedQuestion(tunedLineLeftText, request.Question, request.Environment);
        if (IsStopped(tunedLineLeftText))
        {
            var message = tunedLineLeftText.StartsWith("BLOCKED:", StringComparison.OrdinalIgnoreCase)
                ? BlockedStateChangingRequestMessage
                : tunedLineLeftText;
            await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "stopped"), cancellationToken);
            return CreateStoppedResponse(request, tunedQuestion, message, tuneModel, planModel, generateModel);
        }

        if (IsClearlyDangerousWindowsRequest(request.Question) || IsClearlyDangerousWindowsRequest(tunedQuestion))
        {
            await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "blocked"), cancellationToken);
            return CreateStoppedResponse(
                request, tunedQuestion, BlockedStateChangingRequestMessage, tuneModel, planModel, generateModel);
        }

        if (HealthScriptGenerator.IsHealthIntent(tunedQuestion) || HealthScriptGenerator.IsHealthIntent(request.Question))
        {
            _logger.LogInformation("Health intent detected for Windows. TunedQuestion={TunedQuestion}", tunedQuestion);
            await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "health-script"), cancellationToken);
            var healthResp = await BuildExecutionResponseAsync(
                request, tunedQuestion, HealthScriptGenerator.GetWindowsHealthScript(),
                "PS", "HEALTH", "UNIFIED_HEALTH", tuneModel, planModel, generateModel, cancellationToken, prog);
            healthResp.Plan.GeneratorMode = "TEMPLATE_OR_LLM";
            if (healthResp.Script is not null)
                healthResp.Script.Source = "LLM";
            return healthResp;
        }

        // ── Template lookup ──────────────────────────────────────────────────
        var toolResult = await _toolRegistryResolver.ResolveBestToolAsync(
            request.Environment, tunedQuestion, cancellationToken);

        if (toolResult.Found
            && !string.IsNullOrWhiteSpace(toolResult.ScriptTemplate)
            && !string.IsNullOrWhiteSpace(toolResult.ScriptLanguage))
        {
            _logger.LogInformation(
                "Template hit: QueryCode={QueryCode}, ToolName={ToolName}, Score={Score}",
                toolResult.QueryCode, toolResult.ToolName, toolResult.Score);

            var renderResult = _templateRenderer.Render(new TemplateRenderRequest
            {
                QueryCode = toolResult.QueryCode,
                Environment = request.Environment,
                ScriptLanguage = toolResult.ScriptLanguage,
                ScriptTemplate = toolResult.ScriptTemplate,
                ParameterSchemaJson = toolResult.ParameterSchema,
                RawQuestion = request.Question,
                TunedQuestion = tunedQuestion
            });

            if (renderResult.Success && !string.IsNullOrWhiteSpace(renderResult.RenderedScript))
            {
                if (TryFindDangerousCommand(request.Environment, renderResult.RenderedScript, out var blockedToken))
                {
                    _logger.LogWarning(
                        "Template script blocked. QueryCode={QueryCode}, Token={Token}",
                        toolResult.QueryCode, blockedToken);
                    await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "blocked"), cancellationToken);
                    return CreateStoppedResponse(
                        request, tunedQuestion,
                        $"{BlockedStateChangingRequestMessage} Token={blockedToken}",
                        tuneModel, planModel, generateModel);
                }

                await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "template-hit"), cancellationToken);
                var templateResp = await BuildExecutionResponseAsync(
                    request, tunedQuestion, renderResult.RenderedScript,
                    toolResult.ScriptLanguage, "EXECUTION_READY", "TEMPLATE",
                    tuneModel, planModel, generateModel, cancellationToken, prog);
                templateResp.Plan.GeneratorMode = "TEMPLATE_OR_LLM";
                templateResp.Plan.TemplateHit = true;
                templateResp.Plan.QueryCode = toolResult.QueryCode;
                if (templateResp.Script is not null)
                {
                    templateResp.Script.Source = "TEMPLATE";
                    templateResp.Script.Parameters = renderResult.BoundParameters.Count > 0
                        ? new Dictionary<string, object?>(renderResult.BoundParameters, StringComparer.OrdinalIgnoreCase)
                        : null;
                }
                return templateResp;
            }

            _logger.LogWarning(
                "Template render failed for QueryCode={QueryCode}, ErrorCode={ErrorCode}. Falling back to LLM.",
                toolResult.QueryCode, renderResult.ErrorCode);
        }
        else
        {
            _logger.LogInformation(
                "Template miss. SelectionMethod={SelectionMethod}. Falling back to LLM.",
                toolResult.SelectionMethod);
        }

        // ── LLM fallback ──────────────────────────────────────────────────────
        var generateClient = ResolveClient(generateModel.Provider);
        var generatePrompt = BuildWindowsGeneratePrompt(tunedQuestion);
        var generatedScriptRaw = await generateClient.GenerateAsync(
            generatePrompt, tunedQuestion, request.Environment, generateModel.ModelKey, cancellationToken);

        var sanitizedScript = SanitizeWindowsGeneratedScript(generatedScriptRaw);
        if (!LooksLikePowerShell(sanitizedScript))
        {
            var retryPrompt =
                $"{generatePrompt}{Environment.NewLine}{Environment.NewLine}Your previous output contained non-script text. Return ONLY raw PowerShell code.";
            var retryScriptRaw = await generateClient.GenerateAsync(
                retryPrompt, tunedQuestion, request.Environment, generateModel.ModelKey, cancellationToken);
            sanitizedScript = SanitizeWindowsGeneratedScript(retryScriptRaw);
        }

        sanitizedScript = EnsureWindowsScriptContract(sanitizedScript, tunedQuestion);

        if (TryFindDangerousCommand(request.Environment, sanitizedScript, out var llmBlockedToken))
        {
            await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "blocked"), cancellationToken);
            return CreateStoppedResponse(
                request, tunedQuestion,
                $"{BlockedStateChangingRequestMessage} Token={llmBlockedToken}",
                tuneModel, planModel, generateModel);
        }

        if (string.IsNullOrWhiteSpace(sanitizedScript))
        {
            _logger.LogWarning("Windows PS generation returned empty script. TunedQuestion={TunedQuestion}", tunedQuestion);
            await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE", "empty-script"), cancellationToken);
            return CreateStoppedResponse(
                request, tunedQuestion,
                "STOPPED: LLM_GENERATE returned empty script — cannot execute.",
                tuneModel, planModel, generateModel);
        }

        await prog.EmitAsync(ProgressEvent.PhaseDone("GENERATE"), cancellationToken);
        var llmResp = await BuildExecutionResponseAsync(
            request, tunedQuestion, sanitizedScript,
            "PS", "EXECUTION_READY", "LLM_GENERATE",
            tuneModel, planModel, generateModel, cancellationToken, prog);
        llmResp.Plan.GeneratorMode = "TEMPLATE_OR_LLM";
        if (llmResp.Script is not null)
            llmResp.Script.Source = "LLM";
        return llmResp;
    }

    private static string BuildSqlGeneratePrompt(string tunedQuestion, string environment = "SqlServer_Live")
    {
        return PromptTemplates.ScriptGenerateSql
            .Replace("{{$environmentTag}}", environment ?? "SqlServer_Live", StringComparison.Ordinal)
            .Replace("{{$question}}", tunedQuestion ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{$planJson}}", string.Empty, StringComparison.Ordinal);
    }

    private static string BuildWindowsGeneratePrompt(string tunedQuestion)
    {
        return PromptTemplates.WindowsGenerate
            .Replace("{{$question}}", tunedQuestion ?? string.Empty, StringComparison.Ordinal);
    }

    private static string SanitizeGeneratedScript(string generatedScriptRaw)
    {
        var script = generatedScriptRaw ?? string.Empty;
        if (string.IsNullOrWhiteSpace(script))
            return string.Empty;

        var fencedCode = Regex.Match(
            script,
            @"```(?:[^\r\n`]*)\r?\n(?<code>[\s\S]*?)```",
            RegexOptions.IgnoreCase);
        if (fencedCode.Success)
            script = fencedCode.Groups["code"].Value;

        script = script.Replace("```", string.Empty, StringComparison.Ordinal);

        var normalized = script.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        var index = 0;
        while (index < lines.Length)
        {
            var trimmed = lines[index].Trim();
            if (trimmed.Length == 0)
            {
                index++;
                continue;
            }

            if (trimmed.StartsWith("PLAN:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("EXPLANATION:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("STEPS:", StringComparison.OrdinalIgnoreCase))
            {
                index++;
                continue;
            }

            break;
        }

        return string.Join(Environment.NewLine, lines.Skip(index)).Trim();
    }

    private static string SanitizeWindowsGeneratedScript(string generatedScriptRaw)
    {
        var script = SanitizeGeneratedScript(generatedScriptRaw);
        if (string.IsNullOrWhiteSpace(script))
            return string.Empty;

        var normalized = script.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        var firstScriptLine = -1;

        for (var i = 0; i < lines.Length; i++)
        {
            if (LooksLikePowerShellLine(lines[i]))
            {
                firstScriptLine = i;
                break;
            }
        }

        if (firstScriptLine > 0)
            lines = lines.Skip(firstScriptLine).ToArray();

        var cleaned = string.Join(Environment.NewLine, lines).Trim();
        if (ContainsWindowsPreambleMarkers(cleaned))
            return string.Empty;

        return cleaned;
    }

    private static string NormalizeGeneratedScript(string environment, string generatedScriptRaw)
    {
        var script = (generatedScriptRaw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(script))
            return string.Empty;

        if (EnvironmentRules.IsSqlServer(environment))
        {
            script = Regex.Replace(script, @"^\s*SET\s+NOCOUNT\s+ON;\s*", string.Empty, RegexOptions.IgnoreCase);
            script = CorrectSqlAntiPatterns(script);
        }

        return script.Trim();
    }

    /// <summary>
    /// Corrects known LLM anti-patterns in generated SQL without altering query intent.
    /// Currently fixes: DB_NAME() used as a column value when sys.databases is in the FROM clause.
    /// DB_NAME() always returns the connection database; correct source is the aliased .name column.
    /// </summary>
    private static string CorrectSqlAntiPatterns(string script)
    {
        // If sys.databases is in FROM, DB_NAME() in SELECT is wrong — it always returns the
        // connection database (master), not the iterated row's database name.
        var aliasMatch = SysDatabasesAliasPattern.Match(script);
        if (!aliasMatch.Success || !DbNameColumnPattern.IsMatch(script))
            return script;

        // Determine alias: e.g. "FROM sys.databases d" → "d"; no alias → use bare "name"
        var alias = aliasMatch.Groups[1].Success ? aliasMatch.Groups[1].Value : string.Empty;

        script = DbNameColumnPattern.Replace(script, m =>
        {
            // Preserve the original AS [alias] clause if present
            var columnAlias = m.Groups[1].Value; // e.g. " AS [DatabaseName]"
            var nameExpr = string.IsNullOrEmpty(alias) ? "name" : $"{alias}.name";
            return $"{nameExpr}{columnAlias}";
        });

        return script;
    }

    private static bool LooksLikePowerShellLine(string line)
    {
        var text = (line ?? string.Empty).TrimStart();
        if (text.Length == 0)
            return false;

        return text.StartsWith("Get-", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("Select-Object", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("$", StringComparison.Ordinal)
               || text.StartsWith("param(", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("function", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("foreach", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("try", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("catch", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikePowerShell(string script)
    {
        if (string.IsNullOrWhiteSpace(script))
            return false;

        return script.Contains("Get-", StringComparison.OrdinalIgnoreCase) ||
               script.Contains("Select-Object", StringComparison.OrdinalIgnoreCase) ||
               script.Contains("$", StringComparison.Ordinal) ||
               script.Contains("param(", StringComparison.OrdinalIgnoreCase) ||
               script.Contains("function", StringComparison.OrdinalIgnoreCase) ||
               script.Contains("foreach", StringComparison.OrdinalIgnoreCase) ||
               script.Contains("try", StringComparison.OrdinalIgnoreCase) ||
               script.Contains("catch", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsWindowsPreambleMarkers(string script)
    {
        if (string.IsNullOrWhiteSpace(script))
            return false;

        var lines = script.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("PLAN:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("STEPS:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("EXPLANATION:", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string EnsureWindowsScriptContract(string script, string tunedQuestion)
    {
        var candidate = (script ?? string.Empty).Trim();
        if (!LooksLikePowerShell(candidate))
            return BuildFallbackWindowsScript(tunedQuestion);

        var hasTargetServerParam = Regex.IsMatch(
            candidate,
            @"param\s*\(\s*\[string\]\s*\$TargetServer",
            RegexOptions.IgnoreCase);
        var outputsResult = Regex.IsMatch(candidate, @"\$\s*Result\b", RegexOptions.IgnoreCase);
        var hasMandatoryFields = candidate.Contains("ServerName", StringComparison.OrdinalIgnoreCase)
                                 && candidate.Contains("CapturedAt", StringComparison.OrdinalIgnoreCase)
                                 && candidate.Contains("Status", StringComparison.OrdinalIgnoreCase)
                                 && candidate.Contains("ErrorMessage", StringComparison.OrdinalIgnoreCase);

        if (!hasTargetServerParam || !outputsResult || !hasMandatoryFields)
            return BuildFallbackWindowsScript(tunedQuestion);

        return candidate;
    }

    private static bool LooksLikeSqlScript(string script)
    {
        if (string.IsNullOrWhiteSpace(script))
            return false;

        var normalized = script.TrimStart();
        return Regex.IsMatch(normalized, @"^(DECLARE|WITH|SELECT)\b", RegexOptions.IgnoreCase);
    }

    private static bool HasSqlMandatoryOutputColumns(string script)
    {
        if (string.IsNullOrWhiteSpace(script))
            return false;

        // Only require the two columns that are always correct: ServerName and CapturedAt.
        // DatabaseName is NOT checked here because correct scripts use d.name AS [DatabaseName]
        // (not DB_NAME()) when iterating sys.databases — requiring DB_NAME() would reject them.
        return Regex.IsMatch(script, @"@@SERVERNAME\s+AS\s+\[ServerName\]", RegexOptions.IgnoreCase)
               && Regex.IsMatch(script, @"GETDATE\s*\(\s*\)\s+AS\s+\[CapturedAt\]", RegexOptions.IgnoreCase);
    }

    private static bool IsSqlShapeSafe(string script)
    {
        if (string.IsNullOrWhiteSpace(script))
            return false;

        if (Regex.IsMatch(script, @"(?im)^\s*USE\s+\S+", RegexOptions.IgnoreCase))
            return false;

        if (Regex.IsMatch(script, @"(?im)\bSELECT\s+\*", RegexOptions.IgnoreCase))
            return false;

        if (Regex.IsMatch(script, @"\bTOP\s*\(", RegexOptions.IgnoreCase)
            && !Regex.IsMatch(script, @"\bORDER\s+BY\b", RegexOptions.IgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static bool IsClearlyDangerousSqlRequest(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return Regex.IsMatch(
                   text,
                   @"\b(insert\s+into|update\s+\S+|delete\s+from|merge\s+into|truncate\s+table|drop\s+(table|database|index|view|procedure|proc|login|user|schema|role|function|trigger)|alter\s+(table|database|index|view|procedure|proc|login|user|schema|role|function|trigger|event\s+session)|create\s+(table|database|index|view|procedure|proc|login|user|schema|role|function|trigger)|grant\s+\S+\s+to|revoke\s+\S+\s+from|deny\s+\S+\s+to|kill\s+\d+|reconfigure)\b",
                   RegexOptions.IgnoreCase)
               || Regex.IsMatch(text, @"\b(backup|restore)\s+(database|log)\b", RegexOptions.IgnoreCase)
               || Regex.IsMatch(
                   text,
                   @"\b(xp_cmdshell|sp_configure|sp_OACreate|OPENROWSET|OPENDATASOURCE)\b",
                   RegexOptions.IgnoreCase)
               || Regex.IsMatch(text, @"\bsp_(add|update|delete)_job\b", RegexOptions.IgnoreCase)
               || Regex.IsMatch(
                   text,
                   @"\b(alter\s+event\s+session|xevent\s+(start|stop)|event\s+session\s+(start|stop))\b",
                   RegexOptions.IgnoreCase);
    }

    private static string ResolveSqlTunedQuestion(string tunedLineLeftText, string rawQuestion, string environment)
    {
        var candidate = (tunedLineLeftText ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(candidate)
            || candidate.StartsWith("MISMATCH:", StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith("GENERAL_REFUSAL:", StringComparison.OrdinalIgnoreCase))
        {
            candidate = rawQuestion ?? string.Empty;
        }

        // If the LLM accidentally returned SQL instead of a natural-language sentence, fall back to raw question.
        if (LooksLikeSqlScript(candidate))
            candidate = (rawQuestion ?? string.Empty).Trim();

        var refined = TunedQuestionRefiner.Refine(candidate, environment);
        if (string.IsNullOrWhiteSpace(refined))
            refined = (rawQuestion ?? string.Empty).Trim();
        return refined;
    }

    private static string BuildFallbackSqlScript(string tunedQuestion)
    {
        var question = tunedQuestion ?? string.Empty;

        // Specific helpers that inject dynamic values (day counts, GB thresholds, etc.)
        if (MentionsFailedJobs(question))
            return BuildFailedJobsSqlScript(question);

        if (question.Contains("backup history", StringComparison.OrdinalIgnoreCase)
            || question.Contains("backup", StringComparison.OrdinalIgnoreCase) && question.Contains("history", StringComparison.OrdinalIgnoreCase))
        {
            return BuildBackupHistorySqlScript(question);
        }

        if (MentionsDatabaseSizeComparison(question))
            return BuildDatabaseSizeSqlScript(question);

        // Delegate all other patterns to MockLlmBehavior's comprehensive library.
        var script = MockLlmBehavior.BuildGenerateScript("SqlServer_Live", question);
        return string.IsNullOrWhiteSpace(script) ? BuildDefaultSqlScript(question) : script;
    }

    private static bool MentionsDatabaseSizeComparison(string question) =>
        ContainsAny(question, "database", "databases") &&
        question.Contains("gb", StringComparison.OrdinalIgnoreCase) &&
        ContainsAny(question, "larger than", "greater than", "bigger than", "more than", "> ", ">", "exceed", "over ");

    private static int ResolveRequestedGb(string question)
    {
        var match = Regex.Match(question ?? string.Empty, @"(\d+)\s*gb", RegexOptions.IgnoreCase);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var gb))
            return 10;
        return Math.Max(1, gb);
    }

    private static string BuildDatabaseSizeSqlScript(string question)
    {
        var gb = ResolveRequestedGb(question);
        return $"""
WITH db_size AS (
    SELECT
        mf.database_id,
        SUM(CAST(mf.size AS bigint) * 8192) AS TotalSizeBytes
    FROM sys.master_files AS mf
    GROUP BY mf.database_id
)
SELECT TOP (50)
    @@SERVERNAME AS [ServerName],
    GETDATE() AS [CapturedAt],
    d.name AS [DatabaseName],
    CAST(ds.TotalSizeBytes / (1024.0 * 1024 * 1024) AS decimal(18, 2)) AS [SizeGB]
FROM sys.databases AS d
INNER JOIN db_size AS ds ON d.database_id = ds.database_id
WHERE ds.TotalSizeBytes > CAST({gb} AS bigint) * 1024 * 1024 * 1024
ORDER BY ds.TotalSizeBytes DESC;
""";
    }

    private static string BuildFailedJobsSqlScript(string question)
    {
        var topClause = ShouldLimitResults(question) ? "TOP (50) " : string.Empty;
        return $"""
WITH failed_jobs AS (
    SELECT {topClause}
        j.name AS [JobName],
        msdb.dbo.agent_datetime(h.run_date, h.run_time) AS [FailedAt],
        h.step_id AS [StepId],
        h.step_name AS [StepName],
        h.message AS [Message]
    FROM msdb.dbo.sysjobhistory AS h
    INNER JOIN msdb.dbo.sysjobs AS j ON h.job_id = j.job_id
    WHERE h.run_status = 0
      AND h.step_id > 0
    ORDER BY h.run_date DESC, h.run_time DESC
)
SELECT
    @@SERVERNAME AS [ServerName],
    GETDATE() AS [CapturedAt],
    f.[JobName],
    f.[FailedAt],
    f.[StepId],
    f.[StepName],
    f.[Message]
FROM failed_jobs AS f
ORDER BY f.[FailedAt] DESC;
""";
    }

    private static string BuildBackupHistorySqlScript(string question)
    {
        var days = ResolveRequestedDays(question);
        var topClause = ShouldLimitResults(question) ? "TOP (50) " : string.Empty;
        return $"""
DECLARE @Days int = {days};
WITH backup_history AS (
    SELECT {topClause}
        bs.database_name AS [BackupDatabaseName],
        bs.type AS [BackupType],
        bs.backup_start_date AS [BackupStartDate],
        bs.backup_finish_date AS [BackupFinishDate],
        bs.backup_size AS [BackupSizeBytes],
        bmf.physical_device_name AS [PhysicalDeviceName]
    FROM msdb.dbo.backupset AS bs
    LEFT JOIN msdb.dbo.backupmediafamily AS bmf
        ON bs.media_set_id = bmf.media_set_id
    WHERE bs.backup_finish_date >= DATEADD(DAY, -@Days, GETDATE())
    ORDER BY bs.backup_finish_date DESC
)
SELECT
    @@SERVERNAME AS [ServerName],
    GETDATE() AS [CapturedAt],
    bh.[BackupDatabaseName],
    bh.[BackupType],
    bh.[BackupStartDate],
    bh.[BackupFinishDate],
    bh.[BackupSizeBytes],
    bh.[PhysicalDeviceName]
FROM backup_history AS bh
ORDER BY bh.[BackupFinishDate] DESC;
""";
    }

    private static string BuildDefaultSqlScript(string question)
    {
        var topClause = ShouldLimitResults(question) ? "TOP (50) " : string.Empty;
        return $"""
SELECT {topClause}
    @@SERVERNAME AS [ServerName],
    GETDATE() AS [CapturedAt],
    d.name AS [DatabaseName],
    d.state_desc AS [State],
    d.recovery_model_desc AS [RecoveryModel]
FROM sys.databases AS d
ORDER BY d.name;
""";
    }

    private static bool ShouldLimitResults(string question) =>
        !Regex.IsMatch(question ?? string.Empty, @"\ball\b", RegexOptions.IgnoreCase);

    private static int ResolveRequestedDays(string question)
    {
        var match = Regex.Match(question ?? string.Empty, @"\b(?:last|past|within)\s+(\d+)\s+day", RegexOptions.IgnoreCase);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var days))
            return 7;
        return Math.Clamp(days, 1, 365);
    }

    private static bool IsClearlyDangerousWindowsRequest(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        // Explicitly allow common read-only inventory/diagnostics cmdlets.
        if (Regex.IsMatch(text, @"\b(get-volume|get-ciminstance|get-computerinfo|get-service|get-process|get-winevent|get-eventlog)\b", RegexOptions.IgnoreCase)
            && !Regex.IsMatch(text, @"\b(restart|reboot|shutdown|stop|kill|remove|format|disable|enable)\b", RegexOptions.IgnoreCase))
        {
            return false;
        }

        return Regex.IsMatch(text, @"\b(restart|reboot|shutdown)\s+(server|computer|machine|host)\b", RegexOptions.IgnoreCase)
               || Regex.IsMatch(text, @"\b(start|stop|restart)\s+service\b", RegexOptions.IgnoreCase)
               || Regex.IsMatch(text, @"\b(stop|kill)\s+(process|service)\b", RegexOptions.IgnoreCase)
               || Regex.IsMatch(
                   text,
                   @"\b(remove-item|set-itemproperty|new-itemproperty|restart-computer|stop-computer|start-service|stop-service|restart-service|stop-process|taskkill|format-volume|clear-eventlog|disable-netadapter|new-netfirewallrule|set-netfirewallrule|remove-netfirewallrule|install-\w+|uninstall-\w+)\b",
                   RegexOptions.IgnoreCase);
    }

    private static string ResolveWindowsTunedQuestion(string tunedLineLeftText, string rawQuestion, string environment)
    {
        var candidate = (tunedLineLeftText ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(candidate) ||
            candidate.StartsWith("MISMATCH:", StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith("GENERAL_REFUSAL:", StringComparison.OrdinalIgnoreCase))
        {
            candidate = rawQuestion ?? string.Empty;
        }

        // If the LLM accidentally returned PowerShell code instead of a natural-language sentence, fall back to raw question.
        if (LooksLikePowerShell(candidate))
            candidate = (rawQuestion ?? string.Empty).Trim();

        var refined = TunedQuestionRefiner.Refine(candidate, environment);
        if (string.IsNullOrWhiteSpace(refined))
            refined = (rawQuestion ?? string.Empty).Trim();
        return refined;
    }

    private static string BuildFallbackWindowsScript(string tunedQuestion)
    {
        var question = tunedQuestion ?? string.Empty;

        // Disk/drive: keep inline because of ExtractDriveLetter logic.
        if (Regex.IsMatch(question, @"\b(drive|disk|volume)\b", RegexOptions.IgnoreCase))
        {
            var driveLetter = ExtractDriveLetter(question);
            var driveFilter = string.IsNullOrWhiteSpace(driveLetter)
                ? "$true"
                : $"$_.DeviceID -like '{driveLetter}*'";

            return $$"""
param([string]$TargetServer)
$Result = @()
try {
    if ([string]::IsNullOrWhiteSpace($TargetServer)) { $TargetServer = $env:COMPUTERNAME }
    $items = Get-CimInstance Win32_LogicalDisk -ErrorAction Stop | Where-Object { {{driveFilter}} }
    foreach ($item in $items) {
        $Result += [pscustomobject]@{
            ServerName = $TargetServer
            CapturedAt = Get-Date
            Status = 'OK'
            ErrorMessage = $null
            DeviceID = $item.DeviceID
            VolumeName = $item.VolumeName
            Size = $item.Size
            FreeSpace = $item.FreeSpace
        }
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
""";
        }

        if (Regex.IsMatch(question, @"\b(service|services)\b", RegexOptions.IgnoreCase) &&
            Regex.IsMatch(question, @"\bstopped\b", RegexOptions.IgnoreCase))
        {
            return """
param([string]$TargetServer)
$Result = @()
try {
    if ([string]::IsNullOrWhiteSpace($TargetServer)) { $TargetServer = $env:COMPUTERNAME }
    $items = Get-Service -ErrorAction Stop | Where-Object { $_.Status -eq 'Stopped' }
    foreach ($item in $items) {
        $Result += [pscustomobject]@{
            ServerName = $TargetServer
            CapturedAt = Get-Date
            Status = 'OK'
            ErrorMessage = $null
            ServiceName = $item.Name
            DisplayName = $item.DisplayName
            ServiceStatus = [string]$item.Status
        }
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
""";
        }

        // Delegate all other patterns to MockLlmBehavior's comprehensive library.
        var script = MockLlmBehavior.BuildGenerateScript("Windows_Live", question);
        if (!string.IsNullOrWhiteSpace(script))
            return script;

        return """
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
        BuildNumber = $os.BuildNumber
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
""";
    }

    private static string? ExtractDriveLetter(string question)
    {
        if (string.IsNullOrWhiteSpace(question))
            return null;

        var likeMatch = Regex.Match(question, @"\blike\s+([A-Z]):?\b", RegexOptions.IgnoreCase);
        if (likeMatch.Success)
            return $"{likeMatch.Groups[1].Value.ToUpperInvariant()}:";

        var namedMatch = Regex.Match(question, @"\b(?:drive|volume|disk)\s+(?:name|letter)?\s*([A-Z]):?\b", RegexOptions.IgnoreCase);
        if (namedMatch.Success)
            return $"{namedMatch.Groups[1].Value.ToUpperInvariant()}:";

        return null;
    }

    private static bool TryFindDangerousCommand(string environment, string script, out string blockedToken)
    {
        blockedToken = string.Empty;
        if (string.IsNullOrWhiteSpace(script))
            return false;

        if (EnvironmentRules.IsSqlServer(environment))
        {
            var blockedStatement = Regex.Match(
                script,
                @"(?im)^\s*(INSERT|UPDATE|DELETE|MERGE|TRUNCATE|DROP|ALTER|CREATE|GRANT|REVOKE|DENY|KILL|RECONFIGURE|BACKUP|RESTORE)\b");
            if (blockedStatement.Success)
            {
                blockedToken = blockedStatement.Groups[1].Value.ToUpperInvariant();
                return true;
            }

            foreach (var token in SqlBlockedProcedureTokens)
            {
                if (!Regex.IsMatch(script, $@"\b{Regex.Escape(token)}\b", RegexOptions.IgnoreCase))
                    continue;

                blockedToken = token;
                return true;
            }

            if (Regex.IsMatch(
                    script,
                    @"\bALTER\s+EVENT\s+SESSION\b[\s\S]*?\bSTATE\s*=\s*(START|STOP)\b",
                    RegexOptions.IgnoreCase))
            {
                blockedToken = "ALTER EVENT SESSION STATE";
                return true;
            }

            return false;
        }

        if (!EnvironmentRules.IsWindows(environment))
            return false;

        foreach (var command in WindowsBlockedCommands)
        {
            if (!Regex.IsMatch(script, $@"(?im)^\s*{Regex.Escape(command)}\b", RegexOptions.IgnoreCase))
                continue;

            blockedToken = command;
            return true;
        }

        if (Regex.IsMatch(script, @"(?im)^\s*(Install-[A-Za-z0-9_-]+|Uninstall-[A-Za-z0-9_-]+)\b", RegexOptions.IgnoreCase))
        {
            blockedToken = "Install/Uninstall";
            return true;
        }

        if (Regex.IsMatch(script, @"(?im)^\s*(New-LocalUser|Remove-LocalUser|Set-LocalUser|Add-LocalGroupMember|Remove-LocalGroupMember|net\s+user)\b", RegexOptions.IgnoreCase))
        {
            blockedToken = "LocalUserMutation";
            return true;
        }

        return false;
    }

    private static bool ContainsAny(string value, params string[] candidates)
    {
        return candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MentionsFailedJobs(string text) =>
        ContainsAny(text, "failed jobs", "failed job", "job failures", "jobs failed");

    private ILLMClient ResolveClient(string provider)
    {
        if (_clientsByProvider.TryGetValue(provider, out var client))
            return client;

        throw new InvalidOperationException($"No ILLMClient implementation registered for provider '{provider}'.");
    }

    private static (string LeftText, string? QueryCodeEcho) ParseTunedLine(string tunedLine)
    {
        var raw = (tunedLine ?? string.Empty).Trim();
        if (raw.Length == 0)
            return (string.Empty, null);

        var separatorIndex = raw.IndexOf("||", StringComparison.Ordinal);
        if (separatorIndex < 0)
            return (raw, null);

        var leftText = raw[..separatorIndex].Trim();
        var queryCodeEcho = raw[(separatorIndex + 2)..].Trim();
        return (leftText, queryCodeEcho.Length == 0 ? null : queryCodeEcho);
    }

    private static bool IsStopped(string leftText)
    {
        return leftText.StartsWith("MISMATCH:", StringComparison.OrdinalIgnoreCase)
               || leftText.StartsWith("GENERAL_REFUSAL:", StringComparison.OrdinalIgnoreCase)
               || leftText.StartsWith("BLOCKED:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryRecoverFalseMismatch(
        string leftText,
        string rawQuestion,
        string environment,
        out string recoveredLeftText)
    {
        recoveredLeftText = leftText;
        if (string.IsNullOrWhiteSpace(rawQuestion))
            return false;

        if (leftText.StartsWith("MISMATCH: SQLSERVER_ONLY", StringComparison.OrdinalIgnoreCase)
            && EnvironmentRules.IsSqlServer(environment)
            && ContainsAny(rawQuestion, SqlRescueKeywords))
        {
            var fallbackLine = MockLlmBehavior.BuildTuneLine(rawQuestion, environment, string.Empty);
            var (fallbackLeft, _) = ParseTunedLine(fallbackLine);
            if (!IsStopped(fallbackLeft))
            {
                recoveredLeftText = fallbackLeft;
                return true;
            }
        }

        if (leftText.StartsWith("MISMATCH: WINDOWS_ONLY", StringComparison.OrdinalIgnoreCase)
            && EnvironmentRules.IsWindows(environment)
            && ContainsAny(rawQuestion, WindowsRescueKeywords))
        {
            var fallbackLine = MockLlmBehavior.BuildTuneLine(rawQuestion, environment, string.Empty);
            var (fallbackLeft, _) = ParseTunedLine(fallbackLine);
            if (!IsStopped(fallbackLeft))
            {
                recoveredLeftText = fallbackLeft;
                return true;
            }
        }

        return false;
    }

    private async Task<AskApiResponse> BuildExecutionResponseAsync(
        AskApiRequest request,
        string tunedQuestion,
        string script,
        string scriptLanguage,
        string resolution,
        string dispatchMethod,
        LlmModelDefinition tuneModel,
        LlmModelDefinition planModel,
        LlmModelDefinition generateModel,
        CancellationToken cancellationToken,
        IProgressStream? progress = null)
    {
        _ = resolution;     // kept in signature for call-site clarity
        _ = dispatchMethod; // kept in signature for call-site clarity

        var prog = progress ?? NullProgressStream.Instance;

        var response = CreateBaseResponse(request, tunedQuestion, tuneModel, planModel, generateModel);
        response.Plan.Mode = "LLM_ONLY";
        response.Plan.GeneratorMode = "LLM_ONLY";
        response.Plan.ScriptLanguage = scriptLanguage;
        response.Script = new AskResponseScript
        {
            Final = script,
            Source = "LLM",
            Validation = new AskScriptValidation { IsSafeReadOnly = true }
        };
        response.Result.Kind = "EXECUTION";
        response.Result.Status = "FAILED"; // updated below after execution

        // The orchestrator emits VALIDATE, REPAIR, and EXECUTE phase events itself.
        var executionResponse = await _scriptAutoFixOrchestrator.ExecuteWithAutoFixAsync(
            new ScriptExecutionRequest
            {
                Environment = request.Environment,
                SelectedServers = request.SelectedTargets ?? [],
                TunedQuestion = tunedQuestion,
                ScriptLanguage = scriptLanguage,
                GeneratedScript = script
            },
            prog,
            cancellationToken);

        // Update final script to the post-fix version (may differ from original)
        if (!string.IsNullOrWhiteSpace(executionResponse.FinalScript))
            response.Script.Final = executionResponse.FinalScript;

        PopulateExecutionResult(response.Result, executionResponse);

        // Map retry attempts to top-level retryAttempts node (only when repairs occurred)
        var retryAttempts = MapRetryAttempts(executionResponse.Attempts);
        if (retryAttempts is not null)
            response.RetryAttempts = retryAttempts;

        // Explain step: generate answer node based on execution result.
        await prog.EmitAsync(ProgressEvent.PhaseStart("ANSWER"), cancellationToken);

        response.Answer = await BuildExplainAnswerAsync(
            request,
            tunedQuestion,
            response.Result,
            cancellationToken);

        await prog.EmitAsync(ProgressEvent.PhaseDone("ANSWER"), cancellationToken);

        return response;
    }

    private static List<AskRetryAttempt>? MapRetryAttempts(List<ScriptExecutionAttempt> attempts)
    {
        // Only include when at least one repair attempt occurred
        if (attempts.Count <= 1 && attempts.All(a => a.Phase == "EXECUTE"))
            return null;

        return attempts.Select(a => new AskRetryAttempt
        {
            Attempt = a.Attempt,
            Target = a.Target,
            Phase = a.Phase,
            ScriptHash = a.ScriptHash,
            ScriptPreview = a.ScriptPreview,
            Status = a.Status,
            ErrorType = a.ErrorType,
            ErrorMessage = a.Error,
            RepairedByLlm = a.RepairedByLlm,
            TsUtc = a.TsUtc
        }).ToList();
    }

    private static void PopulateExecutionResult(AskResponseResult result, ScriptExecutionResponse executionResponse)
    {
        var items = new List<AskExecutionItem>();

        foreach (var r in executionResponse.ResultsByServer)
        {
            if (string.Equals(r.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase))
            {
                var rows = r.Rows?.Select(StripNullValues).ToList() ?? [];
                items.Add(new AskExecutionItem
                {
                    Target = r.Server,
                    Status = "SUCCESS",
                    RowCount = rows.Count,
                    Rows = rows.Count > 0 ? rows : null
                });
            }
            else
            {
                // Execution-level failure (connection/timeout) — single error row.
                items.Add(new AskExecutionItem
                {
                    Target = r.Server,
                    Status = "FAILED",
                    RowCount = 0,
                    Rows =
                    [
                        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["ErrorMessage"] = r.Error ?? "Execution failed."
                        }
                    ]
                });
            }
        }

        // Edge case: orchestrator returned no server results but logged an attempt.
        if (items.Count == 0)
        {
            var lastAttempt = executionResponse.Attempts.LastOrDefault();
            if (lastAttempt is not null)
            {
                var isSuccess = string.Equals(lastAttempt.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase);
                items.Add(new AskExecutionItem
                {
                    Target = lastAttempt.Target ?? string.Empty,
                    Status = isSuccess ? "SUCCESS" : "FAILED",
                    RowCount = 0,
                    Rows = isSuccess ? null :
                    [
                        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["ErrorMessage"] = lastAttempt.Error ?? "Execution failed."
                        }
                    ]
                });
            }
        }

        var serverList = executionResponse.ResultsByServer;
        var allSuccess = serverList.Count > 0
            && serverList.All(r => string.Equals(r.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase));
        var anySuccess = serverList.Count > 0
            && serverList.Any(r => string.Equals(r.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase));

        result.Status = allSuccess ? "SUCCESS" : anySuccess ? "PARTIAL_SUCCESS" : "FAILED";
        result.Items = items.Count > 0 ? items : null;
        result.Summary = new AskExecutionSummary
        {
            SuccessCount = executionResponse.Summary.SuccessCount,
            FailCount = executionResponse.Summary.FailCount,
            TotalRowCount = executionResponse.Summary.TotalRowCount,
            DurationMs = executionResponse.ResultsByServer.Sum(r => r.DurationMs)
        };
    }

    /// <summary>Returns a copy of <paramref name="row"/> with all null-valued entries removed.</summary>
    private static Dictionary<string, object?> StripNullValues(Dictionary<string, object?> row) =>
        new(row.Where(kv => kv.Value != null), StringComparer.OrdinalIgnoreCase);

    private async Task<AskAnswerNode> BuildExplainAnswerAsync(
        AskApiRequest request,
        string tunedQuestion,
        AskResponseResult result,
        CancellationToken cancellationToken)
    {
        var highlightsArray = BuildHighlightsArray(result);
        var severity = DeriveSeverity(result, highlightsArray);

        // No useful data when all targets failed — skip LLM call.
        if (string.Equals(result.Status, "FAILED", StringComparison.OrdinalIgnoreCase))
        {
            return new AskAnswerNode
            {
                Status = "FAILED",
                Severity = severity,
                Highlights = highlightsArray.Length > 0 ? highlightsArray : null,
                Explanation = "No data to analyze — all targets failed to execute.",
                Suggestion = "Check server connectivity and review error details in result.items."
            };
        }

        var explainModel = _modelSelector.SelectExplainModel();

        try
        {
            var highlightsText = GenerateHighlights(result);
            var dataSample = BuildDataSample(result);
            var summary = result.Summary is null
                ? string.Empty
                : $"success={result.Summary.SuccessCount}, fail={result.Summary.FailCount}, rows={result.Summary.TotalRowCount}";

            var explainPrompt = PromptTemplates.ExplainAnswer
                .Replace("{{$environment}}", request.Environment ?? string.Empty, StringComparison.Ordinal)
                .Replace("{{$rawQuestion}}", request.Question ?? string.Empty, StringComparison.Ordinal)
                .Replace("{{$tunedQuestion}}", tunedQuestion ?? string.Empty, StringComparison.Ordinal)
                .Replace("{{$resultStatus}}", result.Status ?? string.Empty, StringComparison.Ordinal)
                .Replace("{{$resultSummary}}", summary, StringComparison.Ordinal)
                .Replace("{{$highlights}}", highlightsText, StringComparison.Ordinal)
                .Replace("{{$dataSample}}", dataSample, StringComparison.Ordinal);

            var explainClient = ResolveClient(explainModel.Provider);
            var raw = await explainClient.GenerateAsync(
                explainPrompt,
                tunedQuestion ?? string.Empty,
                request.Environment ?? string.Empty,
                explainModel.ModelKey,
                cancellationToken);

            return ParseExplainResponse(raw, explainModel, highlightsArray, severity, result.Status);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Explain step failed; returning FAILED answer node.");
            return new AskAnswerNode
            {
                Status = "FAILED",
                Severity = severity,
                Highlights = highlightsArray.Length > 0 ? highlightsArray : null,
                Explanation = $"Explanation could not be generated: {ex.Message}"
            };
        }
    }

    private static AskAnswerNode ParseExplainResponse(
        string raw,
        LlmModelDefinition explainModel,
        string[] highlights,
        string severity,
        string? resultStatus)
    {
        var text = (raw ?? string.Empty).Trim();

        // Strip optional markdown fence.
        var fenced = System.Text.RegularExpressions.Regex.Match(
            text, @"```(?:json)?\s*([\s\S]*?)```", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (fenced.Success)
            text = fenced.Groups[1].Value.Trim();

        var answerStatus = string.Equals(resultStatus, "PARTIAL_SUCCESS", StringComparison.OrdinalIgnoreCase)
            ? "PARTIAL"
            : "OK";

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            string? GetStr(string name) =>
                root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
                    ? p.GetString()
                    : null;

            return new AskAnswerNode
            {
                Status = answerStatus,
                Severity = severity,
                Model = new AskModelRef { Provider = explainModel.Provider, ModelKey = explainModel.ModelKey },
                Highlights = highlights.Length > 0 ? highlights : null,
                Explanation = GetStr("explanation"),
                Anomaly = GetStr("anomaly"),
                Analysis = GetStr("analysis"),
                Suggestion = GetStr("suggestion")
            };
        }
        catch
        {
            // LLM returned non-JSON (e.g. script code from mock fallback) — produce a sensible default.
            var explanation = !string.IsNullOrWhiteSpace(text) && !LooksLikeScriptOutput(text)
                ? text
                : "Execution completed. See result.items for the collected data.";
            return new AskAnswerNode
            {
                Status = answerStatus,
                Severity = severity,
                Model = new AskModelRef { Provider = explainModel.Provider, ModelKey = explainModel.ModelKey },
                Highlights = highlights.Length > 0 ? highlights : null,
                Explanation = explanation
            };
        }
    }

    private static readonly Regex ScriptOutputPattern =
        new(@"^(SELECT|WITH|DECLARE)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Returns true when <paramref name="text"/> looks like PS or SQL script rather than an explanation.</summary>
    private static bool LooksLikeScriptOutput(string text)
    {
        var t = text.TrimStart();
        return t.StartsWith("param(", StringComparison.OrdinalIgnoreCase)
               || t.StartsWith('$')
               || ScriptOutputPattern.IsMatch(t);
    }

    private static readonly string[] IdentifierColumns =
    [
        "DeviceID", "DriveLetter", "Drive", "Volume", "Name",
        "JobName", "DatabaseName", "ServiceName", "ProcessName"
    ];

    private static string[] BuildHighlightsArray(AskResponseResult result)
    {
        if (result.Items is null or { Count: 0 })
            return [];

        var lines = new List<string>();

        foreach (var item in result.Items)
        {
            if (string.Equals(item.Status, "FAILED", StringComparison.OrdinalIgnoreCase))
            {
                var errMsg = item.Rows?.FirstOrDefault()
                    ?.TryGetValue("ErrorMessage", out var e) == true ? e?.ToString() : null;
                lines.Add($"{item.Target}: execution failed{(errMsg is null ? string.Empty : $" — {errMsg}")}");
                continue;
            }

            foreach (var row in item.Rows ?? [])
            {
                // Percentage-used highlight
                var pctEntry = row.FirstOrDefault(kv =>
                    kv.Key.Contains("PercentageUsed", StringComparison.OrdinalIgnoreCase)
                    || kv.Key.Contains("PercentFull", StringComparison.OrdinalIgnoreCase));

                if (pctEntry.Key is not null
                    && double.TryParse(pctEntry.Value?.ToString(), System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var pct))
                {
                    var tier = pct >= 95 ? "critical" : pct >= 80 ? "warning" : pct >= 60 ? "elevated" : "healthy";
                    var id = IdentifierColumns
                        .Select(c => row.TryGetValue(c, out var v) ? v?.ToString() : null)
                        .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
                    var label = id is null ? $"PercentageUsed" : id;
                    lines.Add($"{item.Target} {label}: is {pct:0.##}% used ({tier})");
                    continue;
                }

                // Offline / suspect / stopped state
                var stateEntry = row.FirstOrDefault(kv =>
                    string.Equals(kv.Key, "State", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(kv.Key, "ServiceStatus", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(kv.Key, "Status", StringComparison.OrdinalIgnoreCase));

                if (stateEntry.Key is not null)
                {
                    var val = stateEntry.Value?.ToString() ?? string.Empty;
                    if (string.Equals(val, "OFFLINE", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(val, "SUSPECT", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(val, "Stopped", StringComparison.OrdinalIgnoreCase))
                    {
                        lines.Add($"{item.Target}: {stateEntry.Key}={val}");
                        continue;
                    }
                }

                // Inline ErrorMessage
                if (row.TryGetValue("ErrorMessage", out var em) && !string.IsNullOrWhiteSpace(em?.ToString()))
                    lines.Add($"{item.Target}: ErrorMessage={em}");
            }
        }

        return [.. lines];
    }

    private static string DeriveSeverity(AskResponseResult result, string[] highlights)
    {
        if (string.Equals(result.Status, "FAILED", StringComparison.OrdinalIgnoreCase))
            return "UNKNOWN";

        if (highlights.Length == 0)
            return "OK";

        if (highlights.Any(h => h.Contains("(critical)", StringComparison.OrdinalIgnoreCase)
            || h.Contains("OFFLINE", StringComparison.OrdinalIgnoreCase)
            || h.Contains("SUSPECT", StringComparison.OrdinalIgnoreCase)
            || h.Contains("execution failed", StringComparison.OrdinalIgnoreCase)))
            return "CRITICAL";

        if (highlights.Any(h => h.Contains("(warning)", StringComparison.OrdinalIgnoreCase)
            || h.Contains("Stopped", StringComparison.OrdinalIgnoreCase)
            || h.Contains("ErrorMessage=", StringComparison.OrdinalIgnoreCase)))
            return "WARNING";

        if (highlights.Any(h => h.Contains("(elevated)", StringComparison.OrdinalIgnoreCase)))
            return "INFO";

        return "OK";
    }

    private static string GenerateHighlights(AskResponseResult result)
    {
        if (result.Items is null or { Count: 0 })
            return "(none)";

        var lines = new List<string>();

        foreach (var item in result.Items)
        {
            if (string.Equals(item.Status, "FAILED", StringComparison.OrdinalIgnoreCase))
            {
                var errMsg = item.Rows?.FirstOrDefault()
                    ?.TryGetValue("ErrorMessage", out var e) == true ? e?.ToString() : null;
                lines.Add($"[{item.Target}] FAILED: {errMsg}");
                continue;
            }

            foreach (var row in item.Rows ?? [])
            {
                foreach (var kv in row)
                {
                    var key = kv.Key;
                    var val = kv.Value?.ToString() ?? string.Empty;

                    if (key.Contains("PercentageUsed", StringComparison.OrdinalIgnoreCase)
                        && double.TryParse(val, System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var pct)
                        && pct >= 90)
                    {
                        lines.Add($"[{item.Target}] HIGH {key}={val}%");
                        continue;
                    }

                    if ((string.Equals(key, "State", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(key, "ServiceStatus", StringComparison.OrdinalIgnoreCase))
                        && (string.Equals(val, "OFFLINE", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(val, "SUSPECT", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(val, "Stopped", StringComparison.OrdinalIgnoreCase)))
                    {
                        lines.Add($"[{item.Target}] {key}={val}");
                        continue;
                    }

                    if (string.Equals(key, "ErrorMessage", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(val))
                    {
                        lines.Add($"[{item.Target}] ErrorMessage={val}");
                    }
                }
            }
        }

        return lines.Count == 0 ? "(none)" : string.Join(Environment.NewLine, lines);
    }

    private static string BuildDataSample(AskResponseResult result)
    {
        if (result.Items is null or { Count: 0 })
            return "(no data)";

        const int MaxRowsPerTarget = 20;
        var sb = new System.Text.StringBuilder();

        foreach (var item in result.Items)
        {
            sb.Append('[').Append(item.Target).AppendLine("]");
            var rows = item.Rows;
            if (rows is null or { Count: 0 })
                continue;

            var capped = rows.Count > MaxRowsPerTarget ? rows.Take(MaxRowsPerTarget).ToList() : rows;
            foreach (var row in capped)
            {
                sb.Append("  { ");
                sb.Append(string.Join(", ", row.Select(kv => $"{kv.Key}={kv.Value}")));
                sb.AppendLine(" }");
            }

            if (rows.Count > MaxRowsPerTarget)
                sb.AppendLine($"  ... ({rows.Count - MaxRowsPerTarget} more rows truncated)");
        }

        return sb.ToString().Trim();
    }

    private static AskApiResponse CreatePolicyBlockedResponse(
        AskApiRequest request,
        PolicyDecision decision)
    {
        var env = request.Environment ?? string.Empty;
        var isClarify = decision.NeedsClarification;
        var tuningStatus = isClarify ? "NEEDS_CLARIFICATION" : "BLOCKED";
        var resultStatus = isClarify ? "NEEDS_CLARIFICATION" : "STOPPED";
        var message = isClarify
            ? decision.Message ?? "Please clarify your request."
            : $"BLOCKED: {decision.ReasonCode} - {decision.Message}";

        // Build explanation with suggestions for NEEDS_CLARIFICATION
        var answerText = message;
        if (isClarify && decision.ClarifySuggestion1 is not null)
        {
            answerText = $"{message}\n\n1) {decision.ClarifySuggestion1}\n2) {decision.ClarifySuggestion2}";
        }

        return new AskApiResponse
        {
            Meta = new AskResponseMeta
            {
                ConversationId = request.ConversationId ?? string.Empty,
                BearerToken = request.BearerToken ?? string.Empty,
                TimestampUtc = DateTime.UtcNow.ToString("o")
            },
            Request = new AskResponseRequest
            {
                Environment = env,
                Question = request.Question ?? string.Empty,
                SelectedTargets = request.SelectedTargets ?? [],
                TargetType = EnvironmentRules.IsSqlServer(env) ? "SqlServer"
                           : EnvironmentRules.IsWindows(env) ? "Windows"
                           : null
            },
            Tuning = new AskResponseTuning
            {
                TunedQuestion = request.Question ?? string.Empty,
                Status = tuningStatus,
                StopReason = message
            },
            Plan = new AskResponsePlan
            {
                Mode = "ANSWER_ONLY"
            },
            Result = new AskResponseResult
            {
                Kind = "ANSWER_ONLY",
                Status = resultStatus,
                AnswerText = answerText
            },
            Answer = isClarify
                ? new AskAnswerNode
                {
                    Status = "NEEDS_CLARIFICATION",
                    Explanation = message,
                    Suggestion = decision.ClarifySuggestion1 is not null
                        ? $"1) {decision.ClarifySuggestion1}\n2) {decision.ClarifySuggestion2}"
                        : null
                }
                : null
        };
    }

    private static AskApiResponse CreateStoppedResponse(
        AskApiRequest request,
        string tunedQuestion,
        string message,
        LlmModelDefinition tuneModel,
        LlmModelDefinition planModel,
        LlmModelDefinition generateModel)
    {
        var response = CreateBaseResponse(request, tunedQuestion, tuneModel, planModel, generateModel);
        response.Tuning.Status = "STOPPED";
        response.Tuning.StopReason = message;
        response.Plan.Mode = "STOPPED";

        if (EnvironmentRules.IsGeneral(request.Environment))
        {
            response.Result.Kind = "ANSWER_ONLY";
            response.Result.Status = "STOPPED";
            response.Result.AnswerText = message;
            return response;
        }

        response.Result.Kind = "EXECUTION";
        response.Result.Status = "STOPPED";
        return response;
    }

    private static AskApiResponse CreateBaseResponse(
        AskApiRequest request,
        string tunedQuestion,
        LlmModelDefinition tuneModel,
        LlmModelDefinition planModel,
        LlmModelDefinition generateModel)
    {
        _ = planModel; // reserved for future plan-model tracking
        return new AskApiResponse
        {
            Meta = new AskResponseMeta
            {
                ConversationId = request.ConversationId ?? string.Empty,
                BearerToken = request.BearerToken ?? string.Empty,
                TimestampUtc = DateTime.UtcNow.ToString("o")
            },
            Request = new AskResponseRequest
            {
                Environment = request.Environment ?? string.Empty,
                Question = request.Question ?? string.Empty,
                SelectedTargets = request.SelectedTargets ?? [],
                TargetType = EnvironmentRules.IsSqlServer(request.Environment ?? string.Empty) ? "SqlServer"
                           : EnvironmentRules.IsWindows(request.Environment ?? string.Empty) ? "Windows"
                           : null
            },
            Tuning = new AskResponseTuning
            {
                TunedQuestion = tunedQuestion ?? string.Empty,
                Status = "OK",
                Model = new AskModelRef
                {
                    Provider = tuneModel.Provider,
                    ModelKey = tuneModel.ModelKey
                }
            },
            Plan = new AskResponsePlan
            {
                Model = new AskModelRef
                {
                    Provider = generateModel.Provider,
                    ModelKey = generateModel.ModelKey
                }
            },
            Result = new AskResponseResult()
        };
    }

    private sealed class ScriptPlan
    {
        [JsonPropertyName("readOnly")]
        public bool ReadOnly { get; set; }

        [JsonPropertyName("environment")]
        public string Environment { get; set; } = string.Empty;

        [JsonPropertyName("scriptLanguage")]
        public string ScriptLanguage { get; set; } = string.Empty;

        [JsonPropertyName("intent")]
        public string Intent { get; set; } = string.Empty;

        [JsonPropertyName("filters")]
        public List<PlanFilter>? Filters { get; set; } = [];

        [JsonPropertyName("timeWindow")]
        public PlanTimeWindow? TimeWindow { get; set; }

        [JsonPropertyName("needsClarification")]
        public bool NeedsClarification { get; set; }

        [JsonPropertyName("clarificationQuestion")]
        public string? ClarificationQuestion { get; set; }

        [JsonPropertyName("confidence")]
        public double Confidence { get; set; }
    }

    private sealed class PlanFilter
    {
        [JsonPropertyName("field")]
        public string? Field { get; set; }

        [JsonPropertyName("op")]
        public string? Op { get; set; }

        [JsonPropertyName("value")]
        public JsonElement Value { get; set; }

        [JsonPropertyName("unit")]
        public string? Unit { get; set; }
    }

    private sealed class PlanTimeWindow
    {
        [JsonPropertyName("value")]
        public int Value { get; set; }

        [JsonPropertyName("unit")]
        public string Unit { get; set; } = string.Empty;
    }
}
