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
    ILogger<AskPipelineService> logger) : IAskPipelineService
{
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

    private static readonly HashSet<string> AllowedFilterOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        "=",
        "!=",
        ">",
        ">=",
        "<",
        "<=",
        "contains",
        "like",
        "between"
    };

    private static readonly HashSet<string> AllowedTimeUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        "minutes",
        "hours",
        "days"
    };

    private static readonly string[] AdditionalSqlBlockedTokens =
    [
        "CREATE",
        "KILL",
        "EXEC xp_cmdshell"
    ];

    private static readonly string[] AdditionalWindowsBlockedTokens =
    [
        "Set-",
        "New-",
        "shutdown"
    ];

    private readonly IModelSelector _modelSelector = modelSelector;
    private readonly ILogger<AskPipelineService> _logger = logger;

    private readonly IReadOnlyDictionary<string, ILLMClient> _clientsByProvider =
        llmClients.ToDictionary(client => client.Provider, StringComparer.OrdinalIgnoreCase);

    public async Task<AskApiResponse> ExecuteAsync(AskApiRequest request, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Ask pipeline started. ConversationId={ConversationId}, UserId={UserId}, Environment={Environment}",
            request.ConversationId,
            request.UserId,
            request.Environment);

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

        if (EnvironmentRules.IsWindows(request.Environment))
        {
            return await BuildLlmOnlyWindowsAsync(
                request,
                leftText,
                tuneModel,
                planModel,
                generateModel,
                cancellationToken);
        }

        if (IsStopped(leftText))
            return CreateStoppedResponse(request, leftText, leftText, tuneModel, planModel, generateModel);

        var tunedQuestion = TunedQuestionRefiner.Refine(leftText, request.Environment);

        if (!EnvironmentRules.IsSqlServer(request.Environment))
        {
            return CreateStoppedResponse(
                request,
                tunedQuestion,
                "MISMATCH: NEEDS_CLARIFICATION - Unsupported environment. Use General, SqlServer_Live, or Windows_Live.",
                tuneModel,
                planModel,
                generateModel);
        }

        var planClient = ResolveClient(planModel.Provider);
        var planPrompt = BuildPlanPrompt(request.Environment, tunedQuestion);
        var planJsonRaw = await planClient.GenerateAsync(
            planPrompt,
            tunedQuestion,
            request.Environment,
            planModel.ModelKey,
            cancellationToken);

        if (!ParsePlanJson(planJsonRaw, out var plan, out var parseError))
        {
            return CreateStoppedResponse(
                request,
                tunedQuestion,
                $"MISMATCH: NEEDS_CLARIFICATION - {parseError}",
                tuneModel,
                planModel,
                generateModel);
        }

        if (!ValidatePlan(plan!, request.Environment, tunedQuestion, out var planValidationError))
        {
            return CreateStoppedResponse(
                request,
                tunedQuestion,
                planValidationError!,
                tuneModel,
                planModel,
                generateModel);
        }

        var normalizedPlanJson = JsonSerializer.Serialize(plan);
        var generatePrompt = BuildGeneratePrompt(request.Environment, tunedQuestion, normalizedPlanJson);
        var generateClient = ResolveClient(generateModel.Provider);
        var generatedScriptRaw = await generateClient.GenerateAsync(
            generatePrompt,
            tunedQuestion,
            request.Environment,
            generateModel.ModelKey,
            cancellationToken);

        var sanitizedScript = SanitizeGeneratedScript(generatedScriptRaw);
        if (EnvironmentRules.IsWindows(request.Environment) && !LooksLikePowerShell(sanitizedScript))
        {
            var retryPrompt =
                $"{generatePrompt}{Environment.NewLine}{Environment.NewLine}Your previous output contained non-script text. Return ONLY raw PowerShell code.";
            var retryScriptRaw = await generateClient.GenerateAsync(
                retryPrompt,
                tunedQuestion,
                request.Environment,
                generateModel.ModelKey,
                cancellationToken);
            sanitizedScript = SanitizeGeneratedScript(retryScriptRaw);
        }

        var generatedScript = NormalizeGeneratedScript(request.Environment, sanitizedScript);
        if (string.IsNullOrWhiteSpace(generatedScript))
        {
            return CreateStoppedResponse(
                request,
                tunedQuestion,
                "MISMATCH: NEEDS_CLARIFICATION - Script generation returned empty output.",
                tuneModel,
                planModel,
                generateModel);
        }

        if (EnvironmentRules.IsWindows(request.Environment) && !LooksLikePowerShell(generatedScript))
        {
            return CreateStoppedResponse(
                request,
                tunedQuestion,
                "MISMATCH: NEEDS_CLARIFICATION - Script generation returned non-script output.",
                tuneModel,
                planModel,
                generateModel);
        }

        if (TryFindDangerousCommand(request.Environment, generatedScript, out var blockedToken))
        {
            return CreateStoppedResponse(
                request,
                tunedQuestion,
                $"BLOCKED:DANGEROUS_COMMAND:{blockedToken}",
                tuneModel,
                planModel,
                generateModel);
        }

        if (!ValidateGeneratedScript(request.Environment, tunedQuestion, plan!, generatedScript, out var scriptValidationError))
        {
            return CreateStoppedResponse(
                request,
                tunedQuestion,
                scriptValidationError!,
                tuneModel,
                planModel,
                generateModel);
        }

        return CreateExecutionResponse(
            request,
            tunedQuestion,
            generatedScript,
            plan!.ScriptLanguage,
            tuneModel,
            planModel,
            generateModel);
    }

    private async Task<AskApiResponse> BuildGeneralAnswerResponseAsync(
        AskApiRequest request,
        string tunedQuestion,
        LlmModelDefinition tuneModel,
        LlmModelDefinition planModel,
        LlmModelDefinition generateModel,
        CancellationToken cancellationToken)
    {
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
        response.Resolution = "GENERAL_TUNE_ONLY";
        response.ScriptLanguage = null;
        response.Script = null;
        response.Message = null;
        response.Llm.GeneratedScript = null;
        response.Llm.ModelKey = explainModel.ModelKey;
        response.Llm.Provider = explainModel.Provider;
        response.Result.Kind = "ANSWER_ONLY";
        response.Result.AnswerText = answerText;
        response.Result.Execution = null;
        response.ExecutionPayload = new AskExecutionPayload
        {
            ExecutionMode = "GENERAL",
            DispatchMethod = "GENERAL_ANSWER",
            Environment = request.Environment,
            SelectedServers = request.SelectedServers,
            QueryCode = null,
            ToolId = null,
            ToolName = null,
            ScriptLanguage = null,
            ScriptTemplate = null,
            RenderedScript = null,
            BoundParameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase),
            ParameterSchema = null,
            OutputSchema = null,
            TemplateSelectionModelKey = planModel.ModelKey,
            ScriptGenerationModelKey = generateModel.ModelKey,
            TemplateSelectionModelProvider = planModel.Provider,
            ScriptGenerationModelProvider = generateModel.Provider
        };
        return response;
    }

    private async Task<AskApiResponse> BuildLlmOnlyWindowsAsync(
        AskApiRequest request,
        string tunedLineLeftText,
        LlmModelDefinition tuneModel,
        LlmModelDefinition planModel,
        LlmModelDefinition generateModel,
        CancellationToken cancellationToken)
    {
        var tunedQuestion = ResolveWindowsTunedQuestion(tunedLineLeftText, request.Question, request.Environment);

        if (tunedLineLeftText.StartsWith("BLOCKED:", StringComparison.OrdinalIgnoreCase) ||
            IsClearlyDangerousWindowsRequest(request.Question) ||
            IsClearlyDangerousWindowsRequest(tunedQuestion))
        {
            var blockedMessage = tunedLineLeftText.StartsWith("BLOCKED:", StringComparison.OrdinalIgnoreCase)
                ? tunedLineLeftText
                : "BLOCKED: DANGEROUS_REQUEST - Only safe read-only diagnostics and inventory questions are allowed.";

            return CreateStoppedResponse(
                request,
                tunedQuestion,
                blockedMessage,
                tuneModel,
                planModel,
                generateModel);
        }

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

        if (!LooksLikePowerShell(sanitizedScript))
            sanitizedScript = BuildFallbackWindowsScript(tunedQuestion);

        if (TryFindDangerousCommand(request.Environment, sanitizedScript, out var blockedToken))
        {
            return CreateStoppedResponse(
                request,
                tunedQuestion,
                $"BLOCKED:DANGEROUS_COMMAND:{blockedToken}",
                tuneModel,
                planModel,
                generateModel);
        }

        return CreateWindowsLlmOnlyResponse(
            request,
            tunedQuestion,
            sanitizedScript,
            tuneModel,
            planModel,
            generateModel);
    }

    private static string BuildPlanPrompt(string environment, string tunedQuestion)
    {
        var template = EnvironmentRules.IsSqlServer(environment)
            ? PromptTemplates.ScriptPlanSql
            : PromptTemplates.ScriptPlanWindows;

        return template.Replace("{{$question}}", tunedQuestion ?? string.Empty, StringComparison.Ordinal);
    }

    private static bool ParsePlanJson(string rawPlanJson, out ScriptPlan? plan, out string error)
    {
        plan = null;
        error = "Unable to parse planner output JSON.";

        if (string.IsNullOrWhiteSpace(rawPlanJson))
        {
            error = "Planner output was empty.";
            return false;
        }

        var candidate = rawPlanJson.Trim();
        if (candidate.StartsWith("```", StringComparison.Ordinal))
        {
            var firstBrace = candidate.IndexOf('{');
            var lastBrace = candidate.LastIndexOf('}');
            if (firstBrace >= 0 && lastBrace > firstBrace)
                candidate = candidate[firstBrace..(lastBrace + 1)];
        }

        try
        {
            plan = JsonSerializer.Deserialize<ScriptPlan>(
                candidate,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
        }
        catch
        {
            error = "Planner output was not valid JSON.";
            return false;
        }

        if (plan is null)
        {
            error = "Planner returned an empty plan object.";
            return false;
        }

        return true;
    }

    private static bool ValidatePlan(
        ScriptPlan plan,
        string requestEnvironment,
        string tunedQuestion,
        out string? error)
    {
        error = null;

        if (!plan.ReadOnly)
        {
            error = "BLOCKED:DANGEROUS_COMMAND:PLAN_NOT_READ_ONLY";
            return false;
        }

        var expectedEnvironment = EnvironmentRules.IsSqlServer(requestEnvironment)
            ? "SqlServer_Live"
            : "Windows_Live";
        var expectedLanguage = EnvironmentRules.IsSqlServer(requestEnvironment) ? "SQL" : "PS";

        if (!string.Equals(plan.Environment, expectedEnvironment, StringComparison.OrdinalIgnoreCase))
        {
            error = $"MISMATCH: NEEDS_CLARIFICATION - Plan environment must be {expectedEnvironment}.";
            return false;
        }

        if (!string.Equals(plan.ScriptLanguage, expectedLanguage, StringComparison.OrdinalIgnoreCase))
        {
            error = $"MISMATCH: NEEDS_CLARIFICATION - Plan scriptLanguage must be {expectedLanguage}.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(plan.Intent))
        {
            error = "MISMATCH: NEEDS_CLARIFICATION - Plan intent is missing.";
            return false;
        }

        if (plan.Confidence < 0 || plan.Confidence > 1)
        {
            error = "MISMATCH: NEEDS_CLARIFICATION - Plan confidence must be between 0 and 1.";
            return false;
        }

        if (plan.NeedsClarification)
        {
            var clarification = string.IsNullOrWhiteSpace(plan.ClarificationQuestion)
                ? "Please clarify missing filters or time window."
                : plan.ClarificationQuestion.Trim();
            error = $"MISMATCH: NEEDS_CLARIFICATION - {clarification}";
            return false;
        }

        var filters = plan.Filters ?? [];
        foreach (var filter in filters)
        {
            if (string.IsNullOrWhiteSpace(filter.Field))
            {
                error = "MISMATCH: NEEDS_CLARIFICATION - Plan filter field is missing.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(filter.Op) || !AllowedFilterOperators.Contains(filter.Op))
            {
                error = $"MISMATCH: NEEDS_CLARIFICATION - Unsupported filter operator '{filter.Op}'.";
                return false;
            }

            if (filter.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                error = $"MISMATCH: NEEDS_CLARIFICATION - Filter '{filter.Field}' is missing value.";
                return false;
            }
        }

        if (plan.TimeWindow is not null)
        {
            if (plan.TimeWindow.Value <= 0 || !AllowedTimeUnits.Contains(plan.TimeWindow.Unit))
            {
                error = "MISMATCH: NEEDS_CLARIFICATION - Invalid timeWindow.";
                return false;
            }
        }

        if (PlanLooksDangerous(plan, requestEnvironment))
        {
            error = "BLOCKED:DANGEROUS_COMMAND:PLAN_DANGEROUS_INTENT";
            return false;
        }

        var missingSignals = FindMissingSignals(tunedQuestion, requestEnvironment, plan);
        if (missingSignals.Count > 0)
        {
            error = $"MISMATCH: NEEDS_CLARIFICATION - {string.Join("; ", missingSignals)}";
            return false;
        }

        return true;
    }

    private static string BuildGeneratePrompt(string environment, string tunedQuestion, string planJson)
    {
        var template = EnvironmentRules.IsSqlServer(environment)
            ? PromptTemplates.ScriptGenerateSql
            : PromptTemplates.WindowsGenerate;

        return template
            .Replace("{{$question}}", tunedQuestion ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{$planJson}}", planJson ?? string.Empty, StringComparison.Ordinal);
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

        if (EnvironmentRules.IsSqlServer(environment) &&
            !script.StartsWith("SET NOCOUNT ON;", StringComparison.OrdinalIgnoreCase))
        {
            script = $"SET NOCOUNT ON;{Environment.NewLine}{script}";
        }

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

    private static bool IsClearlyDangerousWindowsRequest(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return Regex.IsMatch(text, @"\b(restart|reboot|shutdown)\s+(server|computer|machine|host)\b", RegexOptions.IgnoreCase)
               || Regex.IsMatch(text, @"\b(stop|kill)\s+(process|service)\b", RegexOptions.IgnoreCase)
               || Regex.IsMatch(
                   text,
                   @"\b(remove-item|set-itemproperty|restart-computer|stop-computer|stop-process|format-volume|disable-\w+|enable-\w+|new-\w+|set-\w+)\b",
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

        var refined = TunedQuestionRefiner.Refine(candidate, environment);
        if (string.IsNullOrWhiteSpace(refined))
            refined = (rawQuestion ?? string.Empty).Trim();
        return refined;
    }

    private static string BuildFallbackWindowsScript(string tunedQuestion)
    {
        var question = tunedQuestion ?? string.Empty;
        if (Regex.IsMatch(question, @"\b(drive|disk|volume)\b", RegexOptions.IgnoreCase))
        {
            var driveLetter = ExtractDriveLetter(question);
            var driveFilter = string.IsNullOrWhiteSpace(driveLetter)
                ? "$true"
                : $"$_.DeviceID -like '{driveLetter}*'";

            return string.Join(
                Environment.NewLine,
                "$server = $env:COMPUTERNAME",
                "Get-CimInstance Win32_LogicalDisk |",
                $"    Where-Object {{ {driveFilter} }} |",
                "    Select-Object @{Name='Server';Expression={$server}}, DeviceID, VolumeName, Size, FreeSpace");
        }

        if (Regex.IsMatch(question, @"\b(service|services)\b", RegexOptions.IgnoreCase) &&
            Regex.IsMatch(question, @"\bstopped\b", RegexOptions.IgnoreCase))
        {
            return """
$server = $env:COMPUTERNAME
Get-Service |
    Where-Object { $_.Status -eq 'Stopped' } |
    Select-Object @{Name='Server';Expression={$server}}, Name, Status, DisplayName
""";
        }

        return """
$server = $env:COMPUTERNAME
Get-CimInstance Win32_OperatingSystem |
    Select-Object @{Name='Server';Expression={$server}}, Caption, Version, BuildNumber
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
        var extraTokens = EnvironmentRules.IsSqlServer(environment)
            ? AdditionalSqlBlockedTokens
            : AdditionalWindowsBlockedTokens;

        if (SafetyPolicy.TryFindBlockedToken(environment, script, extraTokens, out var token) &&
            !string.IsNullOrWhiteSpace(token))
        {
            blockedToken = token;
            return true;
        }

        if (EnvironmentRules.IsSqlServer(environment) &&
            Regex.IsMatch(script, @"\bEXEC(?:UTE)?\s+xp_cmdshell\b", RegexOptions.IgnoreCase))
        {
            blockedToken = "EXEC xp_cmdshell";
            return true;
        }

        return false;
    }

    private static bool ValidateGeneratedScript(
        string environment,
        string tunedQuestion,
        ScriptPlan plan,
        string script,
        out string? error)
    {
        error = null;

        if (!ScriptSatisfiesPlanFilters(environment, script, plan))
        {
            error = "MISMATCH: NEEDS_CLARIFICATION - Generated script does not enforce all requested filters/timeWindow.";
            return false;
        }

        if (EnvironmentRules.IsSqlServer(environment))
        {
            if (!script.StartsWith("SET NOCOUNT ON;", StringComparison.OrdinalIgnoreCase))
            {
                error = "MISMATCH: NEEDS_CLARIFICATION - SQL script must start with SET NOCOUNT ON;";
                return false;
            }

            if (!Regex.IsMatch(script, @"@@SERVERNAME\s+as\s+\[Server\]", RegexOptions.IgnoreCase))
            {
                error = "MISMATCH: NEEDS_CLARIFICATION - SQL script must include @@SERVERNAME as [Server].";
                return false;
            }

            if (MentionsDatabaseSize(tunedQuestion) &&
                !script.Contains("sys.master_files", StringComparison.OrdinalIgnoreCase))
            {
                error = "MISMATCH: NEEDS_CLARIFICATION - Database size requests require sys.master_files.";
                return false;
            }

            if (MentionsBackupAge(tunedQuestion) &&
                !script.Contains("backupset", StringComparison.OrdinalIgnoreCase))
            {
                error = "MISMATCH: NEEDS_CLARIFICATION - Backup-age requests require msdb backupset.";
                return false;
            }

            if (script.Contains("backupset", StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(script, @"backupset\s+(?:AS\s+\w+\s+)?WITH\s*\(\s*NOLOCK\s*\)", RegexOptions.IgnoreCase))
            {
                error = "MISMATCH: NEEDS_CLARIFICATION - Avoid WITH (NOLOCK) on msdb backupset unless strictly needed.";
                return false;
            }

            if (MentionsFailedJobs(tunedQuestion) &&
                !script.Contains("sysjobhistory", StringComparison.OrdinalIgnoreCase))
            {
                error = "MISMATCH: NEEDS_CLARIFICATION - Failed-job requests require msdb job history.";
                return false;
            }
        }

        if (EnvironmentRules.IsWindows(environment))
        {
            if (!Regex.IsMatch(script, @"\bServer\s*=", RegexOptions.IgnoreCase) &&
                !script.Contains("[Server]", StringComparison.OrdinalIgnoreCase))
            {
                error = "MISMATCH: NEEDS_CLARIFICATION - PowerShell output must include a [Server] property.";
                return false;
            }

            if (MentionsServiceState(tunedQuestion, "stopped") &&
                !Regex.IsMatch(script, @"Get-Service.*Status\s+-eq\s+'Stopped'", RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                error = "MISMATCH: NEEDS_CLARIFICATION - Service state filter 'Stopped' is not enforced.";
                return false;
            }

            if (MentionsDiskThreshold(tunedQuestion) &&
                !script.Contains("Win32_LogicalDisk", StringComparison.OrdinalIgnoreCase) &&
                !script.Contains("Get-PSDrive", StringComparison.OrdinalIgnoreCase) &&
                !script.Contains("Get-Volume", StringComparison.OrdinalIgnoreCase))
            {
                error = "MISMATCH: NEEDS_CLARIFICATION - Disk threshold request requires disk metrics query.";
                return false;
            }

            if (MentionsEventLogTimeWindow(tunedQuestion) &&
                !script.Contains("Get-WinEvent", StringComparison.OrdinalIgnoreCase))
            {
                error = "MISMATCH: NEEDS_CLARIFICATION - Event log time-window request requires Get-WinEvent.";
                return false;
            }
        }

        return true;
    }

    private static bool ScriptSatisfiesPlanFilters(string environment, string script, ScriptPlan plan)
    {
        var filters = plan.Filters ?? [];
        foreach (var filter in filters)
        {
            if (!ScriptContainsFilter(environment, script, filter))
                return false;
        }

        if (plan.TimeWindow is not null && !ScriptContainsTimeWindow(environment, script, plan.TimeWindow))
            return false;

        return true;
    }

    private static bool ScriptContainsFilter(string environment, string script, PlanFilter filter)
    {
        var op = filter.Op?.Trim() ?? string.Empty;
        var valueText = ExtractFilterValueText(filter.Value);

        if (op.Equals("contains", StringComparison.OrdinalIgnoreCase) ||
            op.Equals("like", StringComparison.OrdinalIgnoreCase))
        {
            var hasLike = script.Contains("LIKE", StringComparison.OrdinalIgnoreCase) ||
                          script.Contains("-like", StringComparison.OrdinalIgnoreCase);
            if (!hasLike)
                return false;

            return string.IsNullOrWhiteSpace(valueText) || script.Contains(valueText, StringComparison.OrdinalIgnoreCase);
        }

        if (op.Equals("between", StringComparison.OrdinalIgnoreCase))
        {
            return script.Contains("between", StringComparison.OrdinalIgnoreCase) ||
                   (script.Contains("-ge", StringComparison.OrdinalIgnoreCase) &&
                    script.Contains("-le", StringComparison.OrdinalIgnoreCase));
        }

        return op switch
        {
            ">" => script.Contains(">", StringComparison.Ordinal) || script.Contains("-gt", StringComparison.OrdinalIgnoreCase),
            ">=" => script.Contains(">=", StringComparison.Ordinal) || script.Contains("-ge", StringComparison.OrdinalIgnoreCase),
            "<" => script.Contains("<", StringComparison.Ordinal) || script.Contains("-lt", StringComparison.OrdinalIgnoreCase),
            "<=" => script.Contains("<=", StringComparison.Ordinal) || script.Contains("-le", StringComparison.OrdinalIgnoreCase),
            "=" => script.Contains("=", StringComparison.Ordinal) || script.Contains("-eq", StringComparison.OrdinalIgnoreCase),
            "!=" => script.Contains("!=", StringComparison.Ordinal) || script.Contains("<>", StringComparison.Ordinal) ||
                    script.Contains("-ne", StringComparison.OrdinalIgnoreCase),
            _ => EnvironmentRules.IsWindows(environment)
        };
    }

    private static bool ScriptContainsTimeWindow(string environment, string script, PlanTimeWindow timeWindow)
    {
        if (EnvironmentRules.IsSqlServer(environment))
        {
            if (!script.Contains("DATEADD", StringComparison.OrdinalIgnoreCase))
                return false;

            return timeWindow.Unit switch
            {
                "minutes" => script.Contains("minute", StringComparison.OrdinalIgnoreCase),
                "hours" => script.Contains("hour", StringComparison.OrdinalIgnoreCase),
                "days" => script.Contains("day", StringComparison.OrdinalIgnoreCase),
                _ => false
            };
        }

        if (EnvironmentRules.IsWindows(environment))
        {
            return script.Contains("AddMinutes", StringComparison.OrdinalIgnoreCase) ||
                   script.Contains("AddHours", StringComparison.OrdinalIgnoreCase) ||
                   script.Contains("AddDays", StringComparison.OrdinalIgnoreCase) ||
                   script.Contains("StartTime", StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    private static List<string> FindMissingSignals(string tunedQuestion, string environment, ScriptPlan plan)
    {
        var missing = new List<string>();
        var q = (tunedQuestion ?? string.Empty).ToLowerInvariant();
        var filters = plan.Filters ?? [];

        var requiresGreater = q.Contains('>') || ContainsAny(q, "greater than", "more than", "over", "above");
        var requiresLower = q.Contains('<') || ContainsAny(q, "less than", "under", "below");
        var requiresBetween = q.Contains("between", StringComparison.OrdinalIgnoreCase);
        var requiresContains = q.Contains("contains", StringComparison.OrdinalIgnoreCase) ||
                               q.Contains(" like ", StringComparison.OrdinalIgnoreCase);
        var requiresTimeWindow = Regex.IsMatch(
            q,
            @"\b(last|past|within)\s+\d+\s+(minute|minutes|hour|hours|day|days)\b",
            RegexOptions.IgnoreCase);

        if (requiresGreater && !filters.Any(f => IsAnyOperator(f.Op, ">", ">=")))
            missing.Add("Missing greater-than filter.");

        if (requiresLower && !filters.Any(f => IsAnyOperator(f.Op, "<", "<=")))
            missing.Add("Missing less-than filter.");

        if (requiresBetween && !filters.Any(f => IsAnyOperator(f.Op, "between")))
            missing.Add("Missing between filter.");

        if (requiresContains && !filters.Any(f => IsAnyOperator(f.Op, "contains", "like")))
            missing.Add("Missing contains/like filter.");

        if (requiresTimeWindow && plan.TimeWindow is null)
            missing.Add("Missing timeWindow.");

        if (EnvironmentRules.IsSqlServer(environment))
        {
            if (MentionsDatabaseSize(q) &&
                !filters.Any(f => ContainsAny(f.Field ?? string.Empty, "size", "gb")))
            {
                missing.Add("Missing database size filter.");
            }

            if (MentionsBackupAge(q) &&
                !filters.Any(f => ContainsAny(f.Field ?? string.Empty, "backup", "age")))
            {
                missing.Add("Missing backup age filter.");
            }

            if (MentionsFailedJobs(q) &&
                !filters.Any(f => ContainsAny(f.Field ?? string.Empty, "job", "failed", "status")))
            {
                missing.Add("Missing failed jobs filter.");
            }
        }

        if (EnvironmentRules.IsWindows(environment))
        {
            if (MentionsServiceState(q, "stopped") &&
                !filters.Any(f =>
                    ContainsAny(f.Field ?? string.Empty, "service", "status") &&
                    string.Equals(ExtractFilterValueText(f.Value), "stopped", StringComparison.OrdinalIgnoreCase)))
            {
                missing.Add("Missing service state filter for Stopped.");
            }

            if (MentionsDiskThreshold(q) &&
                !filters.Any(f => ContainsAny(f.Field ?? string.Empty, "disk", "free", "used", "percent", "threshold")))
            {
                missing.Add("Missing disk threshold filter.");
            }

            if (MentionsEventLogTimeWindow(q) && plan.TimeWindow is null)
                missing.Add("Missing event log timeWindow.");
        }

        return missing;
    }

    private static bool PlanLooksDangerous(ScriptPlan plan, string environment)
    {
        var text = $"{plan.Intent} {string.Join(' ', (plan.Filters ?? []).Select(f => $"{f.Field} {f.Op} {ExtractFilterValueText(f.Value)}"))}";
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var dangerousTokens = EnvironmentRules.IsSqlServer(environment)
            ? new[] { "drop", "alter", "delete", "update", "insert", "truncate", "create", "kill", "xp_cmdshell", "sp_configure" }
            : new[] { "remove-item", "stop-process", "restart-computer", "shutdown", "format-volume", "set-", "new-" };

        return dangerousTokens.Any(token => text.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static string ExtractFilterValueText(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty
        };
    }

    private static bool IsAnyOperator(string? source, params string[] expected)
    {
        if (string.IsNullOrWhiteSpace(source))
            return false;

        return expected.Any(op => source.Equals(op, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsAny(string value, params string[] candidates)
    {
        return candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MentionsDatabaseSize(string text) =>
        ContainsAny(text, "database size", "db size", "size gb", "size in gb", "gb");

    private static bool MentionsBackupAge(string text) =>
        ContainsAny(text, "backup age", "last backup", "backup older");

    private static bool MentionsFailedJobs(string text) =>
        ContainsAny(text, "failed jobs", "failed job", "job failures", "jobs failed");

    private static bool MentionsServiceState(string text, string state) =>
        text.Contains("service", StringComparison.OrdinalIgnoreCase) &&
        text.Contains(state, StringComparison.OrdinalIgnoreCase);

    private static bool MentionsDiskThreshold(string text) =>
        text.Contains("disk", StringComparison.OrdinalIgnoreCase) &&
        (text.Contains('>') || text.Contains('<') || ContainsAny(text, "greater than", "less than", "threshold", "percent"));

    private static bool MentionsEventLogTimeWindow(string text) =>
        ContainsAny(text, "event log", "eventlog") &&
        Regex.IsMatch(text, @"\b(last|past|within)\s+\d+\s+(minute|minutes|hour|hours|day|days)\b", RegexOptions.IgnoreCase);

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

    private static AskApiResponse CreateWindowsLlmOnlyResponse(
        AskApiRequest request,
        string tunedQuestion,
        string script,
        LlmModelDefinition tuneModel,
        LlmModelDefinition planModel,
        LlmModelDefinition generateModel)
    {
        var response = CreateBaseResponse(request, tunedQuestion, tuneModel, planModel, generateModel);
        response.Resolution = "LLM_ONLY";
        response.ScriptLanguage = "PS";
        response.Script = script;
        response.Message = null;
        response.TemplateMatch = new TemplateMatchInfo
        {
            Found = false,
            ToolId = null,
            QueryCode = null,
            Score = null,
            Confidence = null,
            SelectionMethod = null
        };
        response.Template = null;
        response.Llm.GeneratedScript = script;
        response.Llm.ModelKey = generateModel.ModelKey;
        response.Llm.Provider = generateModel.Provider;
        response.Result.Kind = "EXECUTION";
        response.Result.AnswerText = null;
        response.Result.Execution = new AskExecutionResultStub();
        response.ExecutionPayload = new AskExecutionPayload
        {
            ExecutionMode = "LLM_ONLY",
            DispatchMethod = "LLM_GENERATE",
            Environment = request.Environment,
            SelectedServers = request.SelectedServers,
            QueryCode = null,
            ToolId = null,
            ToolName = null,
            ScriptLanguage = "PS",
            ScriptTemplate = null,
            RenderedScript = null,
            BoundParameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase),
            ParameterSchema = null,
            OutputSchema = null,
            TemplateSelectionModelKey = planModel.ModelKey,
            ScriptGenerationModelKey = generateModel.ModelKey,
            TemplateSelectionModelProvider = planModel.Provider,
            ScriptGenerationModelProvider = generateModel.Provider
        };
        return response;
    }

    private static AskApiResponse CreateExecutionResponse(
        AskApiRequest request,
        string tunedQuestion,
        string script,
        string? scriptLanguage,
        LlmModelDefinition tuneModel,
        LlmModelDefinition planModel,
        LlmModelDefinition generateModel)
    {
        var response = CreateBaseResponse(request, tunedQuestion, tuneModel, planModel, generateModel);
        response.Resolution = "EXECUTION_READY";
        response.ScriptLanguage = scriptLanguage;
        response.Script = script;
        response.Message = null;
        response.Llm.GeneratedScript = script;
        response.Llm.ModelKey = generateModel.ModelKey;
        response.Llm.Provider = generateModel.Provider;
        response.Result.Kind = "EXECUTION";
        response.Result.AnswerText = null;
        response.Result.Execution = new AskExecutionResultStub();
        response.ExecutionPayload = new AskExecutionPayload
        {
            ExecutionMode = "LLM_ONLY",
            DispatchMethod = "PLAN_THEN_GENERATE",
            Environment = request.Environment,
            SelectedServers = request.SelectedServers,
            QueryCode = null,
            ToolId = null,
            ToolName = null,
            ScriptLanguage = scriptLanguage,
            ScriptTemplate = null,
            RenderedScript = null,
            BoundParameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase),
            ParameterSchema = null,
            OutputSchema = null,
            TemplateSelectionModelKey = planModel.ModelKey,
            ScriptGenerationModelKey = generateModel.ModelKey,
            TemplateSelectionModelProvider = planModel.Provider,
            ScriptGenerationModelProvider = generateModel.Provider
        };
        return response;
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
        response.Resolution = "STOPPED";
        response.Message = message;
        response.ScriptLanguage = null;
        response.Script = null;
        response.Llm.GeneratedScript = null;

        if (EnvironmentRules.IsGeneral(request.Environment))
        {
            response.Result.Kind = "ANSWER_ONLY";
            response.Result.AnswerText = message;
            response.Result.Execution = null;
            response.ExecutionPayload = new AskExecutionPayload
            {
                ExecutionMode = "GENERAL",
                DispatchMethod = "GENERAL_STOPPED",
                Environment = request.Environment,
                SelectedServers = request.SelectedServers,
                QueryCode = null,
                ToolId = null,
                ToolName = null,
                ScriptLanguage = null,
                ScriptTemplate = null,
                RenderedScript = null,
                BoundParameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase),
                ParameterSchema = null,
                OutputSchema = null,
                TemplateSelectionModelKey = planModel.ModelKey,
                ScriptGenerationModelKey = generateModel.ModelKey,
                TemplateSelectionModelProvider = planModel.Provider,
                ScriptGenerationModelProvider = generateModel.Provider
            };
            return response;
        }

        response.Result.Kind = "EXECUTION";
        response.Result.AnswerText = null;
        response.Result.Execution = new AskExecutionResultStub();
        response.ExecutionPayload = new AskExecutionPayload
        {
            ExecutionMode = "STOPPED",
            DispatchMethod = "STOPPED",
            Environment = request.Environment,
            SelectedServers = request.SelectedServers,
            QueryCode = null,
            ToolId = null,
            ToolName = null,
            ScriptLanguage = null,
            ScriptTemplate = null,
            RenderedScript = null,
            BoundParameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase),
            ParameterSchema = null,
            OutputSchema = null,
            TemplateSelectionModelKey = planModel.ModelKey,
            ScriptGenerationModelKey = generateModel.ModelKey,
            TemplateSelectionModelProvider = planModel.Provider,
            ScriptGenerationModelProvider = generateModel.Provider
        };
        return response;
    }

    private static AskApiResponse CreateBaseResponse(
        AskApiRequest request,
        string tunedQuestion,
        LlmModelDefinition tuneModel,
        LlmModelDefinition planModel,
        LlmModelDefinition generateModel)
    {
        return new AskApiResponse
        {
            ConversationId = request.ConversationId,
            UserId = request.UserId,
            Environment = request.Environment,
            SelectedServers = request.SelectedServers,
            RawQuestion = request.Question,
            TunedQuestion = tunedQuestion,
            QueryCode = null,
            SelectionMethod = null,
            Score = null,
            Confidence = null,
            Resolution = string.Empty,
            ScriptLanguage = null,
            Script = null,
            ScriptTemplate = null,
            RenderedScript = null,
            BoundParameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase),
            Message = null,
            TuningModel = ToModelInfo(tuneModel),
            TemplateSelectionModel = ToModelInfo(planModel),
            ScriptGenerationModel = ToModelInfo(generateModel),
            TemplateMatch = null,
            Template = null!,
            Llm = new LlmResponseInfo
            {
                GeneratedScript = null,
                ModelKey = generateModel.ModelKey,
                Provider = generateModel.Provider
            },
            Result = new AskResultInfo
            {
                Kind = string.Empty,
                AnswerText = null,
                Execution = null
            },
            ExecutionPayload = null
        };
    }

    private static PipelineModelInfo ToModelInfo(LlmModelDefinition model)
    {
        return new PipelineModelInfo
        {
            ModelId = model.ModelId,
            DisplayName = model.DisplayName,
            ModelKey = model.ModelKey,
            Provider = model.Provider
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
