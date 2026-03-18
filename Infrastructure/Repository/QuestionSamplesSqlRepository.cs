using Application.Common.Interfaces;
using Application.Common.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Repository;

/// <summary>
/// Loads sample questions from [SQLGig].[DataBOT].[QuestionSamples].
/// </summary>
public sealed class QuestionSamplesSqlRepository(
    IConfiguration configuration,
    ILogger<QuestionSamplesSqlRepository> logger) : IQuestionSamplesRepository
{
    private readonly ILogger<QuestionSamplesSqlRepository> _logger = logger;
    private readonly string _connectionString = configuration.GetConnectionString("SqlGig") ?? string.Empty;

    public async Task<List<QuestionSampleRow>> GetByEnvironmentAsync(
        string environment,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
            throw new InvalidOperationException("DB_CONFIG_MISSING: ConnectionStrings:SqlGig is not configured.");

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                [SampleId] AS [Id],
                [Environment],
                [GroupKey],
                [GroupTitle],
                [GroupOrder],
                [QuestionText],
                [QuestionOrder],
                [Tags]
            FROM [SQLGig].[DataBOT].[QuestionSamples]
            WHERE [IsActive] = 1
              AND [Environment] = @Environment
            ORDER BY [GroupOrder], [QuestionOrder], [QuestionText];
            """;
        command.Parameters.AddWithValue("@Environment", environment);

        var rows = new List<QuestionSampleRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new QuestionSampleRow
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                Environment = reader["Environment"]?.ToString() ?? environment,
                GroupKey = reader["GroupKey"]?.ToString() ?? string.Empty,
                GroupTitle = reader["GroupTitle"]?.ToString() ?? string.Empty,
                GroupOrder = reader["GroupOrder"] is DBNull ? 0 : Convert.ToInt32(reader["GroupOrder"]),
                QuestionText = reader["QuestionText"]?.ToString() ?? string.Empty,
                QuestionOrder = reader["QuestionOrder"] is DBNull ? 0 : Convert.ToInt32(reader["QuestionOrder"]),
                Tags = reader["Tags"] as string
            });
        }

        _logger.LogInformation(
            "Loaded {Count} QuestionSamples rows for environment {Environment}.",
            rows.Count,
            environment);

        return rows;
    }

    public async Task<QuestionSampleRow?> GetByIdAsync(
        int sampleId,
        string environment,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
            throw new InvalidOperationException("DB_CONFIG_MISSING: ConnectionStrings:SqlGig is not configured.");

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                [SampleId] AS [Id],
                [Environment],
                [GroupKey],
                [GroupTitle],
                [GroupOrder],
                [QuestionText],
                [QuestionOrder],
                [Tags],
                [Script]
            FROM [SQLGig].[DataBOT].[QuestionSamples]
            WHERE [SampleId] = @SampleId
              AND [Environment] = @Environment
              AND [IsActive] = 1;
            """;
        command.Parameters.AddWithValue("@SampleId", sampleId);
        command.Parameters.AddWithValue("@Environment", environment);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var row = new QuestionSampleRow
        {
            Id = reader.GetInt32(reader.GetOrdinal("Id")),
            Environment = reader["Environment"]?.ToString() ?? environment,
            GroupKey = reader["GroupKey"]?.ToString() ?? string.Empty,
            GroupTitle = reader["GroupTitle"]?.ToString() ?? string.Empty,
            GroupOrder = reader["GroupOrder"] is DBNull ? 0 : Convert.ToInt32(reader["GroupOrder"]),
            QuestionText = reader["QuestionText"]?.ToString() ?? string.Empty,
            QuestionOrder = reader["QuestionOrder"] is DBNull ? 0 : Convert.ToInt32(reader["QuestionOrder"]),
            Tags = reader["Tags"] as string,
            Script = reader["Script"] as string
        };

        _logger.LogInformation(
            "Loaded QuestionSample SampleId={SampleId} for environment {Environment}.",
            sampleId, environment);

        return row;
    }

    public async Task<QuestionSampleRow?> GetByGroupKeyAsync(
        string groupKey,
        string environment,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
            throw new InvalidOperationException("DB_CONFIG_MISSING: ConnectionStrings:SqlGig is not configured.");

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP 1
                [SampleId] AS [Id],
                [Environment],
                [GroupKey],
                [GroupTitle],
                [GroupOrder],
                [QuestionText],
                [QuestionOrder],
                [Tags],
                [Script]
            FROM [SQLGig].[DataBOT].[QuestionSamples]
            WHERE [GroupKey] = @GroupKey
              AND [Environment] = @Environment
              AND [IsActive] = 1
            ORDER BY [QuestionOrder];
            """;
        command.Parameters.AddWithValue("@GroupKey", groupKey);
        command.Parameters.AddWithValue("@Environment", environment);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var row = new QuestionSampleRow
        {
            Id = reader.GetInt32(reader.GetOrdinal("Id")),
            Environment = reader["Environment"]?.ToString() ?? environment,
            GroupKey = reader["GroupKey"]?.ToString() ?? string.Empty,
            GroupTitle = reader["GroupTitle"]?.ToString() ?? string.Empty,
            GroupOrder = reader["GroupOrder"] is DBNull ? 0 : Convert.ToInt32(reader["GroupOrder"]),
            QuestionText = reader["QuestionText"]?.ToString() ?? string.Empty,
            QuestionOrder = reader["QuestionOrder"] is DBNull ? 0 : Convert.ToInt32(reader["QuestionOrder"]),
            Tags = reader["Tags"] as string,
            Script = reader["Script"] as string
        };

        _logger.LogInformation(
            "Loaded QuestionSample GroupKey={GroupKey} for environment {Environment}, SampleId={SampleId}.",
            groupKey, environment, row.Id);

        return row;
    }
}
