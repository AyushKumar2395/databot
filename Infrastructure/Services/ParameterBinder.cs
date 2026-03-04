using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Application.Common.Models;

namespace Infrastructure.Services;

internal static class ParameterBinder
{
    private static readonly HashSet<string> RejectedExtractValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "name",
        "database",
        "databases",
        "db",
        "contains",
        "like"
    };

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<ToolExtractRule>> FallbackExtractRulesByName =
        new Dictionary<string, IReadOnlyList<ToolExtractRule>>(StringComparer.OrdinalIgnoreCase)
        {
            ["NameContains"] =
            [
                // 1) Quoted value after contains/like
                new ToolExtractRule
                {
                    Regex = @"\b(db|database|databases)\b.*?\b(name\s*)?(contains|like)\b\s*'([^']+)'",
                    Group = 4
                },
                // 2) Unquoted single token after contains/like
                new ToolExtractRule
                {
                    Regex = @"\b(db|database|databases)\b.*?\b(name\s*)?(contains|like)\b\s*([a-z0-9_.-]+)",
                    Group = 4
                },
                // 2b) Alternate order: contains name X
                new ToolExtractRule
                {
                    Regex = @"\b(db|database|databases)\b.*?\b(contains|like)\b\s*name\s+'?([a-z0-9_.-]+)'?",
                    Group = 3
                },
                // 2c) Alternate order: database contains name X
                new ToolExtractRule
                {
                    Regex = @"\b(db|database|databases)\b.*?\bcontains\b\s+name\s+'?([a-z0-9_.-]+)'?",
                    Group = 2
                },
                // 3) Contains without explicit name
                new ToolExtractRule
                {
                    Regex = @"\b(db|database|databases)\b.*?\bcontains\b\s*'([^']+)'",
                    Group = 2
                },
                // 4) LIKE '%X%' pattern
                new ToolExtractRule
                {
                    Regex = @"\blike\b\s*'%([^%]+)%'",
                    Group = 1
                }
            ],
            ["TopN"] =
            [
                new ToolExtractRule
                {
                    Regex = @"\btop\s+(\d+)\b",
                    Group = 1
                }
            ],
            ["State"] =
            [
                new ToolExtractRule
                {
                    Regex = @"\b(online|offline|restoring|recovery_pending|suspect|emergency)\b",
                    Group = 1
                }
            ]
        };

    public static ParameterBindingResult Bind(
        ToolParameterSchema schema,
        string rawQuestion,
        string tunedQuestion,
        string? queryCode = null)
    {
        var bound = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var raw = rawQuestion ?? string.Empty;
        var tuned = tunedQuestion ?? string.Empty;
        var combined = $"{raw} {tuned}";

        foreach (var parameter in schema.Parameters)
        {
            if (string.IsNullOrWhiteSpace(parameter.Name))
                continue;

            var type = (parameter.Type ?? "string").Trim().ToLowerInvariant();
            var found = false;
            object? value = null;

            if (TryExtract(parameter, raw, tuned, type, out value))
            {
                found = true;
            }
            else if (TryDerive(parameter, raw, tuned, type, queryCode, combined, out value))
            {
                found = true;
            }
            else if (TryDefault(parameter, type, out value))
            {
                found = true;
            }

            if (!found && parameter.Required)
            {
                return ParameterBindingResult.Fail($"MISSING_PARAMETER:{parameter.Name}");
            }

            if (!found)
            {
                value = GetNeutralValue(type);
            }

            value = CoerceValue(value, type, parameter.Min, parameter.Max, parameter.Name);
            bound[parameter.Name] = value;
        }

        return ParameterBindingResult.Ok(bound);
    }

    private static bool TryDerive(
        ToolParameterDefinition parameter,
        string rawQuestion,
        string tunedQuestion,
        string type,
        string? queryCode,
        string combinedQuestion,
        out object? value)
    {
        value = null;

        if (parameter.Name.Equals("OnlyFailed", StringComparison.OrdinalIgnoreCase)
            && string.Equals(queryCode, "SQL_AGENT_JOBS_UNIFIED", StringComparison.OrdinalIgnoreCase)
            && ContainsAny(combinedQuestion, "fail", "failed", "fails", "failure", "failures", "errored"))
        {
            value = 1;
            return true;
        }

        if (parameter.Derive is null || parameter.Derive.ContainsAny.Count == 0 || !parameter.Derive.ValueIfTrue.HasValue)
            return false;

        var hit = parameter.Derive.ContainsAny.Any(phrase =>
            rawQuestion.Contains(phrase, StringComparison.OrdinalIgnoreCase) ||
            tunedQuestion.Contains(phrase, StringComparison.OrdinalIgnoreCase));
        if (!hit)
            return false;

        value = ConvertJsonElement(parameter.Derive.ValueIfTrue.Value, type);
        return true;
    }

    private static bool ContainsAny(string value, params string[] terms)
    {
        return terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryExtract(
        ToolParameterDefinition parameter,
        string rawQuestion,
        string tunedQuestion,
        string type,
        out object? value)
    {
        value = null;
        var rules = BuildExtractRules(parameter);
        if (rules.Count == 0)
            return false;

        foreach (var source in new[] { rawQuestion, tunedQuestion })
        {
            foreach (var rule in rules)
            {
                if (TryExtractByRule(rule, source, type, out value))
                    return true;
            }
        }

        return false;
    }

    private static List<ToolExtractRule> BuildExtractRules(ToolParameterDefinition parameter)
    {
        var rules = new List<ToolExtractRule>();

        rules.AddRange(parameter.GetAllExtractRules().Where(r => !string.IsNullOrWhiteSpace(r.Regex)));

        if (FallbackExtractRulesByName.TryGetValue(parameter.Name, out var fallbackRules))
            rules.AddRange(fallbackRules);

        return rules;
    }

    private static bool TryExtractByRule(ToolExtractRule rule, string source, string type, out object? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(rule.Regex) || string.IsNullOrWhiteSpace(source))
            return false;

        var match = Regex.Match(source, rule.Regex, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success)
            return false;

        var targetGroup = rule.Group <= 0 ? 1 : rule.Group;
        if (match.Groups.Count <= targetGroup)
            return false;

        var extracted = match.Groups[targetGroup].Value.Trim();
        if (string.IsNullOrWhiteSpace(extracted))
            return false;

        if (RejectedExtractValues.Contains(extracted.Trim('\'', '"', '%')))
            return false;

        if (type == "int" && rule.UnitGroup.HasValue)
        {
            if (!int.TryParse(extracted, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericValue))
                return false;

            var unitGroup = rule.UnitGroup.Value;
            var unit = match.Groups.Count > unitGroup ? match.Groups[unitGroup].Value : string.Empty;
            value = ConvertUnitToMinutes(numericValue, unit);
            return true;
        }

        value = extracted;
        return true;
    }

    private static int ConvertUnitToMinutes(int value, string unit)
    {
        if (string.IsNullOrWhiteSpace(unit))
            return value;

        var lower = unit.ToLowerInvariant();
        if (lower.StartsWith("hour"))
            return value * 60;
        if (lower.StartsWith("day"))
            return value * 1440;

        return value;
    }

    private static bool TryDefault(ToolParameterDefinition parameter, string type, out object? value)
    {
        value = null;
        if (!parameter.DefaultValue.HasValue)
            return false;

        var defaultElement = parameter.DefaultValue.Value;
        if (defaultElement.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return false;

        value = ConvertJsonElement(defaultElement, type);
        return true;
    }

    private static object? ConvertJsonElement(JsonElement element, string type)
    {
        return type switch
        {
            "int" => ConvertToInt(element),
            "bit" => ConvertToBit(element),
            _ => element.ValueKind switch
            {
                JsonValueKind.String => element.GetString() ?? string.Empty,
                JsonValueKind.Number => element.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => element.ToString()
            }
        };
    }

    private static object CoerceValue(object? value, string type, int? min, int? max, string parameterName)
    {
        return type switch
        {
            "int" => CoerceInt(value, min, max),
            "bit" => CoerceBit(value),
            _ => CoerceString(value, parameterName)
        };
    }

    private static int CoerceInt(object? value, int? min, int? max)
    {
        var parsed = ConvertToInt(value);
        if (min.HasValue && parsed < min.Value) parsed = min.Value;
        if (max.HasValue && parsed > max.Value) parsed = max.Value;
        return parsed;
    }

    private static int CoerceBit(object? value)
    {
        return ConvertToBit(value);
    }

    private static string CoerceString(object? value, string parameterName)
    {
        var text = (value?.ToString() ?? string.Empty).Trim();

        if (parameterName.Equals("NameContains", StringComparison.OrdinalIgnoreCase))
        {
            text = Regex.Replace(text, @"^(name|db|database|databases)\s+", string.Empty, RegexOptions.IgnoreCase);
            text = text.Trim('%').Trim('\'', '"', ' ', '.', ',', ';', ':');

            // NameContains must include at least one letter/number token.
            if (!Regex.IsMatch(text, "[a-z0-9]", RegexOptions.IgnoreCase))
                return string.Empty;
        }

        return text;
    }

    private static int ConvertToInt(object? value)
    {
        if (value is null) return 0;
        if (value is int i) return i;
        if (value is long l) return (int)l;
        if (value is bool b) return b ? 1 : 0;
        if (value is JsonElement jsonElement) return ConvertToInt(jsonElement);

        return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static int ConvertToInt(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var i) => i,
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            JsonValueKind.True => 1,
            _ => 0
        };
    }

    private static int ConvertToBit(object? value)
    {
        if (value is null) return 0;
        if (value is bool b) return b ? 1 : 0;
        if (value is int i) return i == 0 ? 0 : 1;
        if (value is JsonElement jsonElement)
        {
            return jsonElement.ValueKind switch
            {
                JsonValueKind.True => 1,
                JsonValueKind.False => 0,
                JsonValueKind.Number when jsonElement.TryGetInt32(out var parsedInt) => parsedInt == 0 ? 0 : 1,
                JsonValueKind.String when bool.TryParse(jsonElement.GetString(), out var parsedBool) => parsedBool ? 1 : 0,
                JsonValueKind.String when int.TryParse(jsonElement.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedStringInt) => parsedStringInt == 0 ? 0 : 1,
                _ => 0
            };
        }

        if (bool.TryParse(value.ToString(), out var boolValue))
            return boolValue ? 1 : 0;

        return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? (parsed == 0 ? 0 : 1)
            : 0;
    }

    private static object GetNeutralValue(string type)
    {
        return type switch
        {
            "int" => 0,
            "bit" => 0,
            _ => string.Empty
        };
    }
}

internal sealed class ParameterBindingResult
{
    public bool Success { get; private init; }
    public string? ErrorCode { get; private init; }
    public Dictionary<string, object?> BoundValues { get; private init; } =
        new(StringComparer.OrdinalIgnoreCase);

    public static ParameterBindingResult Ok(Dictionary<string, object?> values)
    {
        return new ParameterBindingResult
        {
            Success = true,
            BoundValues = values
        };
    }

    public static ParameterBindingResult Fail(string errorCode)
    {
        return new ParameterBindingResult
        {
            Success = false,
            ErrorCode = errorCode
        };
    }
}
