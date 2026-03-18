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
        UseForValidate = true,
        UseForGenerate = false,
        UseForRepair = true,
            UseForExplain = true,
            RetryCount = null,
            Generator = 1
        },
        new()
        {
            ModelId = 2,
            DisplayName = "GPT Mini",
            ModelKey = "gpt-4o-mini",
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
            UseForValidate = false,
            UseForGenerate = false,
            UseForRepair = false,
            UseForExplain = false,
            RetryCount = null,
            Generator = 1
        },
        new()
        {
            ModelId = 3,
            DisplayName = "Claude Sonnet",
            ModelKey = "claude-sonnet-4-5",
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
            UseForValidate = false,
            UseForGenerate = true,
            UseForRepair = false,
            UseForExplain = false,
            RetryCount = null,
            Generator = 2
        }
    ];

    public LlmModelDefinition SelectTuneModel() => SelectByFlag(m => m.UseForTune, "UseForTune");

    public LlmModelDefinition SelectPlanModel() => SelectTuneModel();

    public LlmModelDefinition SelectTemplateFindModel() => SelectByFlag(m => m.UseForTemplateFind, "UseForTemplateFind");

    public LlmModelDefinition SelectValidateModel() =>
        SelectByFlag(m => m.UseForValidate || m.UseForRepair, "UseForValidate");

    public LlmModelDefinition SelectGenerateModel() => SelectByFlag(m => m.Generator > 0, "Generator");

    public LlmModelDefinition SelectExplainModel() =>
        SelectByFlag(m => m.UseForExplain, "UseForExplain");

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
        // (UseForTune/UseForTemplateFind/Generator>0), fallback to IsDefault.
        // TODO(Retry policy): read RetryCount and apply retries at call sites.
        return selected;
    }
}
