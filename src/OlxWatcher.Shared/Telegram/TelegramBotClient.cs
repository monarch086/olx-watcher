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
        using var response = await _httpClient.PostAsJsonAsync(
            $"https://api.telegram.org/bot{botToken}/sendMessage",
            new
            {
                chat_id = chatId,
                text,
                parse_mode = parseMode,
                disable_web_page_preview = disableWebPagePreview
            },
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
