using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Application.Common.Interfaces;
using Application.Common.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

/// <summary>
/// Pre-execution compile/parse validation for SQL and PowerShell scripts.
/// SQL: uses sys.dm_exec_describe_first_result_set on first target.
/// PS:  uses local [ScriptBlock]::Create to catch parser errors.
/// </summary>
internal sealed class ScriptValidationService(
    IOptions<ScriptExecutionOptions> options,
    ILogger<ScriptValidationService> logger) : IScriptValidationService
{
    private readonly ScriptExecutionOptions _options = options.Value;
    private readonly ILogger<ScriptValidationService> _logger = logger;

    // Connection error markers — same as ExecutionErrorClassifier
    private static readonly string[] ConnectionMarkers =
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

    private static readonly Regex NumberPattern =
        new(@"\bNumber=(\d+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ── SQL Validation ───────────────────────────────────────────────────────

    public async Task<ScriptValidationResult> ValidateSqlAsync(
        string script, string firstTarget, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(script))
            return ScriptValidationResult.SyntaxError("Script is empty.");

        if (string.IsNullOrWhiteSpace(firstTarget))
            return ScriptValidationResult.SyntaxError("No target provided for SQL validation.");

        try
        {
            var connectionString = BuildSqlConnectionString(firstTarget);
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(ct);

            // Preferred: use sys.dm_exec_describe_first_result_set for parse validation.
            // It compiles the query without executing it and throws on syntax/object errors.
            var validationSql = BuildValidationQuery(script);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = validationSql;
            cmd.CommandType = System.Data.CommandType.Text;
            cmd.CommandTimeout = Math.Min(_options.CommandTimeoutSeconds > 0 ? _options.CommandTimeoutSeconds : 30, 30);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            // If we get here without exception, the script parsed successfully.
            // Drain the result to avoid protocol errors.
            while (await reader.ReadAsync(ct)) { }

            _logger.LogInformation(
                "SQL validation passed on target {Target}.", firstTarget);
            return ScriptValidationResult.Success();
        }
        catch (SqlException ex) when (IsConnectionError(ex))
        {
            _logger.LogWarning(
                "SQL validation connection error on target {Target}: {Message}", firstTarget, ex.Message);
            return ScriptValidationResult.ConnectionError(FormatSqlException(ex));
        }
        catch (SqlException ex)
        {
            var formatted = FormatSqlException(ex);
            var (errorNumber, errorLine) = ExtractErrorDetails(ex);
            _logger.LogInformation(
                "SQL validation failed on target {Target}: {Error}", firstTarget, formatted);
            return ScriptValidationResult.SyntaxError(formatted, errorNumber, errorLine);
        }
        catch (Exception ex) when (IsConnectionException(ex))
        {
            _logger.LogWarning(
                "SQL validation connection error on target {Target}: {Message}", firstTarget, ex.Message);
            return ScriptValidationResult.ConnectionError(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "SQL validation unexpected error on target {Target}: {Message}", firstTarget, ex.Message);
            return ScriptValidationResult.SyntaxError(ex.Message);
        }
    }

    /// <summary>
    /// Builds a validation query that compiles but does not execute the script.
    /// Uses sys.dm_exec_describe_first_result_set which parses and resolves all
    /// object/column references without executing the query.
    /// Falls back to SET FMTONLY ON for scripts that dm_exec_describe can't handle.
    /// </summary>
    private static string BuildValidationQuery(string script)
    {
        // Escape single quotes for embedding in the parameter
        var escaped = script.Replace("'", "''", StringComparison.Ordinal);

        return $"""
            SET NOCOUNT ON;
            BEGIN TRY
                -- dm_exec_describe_first_result_set compiles without executing
                SELECT TOP(0) * FROM sys.dm_exec_describe_first_result_set(
                    N'{escaped}', NULL, 0
                );
            END TRY
            BEGIN CATCH
                -- If dm_exec_describe fails (e.g. for dynamic SQL, IF blocks),
                -- fall back to SET FMTONLY which also compiles without executing.
                BEGIN TRY
                    SET FMTONLY ON;
                    EXEC sp_executesql N'{escaped}';
                    SET FMTONLY OFF;
                END TRY
                BEGIN CATCH
                    SET FMTONLY OFF;
                    -- Re-raise the original error for the caller to parse
                    THROW;
                END CATCH
            END CATCH
            """;
    }

    // ── PowerShell Validation ────────────────────────────────────────────────

    public async Task<ScriptValidationResult> ValidatePowerShellAsync(
        string script, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(script))
            return ScriptValidationResult.SyntaxError("Script is empty.");

        try
        {
            // Validate by parsing locally: [ScriptBlock]::Create($script) | Out-Null
            // This catches all parser errors without executing anything.
            var validationScript = $"[ScriptBlock]::Create(@'\n{script}\n'@) | Out-Null";
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(validationScript));

            var psExe = string.IsNullOrWhiteSpace(_options.PowerShellExecutable)
                ? "powershell"
                : _options.PowerShellExecutable;

            var psi = new ProcessStartInfo
            {
                FileName = psExe,
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync(ct);
            var errorTask = process.StandardError.ReadToEndAsync(ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15)); // Short timeout for parse-only

            await process.WaitForExitAsync(timeoutCts.Token);

            var stdout = await outputTask;
            var stderr = await errorTask;

            // Strip CLIXML noise
            stderr = StripCliXmlNoise(stderr);

            if (process.ExitCode != 0 || !string.IsNullOrWhiteSpace(stderr))
            {
                var errorText = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : stdout.Trim();
                _logger.LogInformation("PowerShell validation failed: {Error}", errorText);
                return ScriptValidationResult.SyntaxError(errorText);
            }

            _logger.LogInformation("PowerShell validation passed.");
            return ScriptValidationResult.Success();
        }
        catch (OperationCanceledException)
        {
            return ScriptValidationResult.SyntaxError("PowerShell validation timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PowerShell validation process error.");
            return ScriptValidationResult.SyntaxError($"Validation process error: {ex.Message}");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string BuildSqlConnectionString(string server)
    {
        var (_, _, connectionTarget) = SqlExecutor.ParseSqlInstanceToken(server);
        var template = _options.SqlConnectionStringTemplate;
        if (!string.IsNullOrWhiteSpace(template))
            return template.Replace("{server}", connectionTarget, StringComparison.OrdinalIgnoreCase);

        return $"Server={connectionTarget};Database=master;Trusted_Connection=True;TrustServerCertificate=True;Encrypt=False;";
    }

    private static bool IsConnectionError(SqlException ex)
    {
        foreach (SqlError error in ex.Errors)
        {
            var msg = error.Message;
            foreach (var marker in ConnectionMarkers)
            {
                if (msg.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    private static bool IsConnectionException(Exception ex)
    {
        var msg = ex.Message;
        foreach (var marker in ConnectionMarkers)
        {
            if (msg.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static (int? Number, int? Line) ExtractErrorDetails(SqlException ex)
    {
        if (ex.Errors.Count == 0)
            return (null, null);

        var first = ex.Errors[0];
        return (first.Number, first.LineNumber > 0 ? first.LineNumber : null);
    }

    private static string FormatSqlException(SqlException ex)
    {
        if (ex.Errors.Count == 0)
            return ex.Message;

        var parts = new List<string>(ex.Errors.Count);
        foreach (SqlError error in ex.Errors)
        {
            parts.Add(
                $"Number={error.Number}; State={error.State}; Line={error.LineNumber}; Message={error.Message}");
        }
        return string.Join(Environment.NewLine, parts);
    }

    private static string StripCliXmlNoise(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return string.Empty;

        var stripped = Regex.Replace(
            stderr,
            @"#<\s*CLIXML\s*\r?\n<Objs[\s\S]*?</Objs>",
            string.Empty,
            RegexOptions.IgnoreCase).Trim();

        return stripped;
    }
}
