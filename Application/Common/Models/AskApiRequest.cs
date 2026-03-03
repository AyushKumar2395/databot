using System.Text.Json.Serialization;

namespace Application.Common.Models;

/// <summary>
/// UI payload contract for /api/ask.
/// </summary>
public sealed class AskApiRequest
{
    [JsonPropertyName("conversationId")]
    public string ConversationId { get; set; } = string.Empty;

    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("environment")]
    public string Environment { get; set; } = string.Empty;

    [JsonPropertyName("question")]
    public string Question { get; set; } = string.Empty;

    [JsonPropertyName("selectedServers")]
    public string[] SelectedServers { get; set; } = [];
}
