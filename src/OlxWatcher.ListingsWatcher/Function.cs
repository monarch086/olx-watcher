using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.CloudWatchEvents;
using Amazon.Lambda.Core;
using OlxWatcher.Shared;
using OlxWatcher.Shared.DynamoDb;
using OlxWatcher.Shared.Dtos;
using OlxWatcher.Shared.Olx;
using OlxWatcher.Shared.Telegram;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace OlxWatcher.ListingsWatcher;

public sealed class Function
{
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly Uri NbuUsdRateUri = new("https://bank.gov.ua/NBUStatService/v1/statdirectory/exchangenew?json&valcode=USD");
    private readonly IAmazonDynamoDB _dynamoDb;

    public Function() : this(new AmazonDynamoDBClient())
    {
    }

    internal Function(IAmazonDynamoDB dynamoDb) => _dynamoDb = dynamoDb;

    public async Task FunctionHandler(CloudWatchEvent<object> scheduledEvent, ILambdaContext context)
    {
        var logger = context.Logger;
        try
        {
            logger.LogInformation($"Starting scheduled listing check at {DateTimeOffset.UtcNow:O}.");

            Dictionary<string, AttributeValue>? lastEvaluatedKey = null;
            var processed = 0;
            var maxConcurrency = GetMaxCheckConcurrency();
            logger.LogInformation($"Checking watched products with a maximum concurrency of {maxConcurrency}.");
            do
            {
                var page = await _dynamoDb.ScanAsync(new ScanRequest
                {
                    TableName = RequiredEnvironmentVariable("WATCHED_PRODUCTS_TABLE"),
                    ExclusiveStartKey = lastEvaluatedKey
                });

                await Parallel.ForEachAsync(
                    page.Items,
                    new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency },
                    async (item, _) =>
                    {
                        var product = WatchedProductDynamoMapper.ToWatchedProduct(item);
                        if (product is null)
                        {
                            logger.LogInformation("Skipping a DynamoDB item without a chat ID or product URL.");
                            return;
                        }

                        if (!ShouldCheckProduct(product, logger))
                        {
                            return;
                        }

                        try
                        {
                            await CheckProductAsync(product, logger);
                            Interlocked.Increment(ref processed);
                        }
                        catch (Exception exception)
                        {
                            const string errorContext = "Перевірка оголошення";
                            logger.LogError(exception, $"Unable to check watched product for chat {product.ChatId}.");
                            await SendErrorNotificationAsync($"{errorContext}, чат {product.ChatId}", exception, logger);
                        }
                    });

                lastEvaluatedKey = page.LastEvaluatedKey;
            }
            while (lastEvaluatedKey is { Count: > 0 });

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

        var nextCheckAt = product.LastCheckedAt.Value.AddDays(7);
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

    private async Task CheckProductAsync(WatchedProductDto product, ILambdaLogger logger)
    {
        logger.LogInformation($"Checking watched product for Telegram chat {product.ChatId}.");
        var actual = await GetProductDetailsAsync(product.ProductUrl, logger);
        if (actual is null)
        {
            logger.LogInformation($"No product metadata found for watched product in chat {product.ChatId}; leaving stored values unchanged.");
            return;
        }

        if (actual.IsActive is false)
        {
            if (product.IsActive is not false)
            {
                await SendProductInactiveAsync(product, logger);
            }

            await UpdateProductActivityAsync(product, false, logger);
            return;
        }

        var nameChanged = product.ProductName is not null
            && actual.Name is not null
            && !string.Equals(product.ProductName, actual.Name, StringComparison.Ordinal);
        var priceChanged = product.ProductPrice is not null
            && actual.Price is not null
            && !string.Equals(product.ProductPrice, actual.Price, StringComparison.Ordinal);

        if (nameChanged || priceChanged)
        {
            await SendProductChangeAsync(product, actual, nameChanged, priceChanged, logger);
        }

        await UpdateProductAsync(product, actual, logger);
    }

    private async Task<OlxProductDetailsDto?> GetProductDetailsAsync(string productUrl, ILambdaLogger logger)
    {
        using var response = await HttpClient.GetAsync(productUrl);
        logger.LogInformation($"OLX page request returned HTTP {(int)response.StatusCode}.");
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return OlxProductPageParser.Parse(await response.Content.ReadAsStringAsync());
    }

    private async Task UpdateProductAsync(WatchedProductDto product, OlxProductDetailsDto actual, ILambdaLogger logger)
    {
        var assignments = new List<string>
        {
            "isActive = :isActive",
            "lastCheckedAt = :lastCheckedAt"
        };
        var values = new Dictionary<string, AttributeValue>
        {
            [":isActive"] = new() { BOOL = true },
            [":lastCheckedAt"] = new() { S = DateTimeOffset.UtcNow.ToString("O") }
        };

        if (actual.Name is not null)
        {
            assignments.Add("productName = :productName");
            values[":productName"] = new AttributeValue { S = actual.Name };
        }

        if (actual.Price is not null)
        {
            assignments.Add("productPrice = :productPrice");
            values[":productPrice"] = new AttributeValue { S = actual.Price };
        }

        await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = RequiredEnvironmentVariable("WATCHED_PRODUCTS_TABLE"),
            Key = WatchedProductDynamoMapper.CreateKey(product),
            UpdateExpression = $"SET {string.Join(", ", assignments)}",
            ExpressionAttributeValues = values
        });

        logger.LogInformation($"Updated watched product metadata for Telegram chat {product.ChatId}.");
    }

    private async Task UpdateProductActivityAsync(WatchedProductDto product, bool isActive, ILambdaLogger logger)
    {
        await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = RequiredEnvironmentVariable("WATCHED_PRODUCTS_TABLE"),
            Key = WatchedProductDynamoMapper.CreateKey(product),
            UpdateExpression = "SET isActive = :isActive, lastCheckedAt = :lastCheckedAt",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":isActive"] = new() { BOOL = isActive },
                [":lastCheckedAt"] = new() { S = DateTimeOffset.UtcNow.ToString("O") }
            }
        });

        logger.LogInformation($"Updated watched product activity to {isActive} for Telegram chat {product.ChatId}.");
    }

    private static async Task SendProductChangeAsync(
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
        var token = RequiredEnvironmentVariable("TELEGRAM_BOT_TOKEN");
        using var response = await HttpClient.PostAsJsonAsync(
            $"https://api.telegram.org/bot{token}/sendMessage",
            new
            {
                chat_id = product.ChatId,
                text = $"<b>{WebUtility.HtmlEncode(productName)}</b>\n{string.Join('\n', changes)}\n{WebUtility.HtmlEncode(product.ProductUrl)}",
                parse_mode = "HTML",
                disable_web_page_preview = true
            });
        logger.LogInformation($"Telegram product-change notification returned HTTP {(int)response.StatusCode} for chat {product.ChatId}.");
        response.EnsureSuccessStatusCode();
    }

    private static async Task SendProductInactiveAsync(WatchedProductDto product, ILambdaLogger logger)
    {
        var productName = product.ProductName ?? "Оголошення OLX";
        var token = RequiredEnvironmentVariable("TELEGRAM_BOT_TOKEN");
        using var response = await HttpClient.PostAsJsonAsync(
            $"https://api.telegram.org/bot{token}/sendMessage",
            new
            {
                chat_id = product.ChatId,
                text = $"<b>{WebUtility.HtmlEncode(productName)}</b>\nОголошення більше неактивне.\n{WebUtility.HtmlEncode(product.ProductUrl)}",
                parse_mode = "HTML",
                disable_web_page_preview = true
            });
        logger.LogInformation($"Telegram inactive-product notification returned HTTP {(int)response.StatusCode} for chat {product.ChatId}.");
        response.EnsureSuccessStatusCode();
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
