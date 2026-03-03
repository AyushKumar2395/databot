using System.Text.RegularExpressions;
using Application.Common.Interfaces;
using Application.Common.Models;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

/// <summary>
/// Implements tune -> template lookup -> generate orchestration for /api/ask.
/// </summary>
public sealed class AskPipelineService(
    IModelSelector modelSelector,
    IEnumerable<ILLMClient> llmClients,
    IToolRegistryResolver toolRegistryResolver,
    ILogger<AskPipelineService> logger) : IAskPipelineService
{
    private static readonly string[] SqlForbiddenTokens =
    [
        "DELETE", "UPDATE", "INSERT", "MERGE", "DROP", "ALTER", "TRUNCATE", "xp_cmdshell", "sp_configure"
    ];

    private static readonly string[] PowerShellForbiddenTokens =
    [
        "Remove-Item", "Set-ItemProperty", "Stop-Process", "Restart-Computer", "Format-Volume"
    ];

    private readonly IModelSelector _modelSelector = modelSelector;
    private readonly IToolRegistryResolver _toolRegistryResolver = toolRegistryResolver;
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

        var routedQueryCode = string.Empty; // QueryCode router is intentionally disabled for this stage.

        var tuneModel = _modelSelector.SelectTuneModel();
        var templateModel = _modelSelector.SelectTemplateFindModel();
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

        var (leftText, queryCodeEcho) = ParseTunedLine(tunedLine);

        if (IsStopped(leftText))
        {
            _logger.LogInformation("Pipeline stopped after tuning. Status={Status}", leftText);

            var stoppedResponse = CreateBaseResponse(request, leftText, tuneModel, templateModel, generateModel);
            stoppedResponse.QueryCode = null;
            stoppedResponse.Resolution = "STOPPED";
            stoppedResponse.ScriptLanguage = null;
            stoppedResponse.Script = null;
            stoppedResponse.Message = leftText;
            return stoppedResponse;
        }

        var refinedTunedQuestion = TunedQuestionRefiner.Refine(leftText, request.Environment);
        if (!string.Equals(refinedTunedQuestion, leftText, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "Tuned question refined for environment {Environment}. Before='{Before}' After='{After}'",
                request.Environment,
                leftText,
                refinedTunedQuestion);
        }

        leftText = refinedTunedQuestion;

        // NOTE: UseForTemplateFind model flag exists, but resolver is intentionally stubbed for this dev step.
        var templateResolution = await _toolRegistryResolver.ResolveBestToolAsync(
            request.Environment,
            leftText,
            cancellationToken);

        if (templateResolution.Found)
        {
            _logger.LogInformation("ToolRegistry resolved a template. ToolCode={ToolCode}", templateResolution.ToolCode);

            var templateResponse = CreateBaseResponse(request, leftText, tuneModel, templateModel, generateModel);
            templateResponse.QueryCode = NormalizeQueryCode(queryCodeEcho);
            templateResponse.Resolution = "LLM_TUNE + TEMPLATE";
            templateResponse.ScriptLanguage = templateResolution.ScriptLanguage;
            templateResponse.Script = templateResolution.Script;
            templateResponse.Message = null;
            return templateResponse;
        }

        var generateClient = ResolveClient(generateModel.Provider);
        var generatePromptTemplate = EnvironmentRules.IsSqlServer(request.Environment)
            ? PromptTemplates.SqlGenerate
            : PromptTemplates.WindowsGenerate;
        var generatePrompt = generatePromptTemplate.Replace("{{$task}}", leftText, StringComparison.Ordinal);

        var generatedScript = await generateClient.GenerateAsync(
            generatePrompt,
            leftText,
            request.Environment,
            generateModel.ModelKey,
            cancellationToken);

        var script = generatedScript.Trim();
        if (TryFindForbiddenToken(request.Environment, script, out var blockedToken))
        {
            var blockedMessage =
                $"BLOCKED: GENERATED_SCRIPT_REJECTED - Unsafe token detected in generated script: {blockedToken}";
            _logger.LogWarning("Generated script blocked. Token={Token}", blockedToken);

            var blockedResponse = CreateBaseResponse(request, leftText, tuneModel, templateModel, generateModel);
            blockedResponse.QueryCode = NormalizeQueryCode(queryCodeEcho);
            blockedResponse.Resolution = "STOPPED";
            blockedResponse.ScriptLanguage = null;
            blockedResponse.Script = null;
            blockedResponse.Message = blockedMessage;
            return blockedResponse;
        }

        var generatedResponse = CreateBaseResponse(request, leftText, tuneModel, templateModel, generateModel);
        generatedResponse.QueryCode = NormalizeQueryCode(queryCodeEcho);
        generatedResponse.Resolution = "LLM_TUNE + LLM_GENERATE";
        generatedResponse.ScriptLanguage = EnvironmentRules.ResolveScriptLanguage(request.Environment);
        generatedResponse.Script = string.IsNullOrWhiteSpace(script) ? null : script;
        generatedResponse.Message = null;
        return generatedResponse;
    }

    private ILLMClient ResolveClient(string provider)
    {
        if (_clientsByProvider.TryGetValue(provider, out var client))
            return client;

        throw new InvalidOperationException($"No ILLMClient implementation registered for provider '{provider}'.");
    }

    private static (string LeftText, string? QueryCodeEcho) ParseTunedLine(string tunedLine)
    {
        var parts = (tunedLine ?? string.Empty).Split("||", 2, StringSplitOptions.None);
        var leftText = parts[0].Trim();
        var queryCodeEcho = parts.Length > 1 ? parts[1].Trim() : null;
        return (leftText, queryCodeEcho);
    }

    private static bool IsStopped(string leftText)
    {
        return leftText.StartsWith("MISMATCH:", StringComparison.OrdinalIgnoreCase)
               || leftText.StartsWith("GENERAL_REFUSAL:", StringComparison.OrdinalIgnoreCase)
               || leftText.StartsWith("BLOCKED:", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeQueryCode(string? queryCode)
    {
        return string.IsNullOrWhiteSpace(queryCode) ? null : queryCode;
    }

    private static bool TryFindForbiddenToken(string environment, string script, out string? blockedToken)
    {
        blockedToken = null;
        if (string.IsNullOrWhiteSpace(script))
            return false;

        if (EnvironmentRules.IsSqlServer(environment))
        {
            foreach (var token in SqlForbiddenTokens)
            {
                var pattern = $@"\b{Regex.Escape(token)}\b";
                if (!Regex.IsMatch(script, pattern, RegexOptions.IgnoreCase)) continue;

                blockedToken = token;
                return true;
            }

            return false;
        }

        if (!EnvironmentRules.IsWindows(environment))
            return false;

        foreach (var token in PowerShellForbiddenTokens)
        {
            if (!script.Contains(token, StringComparison.OrdinalIgnoreCase)) continue;

            blockedToken = token;
            return true;
        }

        return false;
    }

    private static AskApiResponse CreateBaseResponse(
        AskApiRequest request,
        string tunedQuestion,
        LlmModelDefinition tuneModel,
        LlmModelDefinition templateModel,
        LlmModelDefinition generateModel)
    {
        return new AskApiResponse
        {
            ConversationId = request.ConversationId,
            UserId = request.UserId,
            Environment = request.Environment,
            RawQuestion = request.Question,
            TunedQuestion = tunedQuestion,
            TuningModel = ToModelInfo(tuneModel),
            TemplateSelectionModel = ToModelInfo(templateModel),
            ScriptGenerationModel = ToModelInfo(generateModel)
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
}
