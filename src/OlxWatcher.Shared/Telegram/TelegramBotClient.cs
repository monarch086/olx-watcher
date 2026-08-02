using System.Net.Http.Json;

namespace OlxWatcher.Shared.Telegram;

public sealed class TelegramBotClient
{
    private readonly HttpClient _httpClient;

    public TelegramBotClient(HttpClient httpClient) => _httpClient = httpClient;

    public async Task SendMessageAsync(
        string botToken,
        string chatId,
        string text,
        string? parseMode = null,
        bool disableWebPagePreview = false,
        CancellationToken cancellationToken = default)
    {
        var message = new Dictionary<string, object?>
        {
            ["chat_id"] = chatId,
            ["text"] = text,
            ["disable_web_page_preview"] = disableWebPagePreview
        };
        if (!string.IsNullOrWhiteSpace(parseMode))
        {
            message["parse_mode"] = parseMode;
        }

        using var response = await _httpClient.PostAsJsonAsync(
            $"https://api.telegram.org/bot{botToken}/sendMessage",
            message,
            cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (responseBody.Length > 1_000)
        {
            responseBody = responseBody[..997] + "...";
        }

        throw new HttpRequestException(
            $"Telegram sendMessage returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). Response: {responseBody}",
            null,
            response.StatusCode);
    }
}
