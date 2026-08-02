using System.Net;
using System.Net.Http.Json;
using Amazon.Lambda.Core;

namespace OlxWatcher.Shared.Telegram;

public static class TelegramServiceNotifier
{
    public const string ServiceNotificationChatId = "38627946";

    public static async Task SendWatchAddedNotificationSafelyAsync(
        HttpClient httpClient,
        string lambdaName,
        string watcherChatId,
        string productName,
        string productPrice,
        string productUrl,
        ILambdaLogger logger)
    {
        var text = $"<b>Нове оголошення у відстеженні</b>\n"
            + FormatSource(lambdaName)
            + $"<b>Чат:</b> {WebUtility.HtmlEncode(watcherChatId)}\n"
            + $"<b>Товар:</b> {WebUtility.HtmlEncode(productName)}\n"
            + $"<b>Ціна:</b> {WebUtility.HtmlEncode(productPrice)}\n"
            + WebUtility.HtmlEncode(productUrl);

        await SendSafelyAsync(httpClient, text, $"newly watched product in Telegram chat {watcherChatId}", logger);
    }

    public static async Task SendErrorNotificationSafelyAsync(
        HttpClient httpClient,
        string lambdaName,
        string errorContext,
        Exception exception,
        ILambdaLogger logger)
    {
        var rawDetails = exception.ToString();
        if (rawDetails.Length > 2_500)
        {
            rawDetails = rawDetails[..2_497] + "...";
        }

        var text = $"<b>Помилка</b>\n"
            + FormatSource(lambdaName)
            + $"<b>Етап:</b> {WebUtility.HtmlEncode(errorContext)}\n"
            + $"<pre>{WebUtility.HtmlEncode(rawDetails)}</pre>";

        await SendSafelyAsync(httpClient, text, $"error notification from {lambdaName}", logger);
    }

    private static async Task SendSafelyAsync(
        HttpClient httpClient,
        string text,
        string notificationDescription,
        ILambdaLogger logger)
    {
        try
        {
            var serviceBotToken = Environment.GetEnvironmentVariable("TELEGRAM_SERVICE_BOT_TOKEN")
                ?? throw new InvalidOperationException("Required environment variable TELEGRAM_SERVICE_BOT_TOKEN is not set.");
            using var response = await httpClient.PostAsJsonAsync(
                $"https://api.telegram.org/bot{serviceBotToken}/sendMessage",
                new
                {
                    chat_id = ServiceNotificationChatId,
                    text,
                    parse_mode = "HTML",
                    disable_web_page_preview = true
                });
            logger.LogInformation($"Telegram service notification for {notificationDescription} returned HTTP {(int)response.StatusCode}.");
            response.EnsureSuccessStatusCode();
        }
        catch (Exception notificationException)
        {
            logger.LogError(notificationException, $"Unable to send Telegram service notification for {notificationDescription}.");
        }
    }

    private static string FormatSource(string lambdaName)
    {
        var stage = Environment.GetEnvironmentVariable("STAGE") ?? "unknown";
        return $"<b>Lambda:</b> {WebUtility.HtmlEncode(lambdaName)}\n"
            + $"<b>Середовище:</b> {WebUtility.HtmlEncode(stage)}\n";
    }
}
