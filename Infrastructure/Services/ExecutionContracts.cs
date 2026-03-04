namespace Infrastructure.Services;

internal interface ISqlExecutor
{
    Task<ExecutorRunResult> ExecuteAsync(string server, string script, CancellationToken cancellationToken);
}

internal interface IPowerShellExecutor
{
    Task<ExecutorRunResult> ExecuteAsync(string server, string script, CancellationToken cancellationToken);
}

internal sealed class ExecutorRunResult
{
    public string Server { get; init; } = string.Empty;
    public bool Success { get; init; }
    public bool IsRetryableCompileError { get; init; }
    public string? Error { get; init; }
    public long DurationMs { get; init; }
    public List<Dictionary<string, object?>> Rows { get; init; } = [];
    public string? RawOutput { get; init; }
}
