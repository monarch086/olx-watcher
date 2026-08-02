using System.Net;
using System.Net.Http.Json;
using Amazon.Lambda.Core;

namespace OlxWatcher.Shared.Telegram;

public static class TelegramErrorNotifier
{
    public const string ErrorNotificationChatId = "38627946";

    public static async Task SendErrorNotificationSafelyAsync(
        HttpClient httpClient,
        string serviceName,
        string errorContext,
        Exception exception,
        ILambdaLogger logger)
    {
        try
        {
            var serviceBotToken = Environment.GetEnvironmentVariable("TELEGRAM_SERVICE_BOT_TOKEN")
                ?? throw new InvalidOperationException("Required environment variable TELEGRAM_SERVICE_BOT_TOKEN is not set.");
            var rawDetails = exception.ToString();
            if (rawDetails.Length > 2_500)
            {
                rawDetails = rawDetails[..2_497] + "...";
            }

            var text = $"<b>Помилка {WebUtility.HtmlEncode(serviceName)}</b>\n<b>Етап:</b> {WebUtility.HtmlEncode(errorContext)}\n<pre>{WebUtility.HtmlEncode(rawDetails)}</pre>";
            using var response = await httpClient.PostAsJsonAsync(
                $"https://api.telegram.org/bot{serviceBotToken}/sendMessage",
                new
                {
                    chat_id = ErrorNotificationChatId,
                    text,
                    parse_mode = "HTML",
                    disable_web_page_preview = true
                });
            logger.LogInformation($"Telegram error notification returned HTTP {(int)response.StatusCode}.");
            response.EnsureSuccessStatusCode();
        }
        catch (Exception notificationException)
        {
            logger.LogError(notificationException, "Unable to send ListingsWatcher error notification.");
        }
    }
}
