using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Application.Common.Models;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

internal sealed class PowerShellExecutor(
    IOptions<ScriptExecutionOptions> options,
    ILogger<PowerShellExecutor> logger) : IPowerShellExecutor
{
    private readonly ScriptExecutionOptions _options = options.Value;
    private readonly ILogger<PowerShellExecutor> _logger = logger;

    public async Task<ExecutorRunResult> ExecuteAsync(string server, string script, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var wrapper = BuildRemoteExecutionScript(server, script);
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapper));

            var psi = new ProcessStartInfo
            {
                FileName = string.IsNullOrWhiteSpace(_options.PowerShellExecutable)
                    ? "powershell"
                    : _options.PowerShellExecutable,
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.CommandTimeoutSeconds <= 0 ? 60 : _options.CommandTimeoutSeconds));

            await process.WaitForExitAsync(timeoutCts.Token);

            var stdout = await outputTask;
            var stderr = await errorTask;
            var exitCode = process.ExitCode;

            if (exitCode != 0 || !string.IsNullOrWhiteSpace(stderr))
            {
                var error = BuildPowerShellError(exitCode, stderr, stdout);
                return new ExecutorRunResult
                {
                    Server = server,
                    Success = false,
                    IsRetryableCompileError = IsRetryableCompileError(error),
                    Error = error,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    Rows = [],
                    RawOutput = stdout
                };
            }

            var rows = ParsePowerShellRows(stdout, server);
            return new ExecutorRunResult
            {
                Server = server,
                Success = true,
                IsRetryableCompileError = false,
                Error = null,
                DurationMs = stopwatch.ElapsedMilliseconds,
                Rows = rows,
                RawOutput = stdout
            };
        }
        catch (OperationCanceledException ex)
        {
            return new ExecutorRunResult
            {
                Server = server,
                Success = false,
                IsRetryableCompileError = false,
                Error = $"PowerShell execution timed out: {ex.Message}",
                DurationMs = stopwatch.ElapsedMilliseconds,
                Rows = []
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PowerShell execution failed on server {Server}.", server);
            return new ExecutorRunResult
            {
                Server = server,
                Success = false,
                IsRetryableCompileError = IsRetryableCompileError(ex.Message),
                Error = ex.Message,
                DurationMs = stopwatch.ElapsedMilliseconds,
                Rows = []
            };
        }
    }

    private static string BuildRemoteExecutionScript(string server, string script)
    {
        var safeServer = server.Replace("'", "''", StringComparison.Ordinal);
        return
            "$ErrorActionPreference = 'Stop'" + Environment.NewLine +
            "$scriptBlock = [ScriptBlock]::Create(@'" + Environment.NewLine +
            (script ?? string.Empty) + Environment.NewLine +
            "'@)" + Environment.NewLine +
            $"$result = Invoke-Command -ComputerName '{safeServer}' -ScriptBlock $scriptBlock -ErrorAction Stop" + Environment.NewLine +
            "if ($null -eq $result) {" + Environment.NewLine +
            "  '[]'" + Environment.NewLine +
            "} else {" + Environment.NewLine +
            "  $result | ConvertTo-Json -Depth 6 -Compress" + Environment.NewLine +
            "}";
    }

    private static List<Dictionary<string, object?>> ParsePowerShellRows(string stdout, string server)
    {
        var text = (stdout ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(text);
            var rows = new List<Dictionary<string, object?>>();
            switch (doc.RootElement.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (var item in doc.RootElement.EnumerateArray())
                        rows.Add(ToRow(item));
                    break;
                case JsonValueKind.Object:
                    rows.Add(ToRow(doc.RootElement));
                    break;
                default:
                    rows.Add(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Value"] = ToObject(doc.RootElement)
                    });
                    break;
            }

            InjectServerIfMissing(rows, server);
            return rows;
        }
        catch
        {
            return
            [
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Server"] = server,
                    ["RawOutput"] = text
                }
            ];
        }
    }

    private static Dictionary<string, object?> ToRow(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Value"] = ToObject(element)
            };
        }

        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
            row[property.Name] = ToObject(property.Value);
        return row;
    }

    private static object? ToObject(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var l) => l,
            JsonValueKind.Number when element.TryGetDecimal(out var d) => d,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => element.EnumerateArray().Select(ToObject).ToList(),
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(x => x.Name, x => ToObject(x.Value), StringComparer.OrdinalIgnoreCase),
            _ => element.ToString()
        };
    }

    private static void InjectServerIfMissing(List<Dictionary<string, object?>> rows, string server)
    {
        if (rows.Count == 0)
            return;

        var hasServer = rows.Any(r => r.Keys.Any(k => string.Equals(k, "Server", StringComparison.OrdinalIgnoreCase)));
        if (hasServer)
            return;

        foreach (var row in rows)
            row["Server"] = server;
    }

    private static string BuildPowerShellError(int exitCode, string stderr, string stdout)
    {
        var details = new List<string> { $"ExitCode={exitCode}" };
        if (!string.IsNullOrWhiteSpace(stderr))
            details.Add(stderr.Trim());
        if (!string.IsNullOrWhiteSpace(stdout))
            details.Add(stdout.Trim());
        return string.Join(Environment.NewLine, details);
    }

    private static bool IsRetryableCompileError(string errorText)
    {
        if (string.IsNullOrWhiteSpace(errorText))
            return false;

        return errorText.Contains("ParserError", StringComparison.OrdinalIgnoreCase)
               || errorText.Contains("ParseException", StringComparison.OrdinalIgnoreCase)
               || errorText.Contains("CommandNotFoundException", StringComparison.OrdinalIgnoreCase)
               || errorText.Contains("is not recognized as", StringComparison.OrdinalIgnoreCase)
               || errorText.Contains("Unexpected token", StringComparison.OrdinalIgnoreCase)
               || errorText.Contains("PropertyNotFoundException", StringComparison.OrdinalIgnoreCase)
               || errorText.Contains("MethodNotFound", StringComparison.OrdinalIgnoreCase);
    }
}

