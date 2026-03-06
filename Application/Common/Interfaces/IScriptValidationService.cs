using Application.Common.Models;

namespace Application.Common.Interfaces;

/// <summary>
/// Validates scripts via compile/parse checks before real execution.
/// SQL: runs on first target to detect syntax/object errors.
/// PS: validates locally via ScriptBlock::Create.
/// </summary>
public interface IScriptValidationService
{
    /// <summary>
    /// Validates a SQL script against the first target using dm_exec_describe_first_result_set
    /// or a TRY/CATCH wrapper. Returns success or a structured error.
    /// </summary>
    Task<ScriptValidationResult> ValidateSqlAsync(string script, string firstTarget, CancellationToken ct = default);

    /// <summary>
    /// Validates a PowerShell script locally using [ScriptBlock]::Create.
    /// Does not connect to any remote target.
    /// </summary>
    Task<ScriptValidationResult> ValidatePowerShellAsync(string script, CancellationToken ct = default);
}

public sealed class ScriptValidationResult
{
    public bool IsValid { get; init; }

    /// <summary>"SYNTAX" | "CONNECTION" | null</summary>
    public string? ErrorType { get; init; }

    /// <summary>Full error text (includes error number/line for SQL, parser message for PS).</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>SQL error number when available (e.g. 207, 208).</summary>
    public int? SqlErrorNumber { get; init; }

    /// <summary>SQL error line when available.</summary>
    public int? SqlErrorLine { get; init; }

    public static ScriptValidationResult Success() => new() { IsValid = true };

    public static ScriptValidationResult SyntaxError(string message, int? errorNumber = null, int? errorLine = null) =>
        new()
        {
            IsValid = false,
            ErrorType = "SYNTAX",
            ErrorMessage = message,
            SqlErrorNumber = errorNumber,
            SqlErrorLine = errorLine
        };

    public static ScriptValidationResult ConnectionError(string message) =>
        new()
        {
            IsValid = false,
            ErrorType = "CONNECTION",
            ErrorMessage = message
        };
}
