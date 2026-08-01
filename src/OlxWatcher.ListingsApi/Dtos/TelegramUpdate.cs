using System.Text.Json.Serialization;

namespace OlxWatcher.ListingsApi.Dtos;

internal sealed class TelegramUpdate
{
    [JsonPropertyName("update_id")]
    public long UpdateId { get; init; }

    [JsonPropertyName("message")]
    public TelegramMessage? Message { get; init; }
}
