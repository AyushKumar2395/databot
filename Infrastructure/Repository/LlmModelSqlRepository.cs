using Application.Common.Interfaces;
using Application.Common.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Repository;

/// <summary>
/// Loads LLM model definitions from [databot].[LLMModels] on the Azure portal database.
/// </summary>
public sealed class LlmModelSqlRepository(
    IConfiguration configuration,
    ILogger<LlmModelSqlRepository> logger) : ILlmModelRepository
{
    private readonly ILogger<LlmModelSqlRepository> _logger = logger;
    private readonly string _connectionString = configuration.GetConnectionString("AzurePortal") ?? string.Empty;

    public async Task<IReadOnlyList<LlmModelDefinition>> GetAllEnabledAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
            throw new InvalidOperationException("DB_CONFIG_MISSING: ConnectionStrings:AzurePortal is not configured.");

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                [ModelId],
                [DisplayName],
                [ModelKey],
                [Provider],
                [SortOrder],
                [IsDefault],
                [IsEnabled],
                [ApiKeyEncrypted],
                [ApiKeyHint],
                [CreatedAt],
                [UpdatedAt],
                [UpdatedBy],
                [UseForTune],
                [UseForTemplateFind],
                [UseForGenerate],
                [UseForRepair],
                [UseForExplain],
                [RetryCount],
                [Genarator] AS [Generator]
            FROM [databot].[LLMModels]
            WHERE [IsEnabled] = 1
            ORDER BY [SortOrder];
            """;
        command.CommandTimeout = 15;

        var models = new List<LlmModelDefinition>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            models.Add(new LlmModelDefinition
            {
                ModelId = reader.GetInt32(reader.GetOrdinal("ModelId")),
                DisplayName = reader.GetString(reader.GetOrdinal("DisplayName")),
                ModelKey = reader.GetString(reader.GetOrdinal("ModelKey")),
                Provider = reader.GetString(reader.GetOrdinal("Provider")),
                SortOrder = reader.GetInt32(reader.GetOrdinal("SortOrder")),
                IsDefault = reader.GetBoolean(reader.GetOrdinal("IsDefault")),
                IsEnabled = reader.GetBoolean(reader.GetOrdinal("IsEnabled")),
                ApiKeyEncrypted = reader.IsDBNull(reader.GetOrdinal("ApiKeyEncrypted"))
                    ? null : reader.GetString(reader.GetOrdinal("ApiKeyEncrypted")),
                ApiKeyHint = reader.IsDBNull(reader.GetOrdinal("ApiKeyHint"))
                    ? null : reader.GetString(reader.GetOrdinal("ApiKeyHint")),
                CreatedAt = reader.GetDateTime(reader.GetOrdinal("CreatedAt")),
                UpdatedAt = reader.GetDateTime(reader.GetOrdinal("UpdatedAt")),
                UpdatedBy = reader.IsDBNull(reader.GetOrdinal("UpdatedBy"))
                    ? null : reader.GetString(reader.GetOrdinal("UpdatedBy")),
                UseForTune = reader.GetBoolean(reader.GetOrdinal("UseForTune")),
                UseForTemplateFind = reader.GetBoolean(reader.GetOrdinal("UseForTemplateFind")),
                UseForValidate = false, // Column does not exist in DB — default to false
                UseForGenerate = reader.GetBoolean(reader.GetOrdinal("UseForGenerate")),
                UseForRepair = reader.GetBoolean(reader.GetOrdinal("UseForRepair")),
                UseForExplain = reader.GetBoolean(reader.GetOrdinal("UseForExplain")),
                RetryCount = reader.IsDBNull(reader.GetOrdinal("RetryCount"))
                    ? null : reader.GetInt32(reader.GetOrdinal("RetryCount")),
                Generator = reader.IsDBNull(reader.GetOrdinal("Generator"))
                    ? null : reader.GetInt32(reader.GetOrdinal("Generator"))
            });
        }

        _logger.LogInformation("Loaded {Count} enabled LLM models from Azure portal DB", models.Count);
        return models;
    }
}
