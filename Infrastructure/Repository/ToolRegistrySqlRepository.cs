using Application.Common.Interfaces;
using Application.Common.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Repository;

/// <summary>
/// SQL-backed ToolRegistry candidate loader.
/// </summary>
public sealed class ToolRegistrySqlRepository(
    IConfiguration configuration,
    ILogger<ToolRegistrySqlRepository> logger) : IToolRegistrySqlRepository
{
    private readonly ILogger<ToolRegistrySqlRepository> _logger = logger;
    private readonly string _connectionString = configuration.GetConnectionString("SqlGig") ?? string.Empty;

    public async Task<List<QueryCodeCandidate>> GetActiveToolsByEnvironmentAsync(
        string environment,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
            throw new InvalidOperationException("DB_CONFIG_MISSING");

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT
    [ToolId],
    [QueryCode],
    [ToolName],
    [Environment],
    [Description],
    [Keywords],
    [ToolTags],
    [Priority],
    [IsReadOnly],
    [ScriptLanguage],
    [ScriptTemplate],
    [ParameterSchema],
    [OutputSchema]
FROM [SQLGig].[DataBOT].[ToolRegistry]
WHERE [IsActive] = 1
  AND [Environment] = @Environment;
""";
        command.Parameters.AddWithValue("@Environment", environment);

        var rows = new List<QueryCodeCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new QueryCodeCandidate
            {
                ToolId = reader.GetInt32(reader.GetOrdinal("ToolId")),
                QueryCode = reader["QueryCode"]?.ToString() ?? string.Empty,
                ToolName = reader["ToolName"]?.ToString() ?? string.Empty,
                Environment = reader["Environment"]?.ToString() ?? environment,
                Description = reader["Description"]?.ToString() ?? string.Empty,
                Keywords = reader["Keywords"]?.ToString() ?? string.Empty,
                ToolTags = reader["ToolTags"]?.ToString() ?? string.Empty,
                Priority = reader["Priority"] is DBNull ? 0 : Convert.ToInt32(reader["Priority"]),
                IsReadOnly = reader["IsReadOnly"] is DBNull || Convert.ToBoolean(reader["IsReadOnly"]),
                ScriptLanguage = reader["ScriptLanguage"]?.ToString() ?? string.Empty,
                ScriptTemplate = reader["ScriptTemplate"]?.ToString() ?? string.Empty,
                ParameterSchema = reader["ParameterSchema"] as string,
                OutputSchema = reader["OutputSchema"] as string
            });
        }

        _logger.LogInformation(
            "Loaded {Count} ToolRegistry rows for environment {Environment}.",
            rows.Count,
            environment);

        return rows;
    }
}
