using System.Text.Json.Serialization;

namespace Application.Common.Models;

/// <summary>
/// UI payload contract for /api/ask.
/// </summary>
public sealed class AskApiRequest
{
    [JsonPropertyName("conversationId")]
    public string ConversationId { get; set; } = string.Empty;

    [JsonPropertyName("BearerToken")]
    public string BearerToken { get; set; } = string.Empty;

    [JsonPropertyName("environment")]
    public string Environment { get; set; } = string.Empty;

    [JsonPropertyName("question")]
    public string Question { get; set; } = string.Empty;

    [JsonPropertyName("selectedTargets")]
    public string[] SelectedTargets { get; set; } = [];

    /// <summary>Legacy key — clients that still send "selectedServers" are silently remapped to SelectedTargets.</summary>
    [JsonPropertyName("selectedServers")]
    public string[]? SelectedServers { get; set; }
}
