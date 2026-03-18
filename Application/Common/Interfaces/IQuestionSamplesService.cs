using Application.Common.Models;

namespace Application.Common.Interfaces;

public interface IQuestionSamplesService
{
    Task<QuestionSamplesResponse> GetAsync(
        string environment,
        bool refresh,
        CancellationToken cancellationToken);
}
