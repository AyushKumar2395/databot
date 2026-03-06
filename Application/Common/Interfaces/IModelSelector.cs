using Application.Common.Models;

namespace Application.Common.Interfaces;

/// <summary>
/// Resolves which model should be used for each pipeline stage based on flags.
/// </summary>
public interface IModelSelector
{
    LlmModelDefinition SelectTuneModel();
    LlmModelDefinition SelectPlanModel();
    LlmModelDefinition SelectTemplateFindModel();
    LlmModelDefinition SelectValidateModel();
    LlmModelDefinition SelectGenerateModel();
    LlmModelDefinition SelectExplainModel();
}
