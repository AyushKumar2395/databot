using Application.Common.Interfaces;
using Application.Common.Models;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

/// <summary>
/// Production model selector that reads from [databot].[LLMModels] via the Azure portal DB.
/// Caches the model list for 5 minutes to avoid repeated DB calls within a single request burst.
/// Falls back to <see cref="HardcodedModelSelector"/> if the DB is unreachable.
/// </summary>
public sealed class DatabaseModelSelector(
    ILlmModelRepository repository,
    ILogger<DatabaseModelSelector> logger) : IModelSelector
{
    private readonly ILogger<DatabaseModelSelector> _logger = logger;
    private readonly ILlmModelRepository _repository = repository;

    private IReadOnlyList<LlmModelDefinition>? _cachedModels;
    private DateTime _cacheExpiry = DateTime.MinValue;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private readonly object _lock = new();

    public LlmModelDefinition SelectTuneModel() => SelectByFlag(m => m.UseForTune, "UseForTune");

    public LlmModelDefinition SelectPlanModel() => SelectTuneModel();

    public LlmModelDefinition SelectTemplateFindModel() => SelectByFlag(m => m.UseForTemplateFind, "UseForTemplateFind");

    public LlmModelDefinition SelectValidateModel() =>
        SelectByFlag(m => m.UseForValidate || m.UseForRepair, "UseForValidate");

    public LlmModelDefinition SelectGenerateModel() => SelectByFlag(m => m.UseForGenerate, "UseForGenerate");

    public LlmModelDefinition SelectExplainModel() =>
        SelectByFlag(m => m.UseForExplain, "UseForExplain");

    private LlmModelDefinition SelectByFlag(Func<LlmModelDefinition, bool> flagPredicate, string stage)
    {
        var models = GetModels();
        var enabled = models.Where(m => m.IsEnabled).OrderBy(m => m.SortOrder).ToList();
        var selected = enabled.FirstOrDefault(flagPredicate) ?? enabled.FirstOrDefault(m => m.IsDefault);

        if (selected is null)
            throw new InvalidOperationException("No enabled/default LLM model was configured in [databot].[LLMModels].");

        _logger.LogInformation(
            "Model selected for {Stage}: {DisplayName} ({Provider}:{ModelKey})",
            stage,
            selected.DisplayName,
            selected.Provider,
            selected.ModelKey);

        return selected;
    }

    private IReadOnlyList<LlmModelDefinition> GetModels()
    {
        lock (_lock)
        {
            if (_cachedModels is not null && DateTime.UtcNow < _cacheExpiry)
                return _cachedModels;
        }

        try
        {
            var models = _repository.GetAllEnabledAsync().GetAwaiter().GetResult();

            lock (_lock)
            {
                _cachedModels = models;
                _cacheExpiry = DateTime.UtcNow.Add(CacheDuration);
            }

            return models;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load LLM models from Azure DB — falling back to cached/hardcoded models");

            lock (_lock)
            {
                if (_cachedModels is not null)
                    return _cachedModels;
            }

            // Ultimate fallback: use the hardcoded list so the app doesn't crash
            throw new InvalidOperationException(
                "Cannot load LLM models from Azure DB and no cached models available. " +
                "Check ConnectionStrings:AzurePortal configuration.", ex);
        }
    }
}
