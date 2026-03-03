using Application.Common.Interfaces;
using Application.Common.Models;
using Infrastructure.Repository;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace Infrastructure.Services;

/// <summary>
/// Gemini LLM client.
/// Uses the selected Gemini model when API key is configured.
/// Falls back to deterministic mock output when key/call is unavailable.
/// </summary>
public sealed class GeminiClient(
    IOptions<LlmOptions> llmOptions,
    KernelFactory kernelFactory,
    ILogger<GeminiClient> logger) : ILLMClient
{
    private readonly LlmOptions _llmOptions = llmOptions.Value;
    private readonly KernelFactory _kernelFactory = kernelFactory;
    private readonly ILogger<GeminiClient> _logger = logger;

    public string Provider => "Gemini";

    public async Task<string> TuneAsync(
        string promptTemplate,
        string rawQuestion,
        string environmentTag,
        string routedQueryCode,
        string modelKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_llmOptions.Gemini.ApiKey))
        {
            _logger.LogWarning(
                "Gemini API key is not configured. Using deterministic tuning mock for model {ModelKey}.",
                modelKey);

            return MockLlmBehavior.BuildTuneLine(rawQuestion, environmentTag, routedQueryCode);
        }

        try
        {
            _logger.LogInformation("Using Gemini tuning model {ModelKey}.", modelKey);

            var kernel = _kernelFactory.CreateGeminiKernel(modelKey);
            var args = new KernelArguments
            {
                ["rawUserQuestion"] = rawQuestion,
                ["environmentTag"] = environmentTag,
                ["routedQueryCode"] = routedQueryCode ?? string.Empty
            };

            var result = await kernel.InvokePromptAsync(promptTemplate, args, cancellationToken: cancellationToken);
            var line = (result.GetValue<string>() ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(line)
                ? MockLlmBehavior.BuildTuneLine(rawQuestion, environmentTag, routedQueryCode ?? string.Empty)
                : line;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Gemini tuning call failed for model {ModelKey}. Falling back to deterministic mock.",
                modelKey);
            return MockLlmBehavior.BuildTuneLine(rawQuestion, environmentTag, routedQueryCode ?? string.Empty);
        }
    }

    public async Task<string> GenerateAsync(
        string promptTemplate,
        string tunedQuestion,
        string environmentTag,
        string modelKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_llmOptions.Gemini.ApiKey))
        {
            _logger.LogWarning(
                "Gemini API key is not configured. Using deterministic generate mock for model {ModelKey}.",
                modelKey);

            return MockLlmBehavior.BuildGenerateScript(environmentTag, tunedQuestion);
        }

        try
        {
            _logger.LogInformation("Using Gemini generate model {ModelKey}.", modelKey);

            var kernel = _kernelFactory.CreateGeminiKernel(modelKey);
            var args = new KernelArguments
            {
                ["task"] = tunedQuestion,
                ["environmentTag"] = environmentTag
            };

            var result = await kernel.InvokePromptAsync(promptTemplate, args, cancellationToken: cancellationToken);
            var script = (result.GetValue<string>() ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(script)
                ? MockLlmBehavior.BuildGenerateScript(environmentTag, tunedQuestion)
                : script;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Gemini generate call failed for model {ModelKey}. Falling back to deterministic mock.",
                modelKey);
            return MockLlmBehavior.BuildGenerateScript(environmentTag, tunedQuestion);
        }
    }
}
