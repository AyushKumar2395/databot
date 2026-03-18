using Application.Common.Models;

namespace Application.Common.Interfaces;

public interface IQuestionSamplesRepository
{
    Task<List<QuestionSampleRow>> GetByEnvironmentAsync(
        string environment,
        CancellationToken cancellationToken);

    Task<QuestionSampleRow?> GetByIdAsync(
        int sampleId,
        string environment,
        CancellationToken cancellationToken);

    /// <summary>Returns the first sample matching the given GroupKey and environment (e.g. the generic "Metrics" sample).</summary>
    Task<QuestionSampleRow?> GetByGroupKeyAsync(
        string groupKey,
        string environment,
        CancellationToken cancellationToken);
}
