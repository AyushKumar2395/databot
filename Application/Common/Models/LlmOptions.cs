namespace Application.Common.Models;

public sealed record LlmOptions
{
    public string DefaultProvider { get; set; } = "OpenAI";
    public AIOptions OpenAI { get; set; } = new();
    public AIOptions Gemini { get; set; } = new();
}

public sealed record AIOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
}
