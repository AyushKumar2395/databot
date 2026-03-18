using Application.Common.Interfaces;
using Application.Common.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Data;

namespace Infrastructure.Repository;

/// <summary>
/// Calls Get_UserSQLServer / Get_UserWINServer stored procedures on SQLGig
/// to retrieve server lists per user.
/// </summary>
public sealed class UserServerSqlRepository(
    IConfiguration configuration,
    ILogger<UserServerSqlRepository> logger) : IUserServerRepository
{
    private readonly string _connectionString = configuration.GetConnectionString("SqlGig") ?? string.Empty;

    public async Task<List<UserServerEntry>> GetSqlServersAsync(string userId, CancellationToken cancellationToken)
    {
        return await ExecuteServerSpAsync("Get_UserSQLServer", userId, isSql: true, cancellationToken);
    }

    public async Task<List<UserServerEntry>> GetWinServersAsync(string userId, CancellationToken cancellationToken)
    {
        return await ExecuteServerSpAsync("Get_UserWINServer", userId, isSql: false, cancellationToken);
    }

    private async Task<List<UserServerEntry>> ExecuteServerSpAsync(
        string spName, string userId, bool isSql, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
            throw new InvalidOperationException("DB_CONFIG_MISSING: ConnectionStrings:SqlGig is not configured.");

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandType = CommandType.StoredProcedure;
        command.CommandText = spName;
        command.Parameters.AddWithValue("@UserId", userId);
        command.CommandTimeout = 15;

        var servers = new List<UserServerEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            string serverName;
            string? instanceName = null;
            int port = 0;

            if (isSql)
            {
                // For SQL queries, read SQLServer column directly (e.g., "CTS02\ADMIN").
                // Parse hostname and instance from the backslash-delimited value.
                var sqlServerValue = ReadColumnValue(reader, "SQLServer");
                if (string.IsNullOrWhiteSpace(sqlServerValue))
                    continue;

                var backslash = sqlServerValue.IndexOf('\\');
                if (backslash >= 0)
                {
                    serverName = sqlServerValue[..backslash].Trim();
                    instanceName = sqlServerValue[(backslash + 1)..].Trim();
                }
                else
                {
                    // Default instance — use the standard SQL Server default instance name
                    // so the token becomes "CTS02#MSSQLSERVER" instead of bare "CTS02"
                    // (avoids ambiguity with Windows server names).
                    serverName = sqlServerValue.Trim();
                    instanceName = "MSSQLSERVER";
                }

                port = ReadIntColumn(reader, "Port");
            }
            else
            {
                // For Windows queries, read WINServer column explicitly to avoid
                // picking up the SQLServer column (both contain "Server").
                serverName = ReadWinServerName(reader) ?? ReadServerName(reader);
                if (string.IsNullOrWhiteSpace(serverName))
                    continue;
            }

            // Build the token: "Server#Instance" for SQL, "Server" for Windows
            var token = isSql && !string.IsNullOrWhiteSpace(instanceName)
                ? $"{serverName}#{instanceName}"
                : serverName;

            // Deduplicate: the SP may return one row per SQL instance even for
            // Windows queries (Get_UserWINServer joins UserSQLHierarchyView).
            if (!seen.Add(token))
                continue;

            // Display name: "CTS02,1433" for default, "CTS02\ADMIN,1432" for named instances.
            // MSSQLSERVER is the internal default instance name — don't show it to users.
            var isDefault = string.Equals(instanceName, "MSSQLSERVER", StringComparison.OrdinalIgnoreCase);
            var baseName = isSql && !string.IsNullOrWhiteSpace(instanceName) && !isDefault
                ? $@"{serverName}\{instanceName}"
                : serverName;
            var displayName = isSql && port > 0
                ? $"{baseName},{port}"
                : baseName;

            // Read additional columns available from the SP
            var environment = ReadColumnValue(reader, "Environment");
            var domain = ReadColumnValue(reader, "Domain");
            var winServer = ReadColumnValue(reader, "WINServer");
            var sqlService = ReadIntColumn(reader, "SQLService");
            var sqlAgentService = ReadIntColumn(reader, "SQLAgentService");

            servers.Add(new UserServerEntry
            {
                ServerName = serverName,
                InstanceName = !string.IsNullOrWhiteSpace(instanceName) ? instanceName : null,
                Token = token,
                DisplayName = displayName,
                Port = port,
                WinServer = !string.IsNullOrWhiteSpace(winServer) ? winServer : null,
                MonitoringEnvironment = !string.IsNullOrWhiteSpace(environment) ? environment : null,
                Domain = !string.IsNullOrWhiteSpace(domain) ? domain : null,
                SqlServiceOnline = isSql ? sqlService == 1 : null,
                SqlAgentOnline = isSql ? sqlAgentService == 1 : null
            });
        }

        logger.LogInformation(
            "SP {SpName} returned {Count} servers for UserId={UserId}.",
            spName, servers.Count, userId);

        return servers;
    }

    /// <summary>
    /// Reads the WINServer column explicitly — avoids ambiguity with SQLServer column.
    /// </summary>
    private static string? ReadWinServerName(SqlDataReader reader)
    {
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var colName = reader.GetName(i);
            if (colName.Equals("WINServer", StringComparison.OrdinalIgnoreCase)
                || colName.Equals("WinServer", StringComparison.OrdinalIgnoreCase))
            {
                var val = reader[i]?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(val))
                    return val;
            }
        }
        return null;
    }

    /// <summary>
    /// Tries common column names for the server name.
    /// </summary>
    private static string ReadServerName(SqlDataReader reader)
    {
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var colName = reader.GetName(i);
            if (colName.Contains("Server", StringComparison.OrdinalIgnoreCase)
                && !colName.Contains("Instance", StringComparison.OrdinalIgnoreCase))
            {
                var val = reader[i]?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(val))
                    return val;
            }
        }
        // Fallback: first string column
        if (reader.FieldCount > 0)
            return reader[0]?.ToString()?.Trim() ?? string.Empty;
        return string.Empty;
    }

    /// <summary>
    /// Reads a specific column by exact name.
    /// </summary>
    private static string? ReadColumnValue(SqlDataReader reader, string columnName)
    {
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (reader.GetName(i).Equals(columnName, StringComparison.OrdinalIgnoreCase))
                return reader[i]?.ToString()?.Trim();
        }
        return null;
    }

    /// <summary>
    /// Reads an integer column by exact name. Returns 0 if not found or not numeric.
    /// </summary>
    private static int ReadIntColumn(SqlDataReader reader, string columnName)
    {
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (reader.GetName(i).Equals(columnName, StringComparison.OrdinalIgnoreCase))
            {
                if (reader.IsDBNull(i)) return 0;
                return reader.GetValue(i) is int intVal ? intVal : int.TryParse(reader[i]?.ToString(), out var parsed) ? parsed : 0;
            }
        }
        return 0;
    }
}
