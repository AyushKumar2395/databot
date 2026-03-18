using System.Text.Json.Serialization;

namespace Application.Common.Models;

public sealed class ScriptExecutionRequest
{
    [JsonPropertyName("environment")]
    public string Environment { get; set; } = string.Empty;

    [JsonPropertyName("selectedServers")]
    public string[] SelectedServers { get; set; } = [];

    [JsonPropertyName("tunedQuestion")]
    public string TunedQuestion { get; set; } = string.Empty;

    [JsonPropertyName("scriptLanguage")]
    public string ScriptLanguage { get; set; } = string.Empty; // SQL | PS

    [JsonPropertyName("generatedScript")]
    public string GeneratedScript { get; set; } = string.Empty;

    /// <summary>When false, the orchestrator skips LLM repair attempts on validation failure. Default true.</summary>
    [JsonPropertyName("allowRepair")]
    public bool AllowRepair { get; set; } = true;

    /// <summary>When true, the orchestrator skips the safety scanner. Used for curated sample scripts from the DB.</summary>
    [JsonPropertyName("skipSafetyScanning")]
    public bool SkipSafetyScanning { get; set; }
}

public sealed class ScriptExecutionResponse
{
    [JsonPropertyName("environment")]
    public string Environment { get; set; } = string.Empty;

    [JsonPropertyName("scriptLanguage")]
    public string ScriptLanguage { get; set; } = string.Empty;

    [JsonPropertyName("tunedQuestion")]
    public string TunedQuestion { get; set; } = string.Empty;

    [JsonPropertyName("finalScript")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FinalScript { get; set; }

    [JsonPropertyName("attempts")]
    public List<ScriptExecutionAttempt> Attempts { get; set; } = [];

    [JsonPropertyName("resultsByServer")]
    public List<ScriptExecutionServerResult> ResultsByServer { get; set; } = [];

    [JsonPropertyName("summary")]
    public ScriptExecutionSummary Summary { get; set; } = new();
}

public sealed class ScriptExecutionAttempt
{
    [JsonPropertyName("attempt")]
    public int Attempt { get; set; }

    [JsonPropertyName("script")]
    public string Script { get; set; } = string.Empty;

    [JsonPropertyName("target")]
    public string Target { get; set; } = string.Empty;

    /// <summary>"EXECUTE" | "REPAIR"</summary>
    [JsonPropertyName("phase")]
    public string Phase { get; set; } = "EXECUTE";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "FAILED"; // FAILED | SUCCESS | BLOCKED

    /// <summary>"SYNTAX" | "CONNECTION" | null</summary>
    [JsonPropertyName("errorType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorType { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }

    [JsonPropertyName("repairedByLlm")]
    public bool RepairedByLlm { get; set; }

    [JsonPropertyName("scriptHash")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ScriptHash { get; set; }

    [JsonPropertyName("scriptPreview")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ScriptPreview { get; set; }

    [JsonPropertyName("tsUtc")]
    public string TsUtc { get; set; } = string.Empty;
}

public sealed class ScriptExecutionServerResult
{
    [JsonPropertyName("server")]
    public string Server { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "FAILED"; // SUCCESS | FAILED

    [JsonPropertyName("rowCount")]
    public int RowCount { get; set; }

    [JsonPropertyName("rows")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Dictionary<string, object?>>? Rows { get; set; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }

    [JsonPropertyName("rawOutput")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RawOutput { get; set; }
}

public sealed class ScriptExecutionSummary
{
    [JsonPropertyName("successCount")]
    public int SuccessCount { get; set; }

    [JsonPropertyName("failCount")]
    public int FailCount { get; set; }

    [JsonPropertyName("totalRowCount")]
    public int TotalRowCount { get; set; }

    [JsonPropertyName("totalTargets")]
    public int TotalTargets { get; set; }
}

public sealed class ScriptExecutionOptions
{
    public int CommandTimeoutSeconds { get; set; } = 60;
    public int MaxDegreeOfParallelism { get; set; } = 5;
    public string? SqlConnectionStringTemplate { get; set; }
    public string PowerShellExecutable { get; set; } = "powershell";
}
