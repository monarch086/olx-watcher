using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.Lambda.CloudWatchEvents;
using Amazon.Lambda.Core;
using OlxWatcher.Shared;
using OlxWatcher.Shared.DynamoDb;
using OlxWatcher.Shared.Dtos;
using OlxWatcher.Shared.Olx;
using OlxWatcher.Shared.Telegram;

namespace OlxWatcher.ListingsWatcher;

public sealed class ListingsWatcherService
{
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly Uri NbuUsdRateUri = new("https://bank.gov.ua/NBUStatService/v1/statdirectory/exchangenew?json&valcode=USD");
    private readonly WatchedProductRepository _watchedProducts;
    private readonly ProductPriceHistoryRepository _priceHistory;
    private readonly TelegramNotificationOutboxRepository _notificationOutbox;
    private readonly OlxProductClient _olxProductClient;
    private readonly TelegramBotClient _telegramBotClient;

    public ListingsWatcherService() : this(new AmazonDynamoDBClient())
    {
    }

    internal ListingsWatcherService(IAmazonDynamoDB dynamoDb)
    {
        _watchedProducts = new WatchedProductRepository(dynamoDb, RequiredEnvironmentVariable("WATCHED_PRODUCTS_TABLE"));
        _priceHistory = new ProductPriceHistoryRepository(dynamoDb, RequiredEnvironmentVariable("PRODUCT_PRICE_HISTORY_TABLE"));
        _notificationOutbox = new TelegramNotificationOutboxRepository(dynamoDb, RequiredEnvironmentVariable("TELEGRAM_NOTIFICATION_OUTBOX_TABLE"));
        _olxProductClient = new OlxProductClient(HttpClient);
        _telegramBotClient = new TelegramBotClient(HttpClient);
    }

    public async Task RunAsync(CloudWatchEvent<object> scheduledEvent, ILambdaContext context)
    {
        var logger = context.Logger;
        try
        {
            logger.LogInformation($"Starting scheduled listing check at {DateTimeOffset.UtcNow:O}.");

            var watchedProducts = await _watchedProducts.GetAllAsync();

            var productGroups = watchedProducts
                .GroupBy(product => string.IsNullOrWhiteSpace(product.ProductId)
                    ? $"url:{product.ProductUrl}"
                    : $"id:{product.ProductId}", StringComparer.Ordinal)
                .Select(group => group.ToList())
                .ToList();
            logger.LogInformation($"Loaded {watchedProducts.Count} watched products in {productGroups.Count} distinct product groups.");

            var processed = 0;
            var maxConcurrency = GetMaxCheckConcurrency();
            logger.LogInformation($"Checking product groups with a maximum concurrency of {maxConcurrency}.");
            await Parallel.ForEachAsync(
                productGroups,
                new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency },
                async (group, _) =>
                {
                    if (!group.Any(product => ShouldCheckProduct(product, logger)))
                    {
                        return;
                    }

                    try
                    {
                        await CheckProductGroupAsync(group, logger);
                        Interlocked.Add(ref processed, group.Count);
                    }
                    catch (Exception exception)
                    {
                        var productId = group[0].ProductId ?? group[0].ProductUrl;
                        logger.LogError(exception, $"Unable to check watched product group {productId}.");
                        await SendErrorNotificationAsync($"Перевірка оголошення {productId}", exception, logger);
                    }
                });

            await DeliverPendingNotificationsAsync(logger);

            logger.LogInformation($"Completed scheduled listing check. Processed {processed} watched products.");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Listings watcher execution failed.");
            await SendErrorNotificationAsync("Запуск ListingsWatcher", exception, logger);
        }
    }

    private static bool ShouldCheckProduct(WatchedProductDto product, ILambdaLogger logger)
    {
        if (product.IsActive is not false || product.LastCheckedAt is null)
        {
            return true;
        }

        var nextCheckAt = product.LastCheckedAt.Value.AddDays(1);
        if (nextCheckAt <= DateTimeOffset.UtcNow)
        {
            return true;
        }

        logger.LogInformation($"Skipping inactive watched product for Telegram chat {product.ChatId} until {nextCheckAt:O}.");
        return false;
    }

    private static int GetMaxCheckConcurrency()
    {
        const int defaultConcurrency = 5;
        const int maxAllowedConcurrency = 20;
        var value = Environment.GetEnvironmentVariable("WATCHER_MAX_CONCURRENCY");
        return int.TryParse(value, out var concurrency) && concurrency is > 0 and <= maxAllowedConcurrency
            ? concurrency
            : defaultConcurrency;
    }

    private async Task CheckProductGroupAsync(IReadOnlyList<WatchedProductDto> products, ILambdaLogger logger)
    {
        var representativeProduct = products[0];
        logger.LogInformation($"Checking product {representativeProduct.ProductId ?? representativeProduct.ProductUrl} for {products.Count} watcher(s).");
        var actual = await GetProductDetailsAsync(representativeProduct.ProductUrl, logger);
        if (actual is null)
        {
            logger.LogInformation($"No product metadata found for product {representativeProduct.ProductId ?? representativeProduct.ProductUrl}; leaving stored values unchanged.");
            return;
        }

        var productId = actual.ProductId ?? representativeProduct.ProductId;
        var hasPriceChange = actual.Price is not null && products.Any(product =>
            product.ProductPrice is not null
            && !string.Equals(product.ProductPrice, actual.Price, StringComparison.Ordinal));
        if (hasPriceChange && productId is not null)
        {
            await RecordPriceChangeAsync(productId, actual.Price!, logger);
        }

        foreach (var product in products)
        {
            if (actual.IsActive is false)
            {
                if (product.IsActive is not false)
                {
                    await UpdateProductActivityAsync(product, false, logger);
                    await QueueProductInactiveNotificationAsync(product, logger);
                    continue;
                }

                await UpdateProductActivityAsync(product, false, logger);
                continue;
            }

            var nameChanged = product.ProductName is not null
                && actual.Name is not null
                && !string.Equals(product.ProductName, actual.Name, StringComparison.Ordinal);
            var priceChanged = product.ProductPrice is not null
                && actual.Price is not null
                && !string.Equals(product.ProductPrice, actual.Price, StringComparison.Ordinal);

            if (product.IsActive is false)
            {
                await UpdateProductAsync(product, actual, logger);
                await QueueProductReactivatedNotificationAsync(product, actual, logger);
                if (nameChanged || priceChanged)
                {
                    await QueueProductChangeNotificationAsync(product, actual, nameChanged, priceChanged, logger);
                }

                continue;
            }

            if (nameChanged || priceChanged)
            {
                await UpdateProductAsync(product, actual, logger);
                await QueueProductChangeNotificationAsync(product, actual, nameChanged, priceChanged, logger);
                continue;
            }

            await UpdateProductAsync(product, actual, logger);
        }
    }

    private async Task<OlxProductDetailsDto?> GetProductDetailsAsync(string productUrl, ILambdaLogger logger)
    {
        var product = await _olxProductClient.GetProductDetailsAsync(productUrl);
        logger.LogInformation($"OLX page request completed. Product metadata found: {product is not null}.");
        return product;
    }

    private async Task UpdateProductAsync(WatchedProductDto product, OlxProductDetailsDto actual, ILambdaLogger logger)
    {
        await _watchedProducts.UpdateFromOlxAsync(product, actual, DateTimeOffset.UtcNow);

        logger.LogInformation($"Updated watched product metadata for Telegram chat {product.ChatId}.");
    }

    private async Task UpdateProductActivityAsync(WatchedProductDto product, bool isActive, ILambdaLogger logger)
    {
        await _watchedProducts.UpdateActivityAsync(product, isActive, DateTimeOffset.UtcNow);

        logger.LogInformation($"Updated watched product activity to {isActive} for Telegram chat {product.ChatId}.");
    }

    private async Task RecordPriceChangeAsync(string productId, string productPrice, ILambdaLogger logger)
    {
        var changeDate = DateTimeOffset.UtcNow;
        await _priceHistory.RecordAsync(productId, productPrice, changeDate);
        logger.LogInformation($"Recorded a price change for product {productId} at {changeDate:O}.");
    }

    private async Task QueueProductChangeNotificationAsync(
        WatchedProductDto product,
        OlxProductDetailsDto actual,
        bool nameChanged,
        bool priceChanged,
        ILambdaLogger logger)
    {
        var changes = new List<string>();
        if (nameChanged)
        {
            changes.Add($"Name: {WebUtility.HtmlEncode(product.ProductName)} → {WebUtility.HtmlEncode(actual.Name)}");
        }

        if (priceChanged)
        {
            var oldPrice = await FormatPriceAsync(product.ProductPrice, actual.Currency, logger);
            var newPrice = await FormatPriceAsync(actual.Price, actual.Currency, logger);
            changes.Add($"Price: {oldPrice} → {newPrice}");
        }

        var productName = actual.Name ?? product.ProductName ?? "OLX product";
        await EnqueueNotificationAsync(product.ChatId, $"<b>{WebUtility.HtmlEncode(productName)}</b>\n{string.Join('\n', changes)}\n{WebUtility.HtmlEncode(product.ProductUrl)}", logger);
    }

    private async Task QueueProductInactiveNotificationAsync(WatchedProductDto product, ILambdaLogger logger)
    {
        var productName = product.ProductName ?? "Оголошення OLX";
        await EnqueueNotificationAsync(product.ChatId, $"<b>{WebUtility.HtmlEncode(productName)}</b>\nОголошення більше неактивне.\n{WebUtility.HtmlEncode(product.ProductUrl)}", logger);
    }

    private async Task QueueProductReactivatedNotificationAsync(
        WatchedProductDto product,
        OlxProductDetailsDto actual,
        ILambdaLogger logger)
    {
        var productName = actual.Name ?? product.ProductName ?? "Оголошення OLX";
        await EnqueueNotificationAsync(product.ChatId, $"<b>{WebUtility.HtmlEncode(productName)}</b>\nОголошення знову активне.\n{WebUtility.HtmlEncode(product.ProductUrl)}", logger);
    }

    private async Task EnqueueNotificationAsync(string chatId, string text, ILambdaLogger logger)
    {
        var notification = new TelegramNotificationDto
        {
            NotificationId = Guid.NewGuid().ToString("N"),
            ChatId = chatId,
            Text = text,
            ParseMode = "HTML",
            DisableWebPagePreview = true
        };
        await _notificationOutbox.EnqueueAsync(notification, DateTimeOffset.UtcNow);
        logger.LogInformation($"Queued Telegram notification {notification.NotificationId} for chat {chatId}.");
    }

    private async Task DeliverPendingNotificationsAsync(ILambdaLogger logger)
    {
        var notifications = await _notificationOutbox.GetPendingAsync(DateTimeOffset.UtcNow);
        var token = RequiredEnvironmentVariable("TELEGRAM_BOT_TOKEN");
        foreach (var notification in notifications)
        {
            try
            {
                await _telegramBotClient.SendMessageAsync(token, notification.ChatId, notification.Text, notification.ParseMode, notification.DisableWebPagePreview);
                await _notificationOutbox.MarkDeliveredAsync(notification.NotificationId);
                logger.LogInformation($"Delivered Telegram notification {notification.NotificationId} to chat {notification.ChatId}.");
            }
            catch (Exception exception)
            {
                await _notificationOutbox.ScheduleRetryAsync(notification.NotificationId, DateTimeOffset.UtcNow.AddMinutes(5));
                logger.LogError(exception, $"Unable to deliver Telegram notification {notification.NotificationId}.");
                await SendErrorNotificationAsync($"Доставка Telegram-повідомлення {notification.NotificationId}", exception, logger);
            }
        }
    }

    private static Task SendErrorNotificationAsync(string errorContext, Exception exception, ILambdaLogger logger) =>
        TelegramErrorNotifier.SendErrorNotificationSafelyAsync(
            HttpClient,
            "ListingsWatcher",
            errorContext,
            exception,
            logger);

    private static async Task<string> FormatPriceAsync(string? price, string? currency, ILambdaLogger logger)
    {
        if (string.IsNullOrWhiteSpace(price))
        {
            return "unknown";
        }

        if (!PriceFormatter.TryParse(price, out var value))
        {
            return WebUtility.HtmlEncode(price);
        }

        var formatted = PriceFormatter.FormatAmount(value);
        var currencyCode = string.IsNullOrWhiteSpace(currency) ? "UAH" : currency.ToUpperInvariant();

        if (currencyCode == "USD")
        {
            return $"{formatted} USD";
        }

        if (currencyCode != "UAH")
        {
            return $"{formatted} {WebUtility.HtmlEncode(currencyCode)}";
        }

        try
        {
            var usdValue = await ConvertUahToUsdAsync(value, logger);
            return usdValue is null
                ? $"{formatted} UAH"
                : $"{formatted} UAH (≈ ${usdValue.Value.ToString("N2", CultureInfo.InvariantCulture)})";
        }
        catch (Exception exception)
        {
            logger.LogInformation($"Could not retrieve an exchange rate for {currencyCode}: {exception.Message}");
            return $"{formatted} {WebUtility.HtmlEncode(currencyCode)}";
        }
    }

    private static async Task<decimal?> ConvertUahToUsdAsync(decimal amount, ILambdaLogger logger)
    {
        using var response = await HttpClient.GetAsync(NbuUsdRateUri);
        logger.LogInformation($"NBU exchange-rate request returned HTTP {(int)response.StatusCode}.");
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rate = document.RootElement.EnumerateArray().FirstOrDefault();
        if (rate.ValueKind != JsonValueKind.Object
            || !rate.TryGetProperty("rate", out var rateValue)
            || !rateValue.TryGetDecimal(out var uahPerUsd)
            || uahPerUsd <= 0)
        {
            return null;
        }

        return amount / uahPerUsd;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OlxWatcher/1.0");
        return client;
    }

    private static string RequiredEnvironmentVariable(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Required environment variable {name} is not set.");

}
