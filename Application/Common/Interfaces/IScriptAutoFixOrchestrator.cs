using Application.Common.Models;

namespace Application.Common.Interfaces;

public interface IScriptAutoFixOrchestrator
{
    Task<ScriptExecutionResponse> ExecuteAsync(
        ScriptExecutionRequest request,
        CancellationToken cancellationToken);
}
