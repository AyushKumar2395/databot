namespace Application.Common.Interfaces;

/// <summary>
/// Provider-agnostic LLM client contract used by the pipeline.
/// Each implementation decides whether to execute a real call or return a safe mock.
/// </summary>
public interface ILLMClient
{
    string Provider { get; }

    Task<string> TuneAsync(
        string promptTemplate,
        string rawQuestion,
        string environmentTag,
        string routedQueryCode,
        string modelKey,
        CancellationToken cancellationToken);

    Task<string> GenerateAsync(
        string promptTemplate,
        string tunedQuestion,
        string environmentTag,
        string modelKey,
        CancellationToken cancellationToken);
}
