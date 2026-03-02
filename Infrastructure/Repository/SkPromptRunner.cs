using Infrastructure.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.SemanticKernel;

namespace Infrastructure.Repository;

public sealed class SkPromptRunner(KernelFactory factory, IWebHostEnvironment env)
{
    private readonly KernelFactory _kernelFactory = factory;
    private readonly IWebHostEnvironment _env = env;

    public async Task<string> TuningQuestionAsync(string question, string environment, string servers,
        CancellationToken ct)
    {
        var kernel = _kernelFactory.CreateOpenAiKernel();

        var folder = Path.Combine(_env.ContentRootPath, "Prompt", "TuningQuestion");

        var fn = PromptFunctionLoader.LoadFromFolder(folder, "TuningQuestion");

        var args = new KernelArguments()
        {
            ["question"] = question,
            ["environment"] = environment,
            ["servers"] = servers,
        };

        var result = await kernel.InvokeAsync(fn, args, ct);

        return (result.GetValue<string>() ?? string.Empty).Trim();
    }

    public async Task<string> GenerateScriptAsync(string tunedQuestion, string servers, CancellationToken ct)
    {
        var kernel = _kernelFactory.CreateOpenAiKernel();

        var folder = Path.Combine(_env.ContentRootPath, "Prompt", "GenerateScript");

        var fn = PromptFunctionLoader.LoadFromFolder(folder, "GenerateScript");

        var args = new KernelArguments()
        {
            ["tunedQuestion"] = tunedQuestion,
            ["servers"] = servers,
        };

        var result = await kernel.InvokeAsync(fn, args, ct);

        return (result.GetValue<string>() ?? string.Empty).Trim();
    }
}