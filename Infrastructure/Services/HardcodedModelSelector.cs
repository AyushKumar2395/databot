using Application.Common.Interfaces;
using Application.Common.Models;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

/// <summary>
/// Development selector backed by hardcoded rows that mirror [SQLGig].[DataBOT].[LLMModels].
/// </summary>
public sealed class HardcodedModelSelector(ILogger<HardcodedModelSelector> logger) : IModelSelector
{
    private readonly ILogger<HardcodedModelSelector> _logger = logger;

    private static readonly IReadOnlyList<LlmModelDefinition> Models =
    [
        new()
        {
            ModelId = 1,
            DisplayName = "Flash Lite123",
            ModelKey = "gemini-2.5-flash-lite",
            Provider = "Gemini",
            SortOrder = 10,
            IsDefault = true,
            IsEnabled = true,
            ApiKeyEncrypted = null,
            ApiKeyHint = null,
            CreatedAt = new DateTime(2026, 2, 27, 17, 52, 16, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 3, 2, 5, 58, 0, DateTimeKind.Utc),
            UpdatedBy = null,
            UseForTune = true,
            UseForTemplateFind = true,
            UseForGenerate = true,
            UseForRepair = true,
            UseForExplain = true,
            RetryCount = null
        },
        new()
        {
            ModelId = 2,
            DisplayName = "GPT Mini",
            ModelKey = "gpt-5-mini",
            Provider = "OpenAI",
            SortOrder = 20,
            IsDefault = false,
            IsEnabled = true,
            ApiKeyEncrypted = null,
            ApiKeyHint = null,
            CreatedAt = new DateTime(2026, 2, 27, 17, 52, 16, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 3, 2, 5, 58, 0, DateTimeKind.Utc),
            UpdatedBy = null,
            UseForTune = false,
            UseForTemplateFind = false,
            UseForGenerate = false,
            UseForRepair = false,
            UseForExplain = false,
            RetryCount = null
        },
        new()
        {
            ModelId = 3,
            DisplayName = "Claude Sonnet",
            ModelKey = "Claude Sonnet 4.5",
            Provider = "Claude",
            SortOrder = 30,
            IsDefault = false,
            IsEnabled = true,
            ApiKeyEncrypted = null,
            ApiKeyHint = null,
            CreatedAt = new DateTime(2026, 3, 2, 3, 28, 52, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 3, 2, 5, 58, 0, DateTimeKind.Utc),
            UpdatedBy = null,
            UseForTune = false,
            UseForTemplateFind = false,
            UseForGenerate = false,
            UseForRepair = false,
            UseForExplain = false,
            RetryCount = null
        }
    ];

    public LlmModelDefinition SelectTuneModel() => SelectByFlag(m => m.UseForTune, "UseForTune");

    public LlmModelDefinition SelectTemplateFindModel() => SelectByFlag(m => m.UseForTemplateFind, "UseForTemplateFind");

    public LlmModelDefinition SelectGenerateModel() => SelectByFlag(m => m.UseForGenerate, "UseForGenerate");

    private LlmModelDefinition SelectByFlag(Func<LlmModelDefinition, bool> flagPredicate, string stage)
    {
        var enabled = Models.Where(m => m.IsEnabled).OrderBy(m => m.SortOrder).ToList();
        var selected = enabled.FirstOrDefault(flagPredicate) ?? enabled.FirstOrDefault(m => m.IsDefault);

        if (selected is null)
            throw new InvalidOperationException("No enabled/default LLM model was configured.");

        _logger.LogInformation(
            "Model selected for {Stage}: {DisplayName} ({Provider}:{ModelKey})",
            stage,
            selected.DisplayName,
            selected.Provider,
            selected.ModelKey);

        // TODO(DB integration): replace hardcoded list with query from [SQLGig].[DataBOT].[LLMModels]
        // using either EF Core DbContext or Dapper repository.
        // TODO(DB integration): select first enabled by flag
        // (UseForTune/UseForTemplateFind/UseForGenerate), fallback to IsDefault.
        // TODO(Retry policy): read RetryCount and apply retries at call sites.
        return selected;
    }
}
