using Microsoft.SemanticKernel;

namespace Infrastructure.Helpers;

public sealed class PromptFunctionLoader
{
    public static KernelFunction LoadFromFolder(string folderPath, string functionName)
    {
        string path = Path.Combine(folderPath, "skprompt.txt");

        string prompt = File.ReadAllText(path);

        return KernelFunctionFactory.CreateFromPrompt(prompt, functionName: functionName);
    }
}