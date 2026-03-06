namespace Application.Common.Interfaces;

/// <summary>
/// Sends a failed script + error details to LLM for minimal repair.
/// </summary>
public interface IScriptRepairService
{
    /// <summary>
    /// Asks the LLM to fix the script with the smallest possible change.
    /// Returns the repaired script text (code only, no markdown).
    /// </summary>
    Task<string> RepairAsync(
        string environment,
        string tunedQuestion,
        string scriptLanguage,
        string failedScript,
        string errorMessage,
        CancellationToken ct = default);
}
