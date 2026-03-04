using System.Text.Json;
using System.Text.Json.Serialization;

namespace Application.Common.Models;

/// <summary>
/// Deserialized ToolRegistry.ParameterSchema contract used for deterministic parameter binding.
/// </summary>
public sealed class ToolParameterSchema
{
    [JsonPropertyName("parameters")]
    public List<ToolParameterDefinition> Parameters { get; set; } = [];

    [JsonPropertyName("safety")]
    public ToolSafety Safety { get; set; } = new();
}

public sealed class ToolParameterDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "string";

    [JsonPropertyName("required")]
    public bool Required { get; set; }

    [JsonPropertyName("default")]
    public JsonElement? DefaultValue { get; set; }

    [JsonPropertyName("min")]
    public int? Min { get; set; }

    [JsonPropertyName("max")]
    public int? Max { get; set; }

    [JsonPropertyName("extract")]
    public ToolExtractRule? Extract { get; set; }

    [JsonPropertyName("extractAlt")]
    public List<ToolExtractRule> ExtractAlt { get; set; } = [];

    [JsonPropertyName("derive")]
    public ToolDeriveRule? Derive { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtraProperties { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ToolExtractRule> GetAllExtractRules()
    {
        var rules = new List<ToolExtractRule>();

        if (Extract is not null && !string.IsNullOrWhiteSpace(Extract.Regex))
            rules.Add(Extract);

        foreach (var alt in ExtractAlt.Where(r => !string.IsNullOrWhiteSpace(r.Regex)))
            rules.Add(alt);

        foreach (var kv in ExtraProperties)
        {
            if (!kv.Key.StartsWith("extract", StringComparison.OrdinalIgnoreCase))
                continue;

            if (kv.Value.ValueKind != JsonValueKind.Object)
                continue;

            var parsed = ParseExtractRule(kv.Value);
            if (parsed is not null && !string.IsNullOrWhiteSpace(parsed.Regex))
                rules.Add(parsed);
        }

        return rules;
    }

    private static ToolExtractRule? ParseExtractRule(JsonElement extractElement)
    {
        if (!extractElement.TryGetProperty("regex", out var regexElement))
            return null;

        var regex = regexElement.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(regex))
            return null;

        var rule = new ToolExtractRule
        {
            Regex = regex
        };

        if (extractElement.TryGetProperty("group", out var groupElement))
        {
            if (groupElement.ValueKind == JsonValueKind.Number && groupElement.TryGetInt32(out var group))
                rule.Group = group;
            else if (groupElement.ValueKind == JsonValueKind.String &&
                     int.TryParse(groupElement.GetString(), out var parsedGroup))
                rule.Group = parsedGroup;
        }

        if (extractElement.TryGetProperty("unitGroup", out var unitGroupElement))
        {
            if (unitGroupElement.ValueKind == JsonValueKind.Number &&
                unitGroupElement.TryGetInt32(out var unitGroup))
            {
                rule.UnitGroup = unitGroup;
            }
            else if (unitGroupElement.ValueKind == JsonValueKind.String &&
                     int.TryParse(unitGroupElement.GetString(), out var parsedUnitGroup))
            {
                rule.UnitGroup = parsedUnitGroup;
            }
        }

        return rule;
    }
}

public sealed class ToolExtractRule
{
    [JsonPropertyName("regex")]
    public string Regex { get; set; } = string.Empty;

    [JsonPropertyName("group")]
    public int Group { get; set; } = 1;

    [JsonPropertyName("unitGroup")]
    public int? UnitGroup { get; set; }
}

public sealed class ToolDeriveRule
{
    [JsonPropertyName("containsAny")]
    public List<string> ContainsAny { get; set; } = [];

    [JsonPropertyName("valueIfTrue")]
    public JsonElement? ValueIfTrue { get; set; }
}

public sealed class ToolSafety
{
    [JsonPropertyName("readOnly")]
    public bool ReadOnly { get; set; } = true;

    [JsonPropertyName("blockedKeywords")]
    public List<string> BlockedKeywords { get; set; } = [];
}
