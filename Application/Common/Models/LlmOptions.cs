namespace Application.Common.Models;

public sealed record LlmOptions
{
    public string DefaultProvider { get; set; } = "OpenAI";
    public AIOptions OpenAI { get; set; } = null!;
    public AIOptions Gemini { get; set; } = null!;
}

public sealed record AIOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
}