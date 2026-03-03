using Domain.Enums;
using Infrastructure.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.SemanticKernel;

namespace Infrastructure.Repository;

public sealed class SkPromptRunner(KernelFactory factory, IWebHostEnvironment env)
{
    private readonly KernelFactory _kernelFactory = factory;
    private readonly IWebHostEnvironment _env = env;

    /// <summary>
    /// Tunes the raw question into a deterministic, execution-safe request string.
    /// Output is a single line in the format:
    ///   &lt;TUNED_QUESTION&gt;||&lt;ROUTED_QUERYCODE&gt;
    /// Or if irrelevant:
    ///   MISMATCH: IRRELEVANT_QUESTION||&lt;ROUTED_QUERYCODE&gt;
    /// </summary>
    public async Task<string> TuningQuestionAsync(
        string rawUserQuestion,
        string environmentTag,
        string routedQueryCode,
        AgentSwitcher agentSwitcher,
        CancellationToken ct)
    {
        var kernel = agentSwitcher switch
        {
            AgentSwitcher.GPT5Mini => _kernelFactory.CreateOpenAiKernel(),
            AgentSwitcher.Gemini2_5FlashLite => _kernelFactory.CreateGeminiKernel(),
            _ => throw new ArgumentNullException()
        };

        var folder = Path.Combine(_env.ContentRootPath, "Prompt", "TuningQuestion");

        var fn = PromptFunctionLoader.LoadFromFolder(folder, "TuningQuestion");

        var args = new KernelArguments()
        {
            ["rawUserQuestion"] = rawUserQuestion,
            ["environmentTag"] = environmentTag,
            ["routedQueryCode"] = routedQueryCode ?? string.Empty,
        };

        var result = await kernel.InvokeAsync(fn, args, ct);

        return (result.GetValue<string>() ?? string.Empty).Trim();
    }

    /// <summary>
    /// Generates a READ-ONLY script for the tuned task.
    /// - For <SqlServer_Live>: T-SQL
    /// - Otherwise: PowerShell
    /// Output is CODE ONLY.
    /// </summary>
    public async Task<string> GenerateScriptAsync(
        string environmentTag,
        string task,
        AgentSwitcher agentSwitcher,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(task))
            return string.Empty;

        var kernel = agentSwitcher switch
        {
            AgentSwitcher.GPT5Mini => _kernelFactory.CreateOpenAiKernel(),
            AgentSwitcher.Gemini2_5FlashLite => _kernelFactory.CreateGeminiKernel(),
            _ => throw new ArgumentNullException(nameof(agentSwitcher), "Parameter is required")
        };

        var subFolder = string.Equals(environmentTag, "<SqlServer_Live>", StringComparison.OrdinalIgnoreCase)
            ? "Sql"
            : "Windows";

        var folder = Path.Combine(_env.ContentRootPath, "Prompt", "GenerateScript", subFolder);

        // IMPORTANT: function name should match folder function config (keep as you already have)
        var fn = PromptFunctionLoader.LoadFromFolder(folder, subFolder);

        // Pass BOTH values so the prompt can bind them
        var args = new KernelArguments
        {
            ["task"] = task.Trim(),
            ["environmentTag"] = environmentTag
        };

        var result = await kernel.InvokeAsync(fn, args, ct);
        var script = (result.GetValue<string>() ?? string.Empty).Trim();

        // Optional safety: ensure SQL starts correctly
        if (string.Equals(subFolder, "Sql", StringComparison.OrdinalIgnoreCase))
        {
            var firstToken = script.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                                   .FirstOrDefault() ?? string.Empty;

            if (!firstToken.Equals("SELECT", StringComparison.OrdinalIgnoreCase) &&
                !firstToken.Equals("WITH", StringComparison.OrdinalIgnoreCase) &&
                !firstToken.Equals("DECLARE", StringComparison.OrdinalIgnoreCase))
            {
                // Force failure instead of returning a wrong script
                return string.Empty;
            }
        }

        return script;
    }
}