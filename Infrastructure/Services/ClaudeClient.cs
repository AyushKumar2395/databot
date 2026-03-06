using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Application.Common.Interfaces;
using Application.Common.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

/// <summary>
/// Anthropic Claude LLM client.
/// Calls the Anthropic Messages API when an API key is configured.
/// Falls back to deterministic mock output when the key is absent.
/// </summary>
public sealed class ClaudeClient(
    IOptions<LlmOptions> llmOptions,
    IHttpClientFactory httpClientFactory,
    ILogger<ClaudeClient> logger) : ILLMClient
{
    private const string AnthropicVersion = "2023-06-01";
    private const string MessagesEndpoint = "https://api.anthropic.com/v1/messages";
    private const int MaxTokens = 4096;

    private readonly LlmOptions _llmOptions = llmOptions.Value;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly ILogger<ClaudeClient> _logger = logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string Provider => "Claude";

    public async Task<string> TuneAsync(
        string promptTemplate,
        string rawQuestion,
        string environmentTag,
        string routedQueryCode,
        string modelKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_llmOptions.Claude.ApiKey))
        {
            _logger.LogWarning(
                "Claude API key is not configured. Using deterministic tuning mock for model {ModelKey}.",
                modelKey);
            return MockLlmBehavior.BuildTuneLine(rawQuestion, environmentTag, routedQueryCode);
        }

        var prompt = FillTemplate(promptTemplate, new Dictionary<string, string>
        {
            ["rawUserQuestion"] = rawQuestion,
            ["environmentTag"] = environmentTag,
            ["routedQueryCode"] = routedQueryCode ?? string.Empty
        });

        try
        {
            _logger.LogInformation("Using Claude tuning model {ModelKey}.", modelKey);
            var result = await CallClaudeAsync(prompt, modelKey, cancellationToken);
            return string.IsNullOrWhiteSpace(result)
                ? MockLlmBehavior.BuildTuneLine(rawQuestion, environmentTag, routedQueryCode ?? string.Empty)
                : result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Claude tuning call failed for model {ModelKey}. Falling back to deterministic mock.",
                modelKey);
            return MockLlmBehavior.BuildTuneLine(rawQuestion, environmentTag, routedQueryCode ?? string.Empty);
        }
    }

    public async Task<string> GenerateAsync(
        string promptTemplate,
        string tunedQuestion,
        string environmentTag,
        string modelKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_llmOptions.Claude.ApiKey))
        {
            _logger.LogWarning(
                "Claude API key is not configured. Using deterministic generate mock for model {ModelKey}.",
                modelKey);
            return MockLlmBehavior.BuildGenerateScript(environmentTag, tunedQuestion);
        }

        var prompt = FillTemplate(promptTemplate, new Dictionary<string, string>
        {
            ["task"] = tunedQuestion,
            ["environmentTag"] = environmentTag
        });

        try
        {
            _logger.LogInformation("Using Claude generate model {ModelKey}.", modelKey);
            var result = await CallClaudeAsync(prompt, modelKey, cancellationToken);
            return string.IsNullOrWhiteSpace(result)
                ? MockLlmBehavior.BuildGenerateScript(environmentTag, tunedQuestion)
                : result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Claude generate call failed for model {ModelKey}. Falling back to deterministic mock.",
                modelKey);
            return MockLlmBehavior.BuildGenerateScript(environmentTag, tunedQuestion);
        }
    }

    public async Task<string> ValidateTemplateAsync(
        string promptTemplate,
        string tunedQuestion,
        string environmentTag,
        string modelKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_llmOptions.Claude.ApiKey))
        {
            _logger.LogWarning(
                "Claude API key is not configured. Using deterministic template-validate mock for model {ModelKey}.",
                modelKey);
            return MockLlmBehavior.BuildValidateTemplateJson(environmentTag, tunedQuestion, promptTemplate);
        }

        var prompt = FillTemplate(promptTemplate, new Dictionary<string, string>
        {
            ["task"] = tunedQuestion,
            ["environmentTag"] = environmentTag
        });

        try
        {
            _logger.LogInformation("Using Claude validate model {ModelKey}.", modelKey);
            var result = await CallClaudeAsync(prompt, modelKey, cancellationToken);
            return string.IsNullOrWhiteSpace(result)
                ? MockLlmBehavior.BuildValidateTemplateJson(environmentTag, tunedQuestion, promptTemplate)
                : result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Claude validate call failed for model {ModelKey}. Falling back to deterministic mock.",
                modelKey);
            return MockLlmBehavior.BuildValidateTemplateJson(environmentTag, tunedQuestion, promptTemplate);
        }
    }

    // Replaces {{$varName}} tokens in a Semantic Kernel prompt template.
    private static string FillTemplate(string template, Dictionary<string, string> variables)
    {
        return Regex.Replace(template, @"\{\{\$(\w+)\}\}", m =>
        {
            var key = m.Groups[1].Value;
            return variables.TryGetValue(key, out var value) ? value : string.Empty;
        });
    }

    private async Task<string> CallClaudeAsync(string prompt, string modelKey, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("Claude");

        using var request = new HttpRequestMessage(HttpMethod.Post, MessagesEndpoint);
        request.Headers.Add("x-api-key", _llmOptions.Claude.ApiKey);
        request.Headers.Add("anthropic-version", AnthropicVersion);

        var body = new
        {
            model = modelKey,
            max_tokens = MaxTokens,
            messages = new[] { new { role = "user", content = prompt } }
        };

        request.Content = JsonContent.Create(body, options: JsonOptions);

        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var doc = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: ct)
                  ?? throw new InvalidOperationException("Empty response from Claude API.");

        // Response shape: { "content": [{ "type": "text", "text": "..." }] }
        return doc.RootElement
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString() ?? string.Empty;
    }
}
