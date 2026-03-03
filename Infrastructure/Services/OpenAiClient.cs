using Application.Common.Interfaces;
using Application.Common.Models;
using Infrastructure.Repository;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace Infrastructure.Services;

/// <summary>
/// OpenAI LLM client.
/// Uses the selected OpenAI model when API key is configured.
/// Falls back to deterministic mock output when key/call is unavailable.
/// </summary>
public sealed class OpenAiClient(
    IOptions<LlmOptions> llmOptions,
    KernelFactory kernelFactory,
    ILogger<OpenAiClient> logger) : ILLMClient
{
    private readonly LlmOptions _llmOptions = llmOptions.Value;
    private readonly KernelFactory _kernelFactory = kernelFactory;
    private readonly ILogger<OpenAiClient> _logger = logger;

    public string Provider => "OpenAI";

    public async Task<string> TuneAsync(
        string promptTemplate,
        string rawQuestion,
        string environmentTag,
        string routedQueryCode,
        string modelKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_llmOptions.OpenAI.ApiKey))
        {
            _logger.LogWarning(
                "OpenAI API key is not configured. Using deterministic tuning mock for model {ModelKey}.",
                modelKey);

            return MockLlmBehavior.BuildTuneLine(rawQuestion, environmentTag, routedQueryCode);
        }

        try
        {
            _logger.LogInformation(
                "Using OpenAI tuning model {ModelKey}.",
                modelKey);

            var kernel = _kernelFactory.CreateOpenAiKernel(modelKey);
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
                "OpenAI tuning call failed for model {ModelKey}. Falling back to deterministic mock.",
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
        if (string.IsNullOrWhiteSpace(_llmOptions.OpenAI.ApiKey))
        {
            _logger.LogWarning(
                "OpenAI API key is not configured. Using deterministic generate mock for model {ModelKey}.",
                modelKey);

            return MockLlmBehavior.BuildGenerateScript(environmentTag, tunedQuestion);
        }

        try
        {
            _logger.LogInformation(
                "Using OpenAI generate model {ModelKey}.",
                modelKey);

            var kernel = _kernelFactory.CreateOpenAiKernel(modelKey);
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
                "OpenAI generate call failed for model {ModelKey}. Falling back to deterministic mock.",
                modelKey);
            return MockLlmBehavior.BuildGenerateScript(environmentTag, tunedQuestion);
        }
    }
}
