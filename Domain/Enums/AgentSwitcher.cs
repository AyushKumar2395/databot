using System.Text.Json.Serialization;

namespace Domain.Enums;

[JsonConverter(typeof(JsonStringEnumConverter<AgentSwitcher>))]
public enum AgentSwitcher
{
    GPT5Mini,
    Gemini2_5FlashLite
}
