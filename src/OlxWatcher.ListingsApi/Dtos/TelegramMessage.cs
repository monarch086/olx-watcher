using System.Text.Json.Serialization;

namespace OlxWatcher.ListingsApi.Dtos;

internal sealed class TelegramMessage
{
    [JsonPropertyName("chat")]
    public TelegramChat? Chat { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }
}
