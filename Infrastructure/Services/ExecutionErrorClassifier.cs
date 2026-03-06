using System.Text.RegularExpressions;

namespace Infrastructure.Services;

/// <summary>
/// Classifies execution error messages as SYNTAX (LLM-repairable) or CONNECTION (not repairable).
/// </summary>
internal static class ExecutionErrorClassifier
{
    /// <summary>
    /// SQL Server error numbers that indicate a compile-time / type-mismatch error that an LLM
    /// can likely fix by rewriting the script. Format from SqlExecutor: "Number=NNN; State=S; ..."
    /// </summary>
    private static readonly HashSet<int> SqlRetryableNumbers =
    [
        102,  // Incorrect syntax near
        105,  // Unclosed quotation mark
        156,  // Incorrect syntax near keyword
        195,  // Not a recognized built-in function
        206,  // Operand type clash (e.g. datetime vs int)
        207,  // Invalid column name
        208,  // Invalid object name
        213,  // Column name or number of supplied values does not match
        2812, // Could not find stored procedure
        4104, // Multi-part identifier could not be bound
        4121, // Cannot find either column
        8115, // Arithmetic overflow converting expression to datetime
        8116, // Argument data type is invalid for argument N
        245,  // Conversion failed when converting varchar to int
        257,  // Implicit conversion from data type X to Y is not allowed
    ];

    // SQL keyword-based syntax markers (when error message doesn't include Number=NNN)
    private static readonly string[] SqlSyntaxKeywords =
    [
        "Invalid column name",
        "Invalid object name",
        "Incorrect syntax near",
        "Incorrect syntax",
        "Must declare the scalar variable",
        "Ambiguous column name",
        "is not a valid identifier",
        "Operand type clash",
        "Arithmetic overflow",
        "Conversion failed",
        "data type mismatch",
        "type clash",
        "not a recognized built-in function",
        "syntax error",
        "Could not find",
        "not bound",
        "near '",
    ];

    // PowerShell parser errors
    private static readonly string[] PsSyntaxMarkers =
    [
        "ParserError",
        "Unexpected token",
        "At line:",
        "Missing closing",
        "is not recognized as the name of a cmdlet",
        "unexpected end of input",
        "expression or statement",
    ];

    // Connection / runtime-unreachable errors (do NOT retry with LLM repair)
    private static readonly string[] SqlConnectionMarkers =
    [
        "error: 40",
        "Login failed",
        "A network-related or instance-specific error",
        "Timeout expired",
        "timeout expired",
        "The server was not found",
        "Cannot open server",
        "named pipes provider",
        "TCP Provider",
        "connection was forcibly closed",
        "connection attempt failed",
    ];

    private static readonly string[] PsConnectionMarkers =
    [
        "RPC server unavailable",
        "WinRM cannot process the request",
        "The network path was not found",
        "Access is denied",
        "No logon servers are available",
        "cannot connect",
        "Connection refused",
        "WSManFault",
        "WinRM service",
    ];

    private static readonly Regex NumberPattern =
        new(@"\bNumber=(\d+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string Classify(string? errorMessage, string environment)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
            return "UNKNOWN";

        if (EnvironmentRules.IsSqlServer(environment))
            return ClassifySql(errorMessage);

        return ClassifyPs(errorMessage);
    }

    private static string ClassifySql(string errorMessage)
    {
        // Check connection markers first (they are never retryable)
        foreach (var m in SqlConnectionMarkers)
        {
            if (errorMessage.Contains(m, StringComparison.OrdinalIgnoreCase))
                return "CONNECTION";
        }

        // Parse all Number=NNN occurrences from FormatSqlException output
        foreach (Match match in NumberPattern.Matches(errorMessage))
        {
            if (int.TryParse(match.Groups[1].Value, out var num) && SqlRetryableNumbers.Contains(num))
                return "SYNTAX";
        }

        // Fallback: keyword scan
        foreach (var k in SqlSyntaxKeywords)
        {
            if (errorMessage.Contains(k, StringComparison.OrdinalIgnoreCase))
                return "SYNTAX";
        }

        return "UNKNOWN";
    }

    private static string ClassifyPs(string errorMessage)
    {
        foreach (var m in PsConnectionMarkers)
        {
            if (errorMessage.Contains(m, StringComparison.OrdinalIgnoreCase))
                return "CONNECTION";
        }

        foreach (var m in PsSyntaxMarkers)
        {
            if (errorMessage.Contains(m, StringComparison.OrdinalIgnoreCase))
                return "SYNTAX";
        }

        return "UNKNOWN";
    }

    public static bool IsSyntaxError(string? errorMessage, string environment)
        => Classify(errorMessage, environment) == "SYNTAX";

    public static bool IsConnectionError(string? errorMessage, string environment)
        => Classify(errorMessage, environment) == "CONNECTION";
}
