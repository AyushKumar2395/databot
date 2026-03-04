using System.Text.RegularExpressions;

namespace Infrastructure.Services;

internal static class IntentConstraintChecker
{
    public static IntentConstraintResult Check(
        string environment,
        string rawQuestion,
        string tunedQuestion,
        string validatedScript,
        Dictionary<string, object?> boundParameters,
        string? queryCode,
        string? toolTags)
    {
        if (!EnvironmentRules.IsSqlServer(environment))
            return IntentConstraintResult.Success();

        var tuned = (tunedQuestion ?? string.Empty).ToLowerInvariant();
        var raw = (rawQuestion ?? string.Empty).ToLowerInvariant();
        var script = (validatedScript ?? string.Empty).ToLowerInvariant();

        if (RequiresSqlSizeFilter(tuned, raw))
        {
            if (!ScriptHasSqlSizeFilter(script))
            {
                return IntentConstraintResult.Fail("INTENT_NOT_SATISFIED_SIZE_FILTER");
            }
        }

        if (RequiresDatabaseNameFilter(tuned, raw, queryCode, toolTags))
        {
            var nameContains = boundParameters.TryGetValue("NameContains", out var value)
                ? (value?.ToString() ?? string.Empty).Trim()
                : string.Empty;

            var hasBoundName = !string.IsNullOrWhiteSpace(nameContains)
                               && !string.Equals(nameContains, "name", StringComparison.OrdinalIgnoreCase);

            var hasLikeFilter = Regex.IsMatch(script, @"\blike\b", RegexOptions.IgnoreCase);
            if (!hasBoundName && !hasLikeFilter)
            {
                return IntentConstraintResult.Fail("INTENT_NOT_SATISFIED_NAME_FILTER");
            }
        }

        return IntentConstraintResult.Success();
    }

    private static bool RequiresSqlSizeFilter(string tuned, string raw)
    {
        var source = $"{tuned} {raw}";
        var hasDbContext = source.Contains("database", StringComparison.OrdinalIgnoreCase)
                           || Regex.IsMatch(source, @"\bdb\b", RegexOptions.IgnoreCase);
        if (!hasDbContext)
            return false;

        var hasSizeWord = source.Contains("size", StringComparison.OrdinalIgnoreCase);
        var hasThresholdUnit = Regex.IsMatch(source, @"(>|<|>=|<=)?\s*\d+\s*(gb|mb)\b", RegexOptions.IgnoreCase);
        if (!hasSizeWord && !hasThresholdUnit)
            return false;

        var hasComparison = Regex.IsMatch(
            source,
            @"(>|<|>=|<=|greater\s+than|less\s+than|more\s+than|under|over|at\s+least|at\s+most)",
            RegexOptions.IgnoreCase);

        return hasComparison || hasThresholdUnit;
    }

    private static bool ScriptHasSqlSizeFilter(string script)
    {
        var hasFileSource = script.Contains("sys.master_files", StringComparison.OrdinalIgnoreCase)
                            || script.Contains("sys.database_files", StringComparison.OrdinalIgnoreCase);
        if (!hasFileSource)
            return false;

        var hasSizeComputation = Regex.IsMatch(
            script,
            @"(size\s*/\s*128(\.0)?|size\s*\*\s*8|sizemb|sizegb)",
            RegexOptions.IgnoreCase);
        if (!hasSizeComputation)
            return false;

        var hasFilter = Regex.IsMatch(
            script,
            @"\b(where|having)\b[\s\S]{0,500}(>=|<=|>|<)",
            RegexOptions.IgnoreCase);

        return hasFilter;
    }

    private static bool RequiresDatabaseNameFilter(
        string tuned,
        string raw,
        string? queryCode,
        string? toolTags)
    {
        var source = $"{tuned} {raw}";
        var containsIntent = source.Contains("contains", StringComparison.OrdinalIgnoreCase)
                             || Regex.IsMatch(source, @"\blike\b", RegexOptions.IgnoreCase);
        if (!containsIntent)
            return false;

        if (source.Contains("database", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(source, @"\bdb\b", RegexOptions.IgnoreCase))
        {
            return true;
        }

        var context = $"{queryCode} {toolTags}";
        return context.Contains("DATABASE", StringComparison.OrdinalIgnoreCase);
    }
}

internal readonly record struct IntentConstraintResult(bool Ok, string? Reason)
{
    public static IntentConstraintResult Success() => new(true, null);

    public static IntentConstraintResult Fail(string reason) => new(false, reason);
}
