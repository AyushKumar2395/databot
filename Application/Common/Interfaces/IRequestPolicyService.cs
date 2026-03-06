using Application.Common.Models;

namespace Application.Common.Interfaces;

/// <summary>
/// Deterministic request-level safety gate.
/// Runs BEFORE any LLM call to block dangerous state-changing requests,
/// sexual content, or environment mismatches.
/// </summary>
public interface IRequestPolicyService
{
    PolicyDecision Evaluate(AskApiRequest request, string tunedQuestionOrRaw);
}

/// <summary>
/// Result of the request policy evaluation.
/// </summary>
public sealed class PolicyDecision
{
    public bool Allowed { get; init; }

    /// <summary>True when the gate needs the user to pick between Windows vs SQL context.</summary>
    public bool NeedsClarification { get; init; }

    /// <summary>"DANGEROUS_ACTION" | "SEXUAL_CONTENT" | "ENV_MISMATCH" | "NEEDS_CLARIFICATION" | null when allowed.</summary>
    public string? ReasonCode { get; init; }

    /// <summary>User-friendly explanation of why the request was blocked or needs clarification.</summary>
    public string? Message { get; init; }

    /// <summary>Example allowed prompts the user could try instead.</summary>
    public string[]? SafeAlternatives { get; init; }

    /// <summary>When ENV_MISMATCH, suggests the correct environment.</summary>
    public string? SuggestedEnvironment { get; init; }

    /// <summary>First clarification suggestion (e.g. "Windows: when did the server reboot last?").</summary>
    public string? ClarifySuggestion1 { get; init; }

    /// <summary>Second clarification suggestion (e.g. "SQL: when did SQL Server service restart?").</summary>
    public string? ClarifySuggestion2 { get; init; }

    public static PolicyDecision Allow() => new() { Allowed = true };

    public static PolicyDecision BlockDangerousAction(string message, string[]? alternatives = null) =>
        new()
        {
            Allowed = false,
            ReasonCode = "DANGEROUS_ACTION",
            Message = message,
            SafeAlternatives = alternatives
        };

    public static PolicyDecision BlockSexualContent(string message) =>
        new()
        {
            Allowed = false,
            ReasonCode = "SEXUAL_CONTENT",
            Message = message
        };

    public static PolicyDecision BlockEnvMismatch(string message, string suggestedEnvironment, string[]? alternatives = null) =>
        new()
        {
            Allowed = false,
            ReasonCode = "ENV_MISMATCH",
            Message = message,
            SuggestedEnvironment = suggestedEnvironment,
            SafeAlternatives = alternatives
        };

    public static PolicyDecision Clarify(string question, string suggestion1, string suggestion2) =>
        new()
        {
            Allowed = false,
            NeedsClarification = true,
            ReasonCode = "NEEDS_CLARIFICATION",
            Message = question,
            ClarifySuggestion1 = suggestion1,
            ClarifySuggestion2 = suggestion2
        };
}
