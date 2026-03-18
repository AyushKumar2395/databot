using Application.Common.Models;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace Infrastructure.Repository;

public sealed class KernelFactory(IOptions<LlmOptions> options)
{
    private readonly LlmOptions _llmOptions = options.Value;

    public Kernel CreateOpenAiKernel(string? overrideModel = null, string? overrideApiKey = null)
    {
        var model = overrideModel ?? _llmOptions.OpenAI.ModelId;
        var key = overrideApiKey ?? _llmOptions.OpenAI.ApiKey;

        var builder = Kernel.CreateBuilder()
            .AddOpenAIChatCompletion(modelId: model, apiKey: key);

        return builder.Build();
    }

    public Kernel CreateGeminiKernel(string? overrideModel = null, string? overrideApiKey = null)
    {
        var model = overrideModel ?? _llmOptions.Gemini.ModelId;
        var key = overrideApiKey ?? _llmOptions.Gemini.ApiKey;

        var builder = Kernel.CreateBuilder()
        .AddGoogleAIGeminiChatCompletion(modelId: model, apiKey: key);

        return builder.Build();
    }
}
