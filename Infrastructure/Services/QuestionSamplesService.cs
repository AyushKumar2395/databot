using Application.Common.Interfaces;
using Application.Common.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

/// <summary>
/// Groups raw QuestionSamples rows into the API response shape and caches per environment.
/// </summary>
public sealed class QuestionSamplesService(
    IQuestionSamplesRepository repository,
    IMemoryCache cache,
    ILogger<QuestionSamplesService> logger) : IQuestionSamplesService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    private readonly IQuestionSamplesRepository _repository = repository;
    private readonly IMemoryCache _cache = cache;
    private readonly ILogger<QuestionSamplesService> _logger = logger;

    public async Task<QuestionSamplesResponse> GetAsync(
        string environment,
        bool refresh,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"QuestionSamples:{environment}";

        if (refresh)
        {
            _cache.Remove(cacheKey);
            _logger.LogInformation("Cache invalidated for {CacheKey}.", cacheKey);
        }

        if (_cache.TryGetValue<QuestionSamplesResponse>(cacheKey, out var cached) && cached is not null)
        {
            _logger.LogDebug("Cache hit for {CacheKey}.", cacheKey);
            return cached;
        }

        var rows = await _repository.GetByEnvironmentAsync(environment, cancellationToken);
        var response = BuildResponse(environment, rows);

        _cache.Set(cacheKey, response, CacheDuration);
        _logger.LogInformation(
            "Cached {GroupCount} groups ({ItemCount} questions) for {Environment}.",
            response.Groups.Count,
            response.Groups.Sum(g => g.Items.Count),
            environment);

        return response;
    }

    internal static QuestionSamplesResponse BuildResponse(string environment, List<QuestionSampleRow> rows)
    {
        var groups = rows
            .GroupBy(r => r.GroupKey)
            .Select(g =>
            {
                var first = g.First();
                return new QuestionSampleGroup
                {
                    GroupKey = first.GroupKey,
                    GroupTitle = first.GroupTitle,
                    Order = first.GroupOrder,
                    Items = g
                        .OrderBy(r => r.QuestionOrder)
                        .ThenBy(r => r.QuestionText)
                        .Select(r => new QuestionSampleItem
                        {
                            Id = r.Id,
                            Question = r.QuestionText,
                            Order = r.QuestionOrder,
                            Tags = ParseTags(r.Tags)
                        })
                        .ToList()
                };
            })
            .OrderBy(g => g.Order)
            .ThenBy(g => g.GroupKey)
            .ToList();

        return new QuestionSamplesResponse
        {
            Environment = environment,
            Groups = groups
        };
    }

    private static string[]? ParseTags(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags))
            return null;

        var parsed = tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parsed.Length > 0 ? parsed : null;
    }
}
