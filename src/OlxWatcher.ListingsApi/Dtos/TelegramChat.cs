using System.Text.Json.Serialization;

namespace OlxWatcher.ListingsApi.Dtos;

internal sealed class TelegramChat
{
    [JsonPropertyName("id")]
    public long Id { get; init; }
}
