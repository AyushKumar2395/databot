using System.Text.Json.Serialization;

namespace Application.Common.Models;

/// <summary>
/// Incremental server-to-client update sent over SignalR for ask requests.
/// </summary>
public sealed class AskStreamUpdate
{
    [JsonPropertyName("event")]
    public string Event { get; set; } = string.Empty; // status | chunk | completed | validation_error | error

    [JsonPropertyName("stage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Stage { get; set; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Content { get; set; }

    [JsonPropertyName("code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code { get; set; }

    [JsonPropertyName("validationErrors")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string[]>? ValidationErrors { get; set; }

    [JsonPropertyName("response")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AskApiResponse? Response { get; set; }

    public static AskStreamUpdate Status(string stage, string? message = null)
    {
        return new AskStreamUpdate
        {
            Event = "status",
            Stage = stage,
            Message = message
        };
    }

    public static AskStreamUpdate Chunk(string content)
    {
        return new AskStreamUpdate
        {
            Event = "chunk",
            Stage = "response",
            Content = content
        };
    }

    public static AskStreamUpdate Completed(AskApiResponse response)
    {
        return new AskStreamUpdate
        {
            Event = "completed",
            Stage = "done",
            Response = response
        };
    }

    public static AskStreamUpdate ValidationFailed(Dictionary<string, string[]> validationErrors)
    {
        return new AskStreamUpdate
        {
            Event = "validation_error",
            Stage = "validation",
            Message = "Request validation failed.",
            ValidationErrors = validationErrors
        };
    }

    public static AskStreamUpdate Error(string code, string message)
    {
        return new AskStreamUpdate
        {
            Event = "error",
            Stage = "pipeline",
            Code = code,
            Message = message
        };
    }
}
