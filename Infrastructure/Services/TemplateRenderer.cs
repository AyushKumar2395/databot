using System.Text.RegularExpressions;
using Application.Common.Interfaces;
using Application.Common.Models;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

/// <summary>
/// Deterministic template renderer for ToolRegistry scripts.
/// </summary>
public sealed class TemplateRenderer(ILogger<TemplateRenderer> logger) : ITemplateRenderer
{
    private readonly ILogger<TemplateRenderer> _logger = logger;

    public TemplateRenderResult Render(TemplateRenderRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ScriptTemplate))
        {
            return new TemplateRenderResult
            {
                Success = false,
                ErrorCode = "TEMPLATE_EMPTY"
            };
        }

        var schemaResult = ParseSchema(request.ParameterSchemaJson);
        if (!schemaResult.Success || schemaResult.Schema is null)
        {
            return new TemplateRenderResult
            {
                Success = false,
                ErrorCode = schemaResult.ErrorCode ?? "PARAMETER_SCHEMA_INVALID"
            };
        }

        var binding = ParameterBinder.Bind(
            schemaResult.Schema,
            request.RawQuestion,
            request.TunedQuestion,
            request.QueryCode);
        if (!binding.Success)
        {
            return new TemplateRenderResult
            {
                Success = false,
                ErrorCode = binding.ErrorCode
            };
        }

        if (HasNameContainsIntent(request.RawQuestion, request.TunedQuestion)
            && schemaResult.Schema.Parameters.Any(p =>
                p.Name.Equals("NameContains", StringComparison.OrdinalIgnoreCase))
            && binding.BoundValues.TryGetValue("NameContains", out var nameContainsValue)
            && string.IsNullOrWhiteSpace(nameContainsValue?.ToString()))
        {
            return new TemplateRenderResult
            {
                Success = false,
                ErrorCode = "PARAM_BIND_FAILED_NameContains"
            };
        }

        var rendered = RenderTemplate(
            request.ScriptTemplate,
            request.ScriptLanguage,
            binding.BoundValues,
            schemaResult.Schema.Parameters);

        if (!rendered.Success)
        {
            return new TemplateRenderResult
            {
                Success = false,
                ErrorCode = rendered.ErrorCode
            };
        }

        if (rendered.RenderedScript.Contains("{{", StringComparison.Ordinal) ||
            rendered.RenderedScript.Contains("}}", StringComparison.Ordinal))
        {
            return new TemplateRenderResult
            {
                Success = false,
                ErrorCode = "UNRESOLVED_PLACEHOLDER"
            };
        }

        _logger.LogInformation(
            "Template rendered for environment {Environment}. ScriptLanguage={ScriptLanguage}, Params={ParameterCount}",
            request.Environment,
            request.ScriptLanguage,
            binding.BoundValues.Count);

        return new TemplateRenderResult
        {
            Success = true,
            RenderedScript = rendered.RenderedScript,
            BoundParameters = binding.BoundValues,
            BlockedKeywords = schemaResult.Schema.Safety.BlockedKeywords
                .Where(token => !string.IsNullOrWhiteSpace(token))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Schema = schemaResult.Schema
        };
    }

    public TemplateRenderResult RenderWithBoundParameters(TemplateRenderBoundRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ScriptTemplate))
        {
            return new TemplateRenderResult
            {
                Success = false,
                ErrorCode = "TEMPLATE_EMPTY"
            };
        }

        var schema = request.Schema ?? new ToolParameterSchema();
        var bound = request.BoundParameters ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        var rendered = RenderTemplate(
            request.ScriptTemplate,
            request.ScriptLanguage,
            bound,
            schema.Parameters);

        if (!rendered.Success)
        {
            return new TemplateRenderResult
            {
                Success = false,
                ErrorCode = rendered.ErrorCode
            };
        }

        if (rendered.RenderedScript.Contains("{{", StringComparison.Ordinal) ||
            rendered.RenderedScript.Contains("}}", StringComparison.Ordinal))
        {
            return new TemplateRenderResult
            {
                Success = false,
                ErrorCode = "UNRESOLVED_PLACEHOLDER"
            };
        }

        return new TemplateRenderResult
        {
            Success = true,
            RenderedScript = rendered.RenderedScript,
            BoundParameters = new Dictionary<string, object?>(bound, StringComparer.OrdinalIgnoreCase),
            BlockedKeywords = schema.Safety.BlockedKeywords
                .Where(token => !string.IsNullOrWhiteSpace(token))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Schema = schema
        };
    }

    private static bool HasNameContainsIntent(string rawQuestion, string tunedQuestion)
    {
        var merged = $"{rawQuestion} {tunedQuestion}";
        return merged.Contains("contains", StringComparison.OrdinalIgnoreCase)
               || merged.Contains(" like ", StringComparison.OrdinalIgnoreCase)
               || Regex.IsMatch(merged, @"\blike\b", RegexOptions.IgnoreCase);
    }

    private static SchemaParseResult ParseSchema(string? parameterSchemaJson)
    {
        if (string.IsNullOrWhiteSpace(parameterSchemaJson))
        {
            return SchemaParseResult.Ok(new ToolParameterSchema());
        }

        try
        {
            var schema = System.Text.Json.JsonSerializer.Deserialize<ToolParameterSchema>(
                parameterSchemaJson,
                new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

            return schema is null
                ? SchemaParseResult.Fail("PARAMETER_SCHEMA_INVALID")
                : SchemaParseResult.Ok(schema);
        }
        catch (Exception)
        {
            return SchemaParseResult.Fail("PARAMETER_SCHEMA_INVALID");
        }
    }

    private static RenderProcessResult RenderTemplate(
        string template,
        string scriptLanguage,
        Dictionary<string, object?> boundValues,
        List<ToolParameterDefinition> parameterDefinitions)
    {
        var placeholders = Regex.Matches(template, @"\{\{\s*(?<name>[A-Za-z0-9_]+)\s*\}\}")
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var placeholder in placeholders)
        {
            if (!boundValues.ContainsKey(placeholder))
            {
                return RenderProcessResult.Fail($"MISSING_PARAMETER:{placeholder}");
            }
        }

        var typeByParam = parameterDefinitions
            .Where(param => !string.IsNullOrWhiteSpace(param.Name))
            .ToDictionary(
                param => param.Name,
                param => param.Type ?? "string",
                StringComparer.OrdinalIgnoreCase);

        var rendered = Regex.Replace(
            template,
            @"\{\{\s*(?<name>[A-Za-z0-9_]+)\s*\}\}",
            match =>
            {
                var name = match.Groups["name"].Value;
                boundValues.TryGetValue(name, out var value);
                typeByParam.TryGetValue(name, out var type);
                return FormatLiteral(scriptLanguage, type ?? "string", value);
            });

        return RenderProcessResult.Ok(rendered);
    }

    private static string FormatLiteral(string scriptLanguage, string type, object? value)
    {
        var normalizedType = (type ?? "string").Trim().ToLowerInvariant();
        var isSql = string.Equals(scriptLanguage, "SQL", StringComparison.OrdinalIgnoreCase);
        var isPs = string.Equals(scriptLanguage, "PS", StringComparison.OrdinalIgnoreCase);

        if (normalizedType is "int" or "bit")
        {
            var numeric = ConvertToNumeric(value, normalizedType);
            return numeric.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var text = value?.ToString() ?? string.Empty;
        text = text.Replace("'", "''", StringComparison.Ordinal);

        if (isSql)
            return $"N'{text}'";

        if (isPs)
            return $"'{text}'";

        return text;
    }

    private static int ConvertToNumeric(object? value, string normalizedType)
    {
        if (value is bool boolValue)
            return boolValue ? 1 : 0;

        if (value is int intValue)
            return normalizedType == "bit" ? (intValue == 0 ? 0 : 1) : intValue;

        if (int.TryParse(value?.ToString(), out var parsed))
            return normalizedType == "bit" ? (parsed == 0 ? 0 : 1) : parsed;

        if (bool.TryParse(value?.ToString(), out var parsedBool))
            return parsedBool ? 1 : 0;

        return 0;
    }

    private sealed class RenderProcessResult
    {
        public bool Success { get; private init; }
        public string? ErrorCode { get; private init; }
        public string RenderedScript { get; private init; } = string.Empty;

        public static RenderProcessResult Ok(string renderedScript)
        {
            return new RenderProcessResult
            {
                Success = true,
                RenderedScript = renderedScript
            };
        }

        public static RenderProcessResult Fail(string errorCode)
        {
            return new RenderProcessResult
            {
                Success = false,
                ErrorCode = errorCode
            };
        }
    }

    private sealed class SchemaParseResult
    {
        public bool Success { get; private init; }
        public string? ErrorCode { get; private init; }
        public ToolParameterSchema? Schema { get; private init; }

        public static SchemaParseResult Ok(ToolParameterSchema schema)
        {
            return new SchemaParseResult
            {
                Success = true,
                Schema = schema
            };
        }

        public static SchemaParseResult Fail(string errorCode)
        {
            return new SchemaParseResult
            {
                Success = false,
                ErrorCode = errorCode
            };
        }
    }
}
