using Application.Common.Models;

namespace Application.Common.Interfaces;

/// <summary>
/// Abstraction for emitting pipeline progress events.
/// The SSE implementation writes to HttpResponse.Body;
/// a no-op implementation is used for the non-streaming /api/ask path.
/// </summary>
public interface IProgressStream
{
    /// <summary>Emit a single progress event. Implementations must never throw.</summary>
    ValueTask EmitAsync(ProgressEvent e, CancellationToken ct = default);
}

/// <summary>No-op implementation used by the non-streaming pipeline path.</summary>
public sealed class NullProgressStream : IProgressStream
{
    public static readonly NullProgressStream Instance = new();
    private NullProgressStream() { }
    public ValueTask EmitAsync(ProgressEvent e, CancellationToken ct = default) => ValueTask.CompletedTask;
}
