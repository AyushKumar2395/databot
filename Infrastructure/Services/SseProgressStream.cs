using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Application.Common.Interfaces;
using Application.Common.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

/// <summary>
/// Writes Server-Sent Events to an ASP.NET Core HttpResponse.
/// Each EmitAsync call serializes the event and writes:
///   data: {json}\n\n
/// The stream is flushed after every event so proxies and browsers receive each event immediately.
/// </summary>
public sealed class SseProgressStream(
    HttpResponse response,
    string conversationId,
    string bearerToken,
    ILogger<SseProgressStream> logger) : IProgressStream
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async ValueTask EmitAsync(ProgressEvent e, CancellationToken ct = default)
    {
        try
        {
            // Augment with correlation keys before serializing.
            var augmented = new SseEnvelope
            {
                Type = e.Type,
                TsUtc = e.TsUtc,
                ConversationId = conversationId,
                BearerToken = bearerToken,
                Phase = e.Phase,
                Target = e.Target,
                Message = e.Message,
                Data = e.Data,
                Response = e.Response
            };

            var json = JsonSerializer.Serialize(augmented, JsonOptions);
            // SSE wire format: "data: {payload}\n\n"
            var bytes = Encoding.UTF8.GetBytes($"data: {json}\n\n");

            await response.Body.WriteAsync(bytes, ct);
            await response.Body.FlushAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — propagate so the pipeline stops.
            throw;
        }
        catch (Exception ex)
        {
            // Never crash the pipeline due to a broken client connection.
            logger.LogWarning(ex, "SSE emit failed (client likely disconnected). ConversationId={ConversationId}", conversationId);
        }
    }

    // Internal envelope that adds the correlation fields.
    private sealed class SseEnvelope
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = string.Empty;

        [JsonPropertyName("tsUtc")]
        public string TsUtc { get; init; } = string.Empty;

        [JsonPropertyName("conversationId")]
        public string ConversationId { get; init; } = string.Empty;

        [JsonPropertyName("BearerToken")]
        public string BearerToken { get; init; } = string.Empty;

        [JsonPropertyName("phase")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Phase { get; init; }

        [JsonPropertyName("target")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Target { get; init; }

        [JsonPropertyName("message")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Message { get; init; }

        [JsonPropertyName("data")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Data { get; init; }

        [JsonPropertyName("response")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public AskApiResponse? Response { get; init; }
    }
}
