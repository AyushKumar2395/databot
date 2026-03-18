using System.Diagnostics;
using System.Text.RegularExpressions;
using Application.Common.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

internal sealed class SqlExecutor(
    IOptions<ScriptExecutionOptions> options,
    ILogger<SqlExecutor> logger) : ISqlExecutor
{
    private static readonly HashSet<int> RetryableSqlErrors =
    [
        102, // Incorrect syntax near
        105, // Unclosed quotation
        156, // Incorrect syntax near keyword
        195, // not a recognized built-in function
        207, // Invalid column name
        208, // Invalid object name
        213, // Column name/number mismatch
        2812, // Could not find stored procedure
        4104, // Multi-part identifier could not be bound
        4121 // Cannot find either column
    ];

    private readonly ScriptExecutionOptions _options = options.Value;
    private readonly ILogger<SqlExecutor> _logger = logger;

    public async Task<ExecutorRunResult> ExecuteAsync(string server, string script, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var (_, _, connectionTarget) = ParseSqlInstanceToken(server);
        // Use the full connection target as display name (e.g., "CTS03\CTSGlobal" or "CTS02,1432\ADMIN")
        // so execution result items show the instance name, not just the hostname.
        var displayServer = connectionTarget;
        try
        {
            var rows = new List<Dictionary<string, object?>>();
            var connectionString = BuildSqlConnectionString(server);

            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            foreach (var batch in SplitBatches(script))
            {
                if (string.IsNullOrWhiteSpace(batch))
                    continue;

                await using var command = connection.CreateCommand();
                command.CommandText = batch;
                command.CommandType = System.Data.CommandType.Text;
                command.CommandTimeout = _options.CommandTimeoutSeconds <= 0 ? 60 : _options.CommandTimeoutSeconds;

                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                do
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                        for (var i = 0; i < reader.FieldCount; i++)
                        {
                            var name = reader.GetName(i);
                            row[name] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                        }

                        rows.Add(row);
                    }
                } while (await reader.NextResultAsync(cancellationToken));
            }

            InjectServerIfMissing(rows, displayServer);
            return new ExecutorRunResult
            {
                Server = displayServer,
                Success = true,
                IsRetryableCompileError = false,
                Error = null,
                DurationMs = stopwatch.ElapsedMilliseconds,
                Rows = rows
            };
        }
        catch (SqlException ex) when (ex.Message.Contains("Operation cancelled", StringComparison.OrdinalIgnoreCase)
                                   || ex.Message.Contains("operation was canceled", StringComparison.OrdinalIgnoreCase))
        {
            // Timeout/cancellation — log cleanly without stack trace
            _logger.LogDebug("SQL query timed out on {Server} after {Ms}ms.", displayServer, stopwatch.ElapsedMilliseconds);
            return new ExecutorRunResult
            {
                Server = displayServer,
                Success = false,
                IsRetryableCompileError = false,
                Error = $"Query timed out after {stopwatch.ElapsedMilliseconds / 1000}s on {displayServer}.",
                DurationMs = stopwatch.ElapsedMilliseconds,
                Rows = []
            };
        }
        catch (SqlException ex)
        {
            var error = FormatSqlException(ex);
            _logger.LogWarning("SQL execution failed on {Server}: {Error}", displayServer, error);
            return new ExecutorRunResult
            {
                Server = displayServer,
                Success = false,
                IsRetryableCompileError = IsRetryableCompileError(ex),
                Error = error,
                DurationMs = stopwatch.ElapsedMilliseconds,
                Rows = []
            };
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("SQL query cancelled on {Server} after {Ms}ms.", displayServer, stopwatch.ElapsedMilliseconds);
            return new ExecutorRunResult
            {
                Server = displayServer,
                Success = false,
                IsRetryableCompileError = false,
                Error = $"Query cancelled after {stopwatch.ElapsedMilliseconds / 1000}s on {displayServer}.",
                DurationMs = stopwatch.ElapsedMilliseconds,
                Rows = []
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning("SQL execution failed on {Server}: {Error}", displayServer, ex.Message);
            return new ExecutorRunResult
            {
                Server = displayServer,
                Success = false,
                IsRetryableCompileError = false,
                Error = ex.Message,
                DurationMs = stopwatch.ElapsedMilliseconds,
                Rows = []
            };
        }
    }

    /// <summary>
    /// Parses a selectedServers token into its SQL connection components.
    /// "CTS03#Admin"  → server="CTS03", instance="Admin", connectionTarget="CTS03\Admin"
    /// "CTS03"        → server="CTS03", instance=null,    connectionTarget="CTS03"
    /// </summary>
    internal static (string Server, string? Instance, string ConnectionTarget) ParseSqlInstanceToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return (string.Empty, null, string.Empty);

        var hashIndex = token.IndexOf('#');
        if (hashIndex < 0)
            return (token, null, token);

        var server = token[..hashIndex];
        var instance = token[(hashIndex + 1)..];

        // MSSQLSERVER is the default instance — connect with just the server name,
        // not "Server\MSSQLSERVER" which would fail.
        if (instance.Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase))
            return (server, null, server);

        return (server, instance, $@"{server}\{instance}");
    }

    private string BuildSqlConnectionString(string server)
    {
        var (_, _, connectionTarget) = ParseSqlInstanceToken(server);
        var template = _options.SqlConnectionStringTemplate;
        if (!string.IsNullOrWhiteSpace(template))
            return template.Replace("{server}", connectionTarget, StringComparison.OrdinalIgnoreCase);

        return $"Server={connectionTarget};Database=master;Trusted_Connection=True;TrustServerCertificate=True;Encrypt=False;";
    }

    private static IReadOnlyList<string> SplitBatches(string script)
    {
        if (string.IsNullOrWhiteSpace(script))
            return [string.Empty];

        var parts = Regex.Split(
            script,
            @"^\s*GO\s*;?\s*$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);

        return parts.Length == 0 ? [script] : parts;
    }

    private static void InjectServerIfMissing(List<Dictionary<string, object?>> rows, string server)
    {
        if (rows.Count == 0)
            return;

        foreach (var row in rows)
        {
            if (!row.Keys.Any(k => string.Equals(k, "ServerName", StringComparison.OrdinalIgnoreCase)))
            {
                var existingServer = row.FirstOrDefault(kv => string.Equals(kv.Key, "Server", StringComparison.OrdinalIgnoreCase));
                row["ServerName"] = existingServer.Value ?? server;
            }
        }
    }

    private static bool IsRetryableCompileError(SqlException ex)
    {
        if (ex.Errors.Count == 0)
            return false;

        foreach (SqlError error in ex.Errors)
        {
            if (RetryableSqlErrors.Contains(error.Number))
                return true;

            if (ContainsRetryableSqlMessage(error.Message))
                return true;
        }

        return false;
    }

    private static bool ContainsRetryableSqlMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        return message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Incorrect syntax", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Could not find", StringComparison.OrdinalIgnoreCase)
               || message.Contains("not a recognized built-in function", StringComparison.OrdinalIgnoreCase);
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
}

