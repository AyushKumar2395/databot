using Application.Common.Models;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace Infrastructure.Repository;

public sealed class KernelFactory(IOptions<LlmOptions> options)
{
    private readonly LlmOptions _llmOptions = options.Value;

    public Kernel CreateOpenAiKernel(string? overrideModel = null)
    {
        var model = overrideModel ?? _llmOptions.OpenAI.ModelId;

        var builder = Kernel.CreateBuilder()
            .AddOpenAIChatCompletion(modelId: model, apiKey: _llmOptions.OpenAI.ApiKey);

        return builder.Build();
    }
}