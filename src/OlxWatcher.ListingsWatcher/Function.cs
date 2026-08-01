using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.CloudWatchEvents;
using Amazon.Lambda.Core;
using OlxWatcher.Shared;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace OlxWatcher.ListingsWatcher;

public sealed class Function
{
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly Regex ScriptRegex = new(
        "<script\\b(?<attributes>[^>]*)>(?<content>.*?)</script>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Uri NbuUsdRateUri = new("https://bank.gov.ua/NBUStatService/v1/statdirectory/exchangenew?json&valcode=USD");
    private readonly IAmazonDynamoDB _dynamoDb;

    public Function() : this(new AmazonDynamoDBClient())
    {
    }

    internal Function(IAmazonDynamoDB dynamoDb) => _dynamoDb = dynamoDb;

    public async Task FunctionHandler(CloudWatchEvent<object> scheduledEvent, ILambdaContext context)
    {
        var logger = context.Logger;
        logger.LogInformation($"Starting scheduled listing check at {DateTimeOffset.UtcNow:O}.");

        Dictionary<string, AttributeValue>? lastEvaluatedKey = null;
        var processed = 0;
        do
        {
            var page = await _dynamoDb.ScanAsync(new ScanRequest
            {
                TableName = RequiredEnvironmentVariable("WATCHED_PRODUCTS_TABLE"),
                ExclusiveStartKey = lastEvaluatedKey
            });

            foreach (var item in page.Items)
            {
                var product = WatchedProduct.FromItem(item);
                if (product is null)
                {
                    logger.LogInformation("Skipping a DynamoDB item without a chat ID or product URL.");
                    continue;
                }

                try
                {
                    await CheckProductAsync(product, logger);
                    processed++;
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, $"Unable to check watched product for chat {product.ChatId}.");
                }
            }

            lastEvaluatedKey = page.LastEvaluatedKey;
        }
        while (lastEvaluatedKey is { Count: > 0 });

        logger.LogInformation($"Completed scheduled listing check. Processed {processed} watched products.");
    }

    private async Task CheckProductAsync(WatchedProduct product, ILambdaLogger logger)
    {
        logger.LogInformation($"Checking watched product for Telegram chat {product.ChatId}.");
        var actual = await GetProductDetailsAsync(product.ProductUrl, logger);
        if (actual is null)
        {
            logger.LogInformation($"No product metadata found for watched product in chat {product.ChatId}; leaving stored values unchanged.");
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

    private async Task<ProductDetails?> GetProductDetailsAsync(string productUrl, ILambdaLogger logger)
    {
        using var response = await HttpClient.GetAsync(productUrl);
        logger.LogInformation($"OLX page request returned HTTP {(int)response.StatusCode}.");
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var html = await response.Content.ReadAsStringAsync();
        foreach (Match match in ScriptRegex.Matches(html))
        {
            if (!match.Groups["attributes"].Value.Contains("application/ld+json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(match.Groups["content"].Value);
                var product = FindProduct(document.RootElement);
                if (product is not null)
                {
                    return product;
                }
            }
            catch (JsonException)
            {
                // Pages can contain unrelated malformed JSON-LD. Continue to the next script block.
            }
        }

        return null;
    }

    private async Task UpdateProductAsync(WatchedProduct product, ProductDetails actual, ILambdaLogger logger)
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
            Key = product.Key,
            UpdateExpression = $"SET {string.Join(", ", assignments)}",
            ExpressionAttributeValues = values
        });

        logger.LogInformation($"Updated watched product metadata for Telegram chat {product.ChatId}.");
    }

    private static async Task SendProductChangeAsync(
        WatchedProduct product,
        ProductDetails actual,
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

    private static ProductDetails? FindProduct(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (HasProductType(element))
            {
                var details = CreateProductDetails(element);
                if (details is not null)
                {
                    return details;
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                var nestedProduct = FindProduct(property.Value);
                if (nestedProduct is not null)
                {
                    return nestedProduct;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nestedProduct = FindProduct(item);
                if (nestedProduct is not null)
                {
                    return nestedProduct;
                }
            }
        }

        return null;
    }

    private static bool HasProductType(JsonElement element)
    {
        if (!element.TryGetProperty("@type", out var type))
        {
            return false;
        }

        return type.ValueKind switch
        {
            JsonValueKind.String => string.Equals(type.GetString(), "Product", StringComparison.OrdinalIgnoreCase),
            JsonValueKind.Array => type.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String
                && string.Equals(item.GetString(), "Product", StringComparison.OrdinalIgnoreCase)),
            _ => false
        };
    }

    private static ProductDetails? CreateProductDetails(JsonElement product)
    {
        var name = GetJsonString(product, "name");
        var (price, currency) = TryGetOffer(product);
        return name is null && price is null ? null : new ProductDetails(name, price, currency);
    }

    private static (string? Price, string? Currency) TryGetOffer(JsonElement product)
    {
        if (!product.TryGetProperty("offers", out var offers))
        {
            return (null, null);
        }

        if (offers.ValueKind == JsonValueKind.Array)
        {
            offers = offers.EnumerateArray().FirstOrDefault();
        }

        return offers.ValueKind == JsonValueKind.Object
            ? (GetJsonString(offers, "price"), GetJsonString(offers, "priceCurrency"))
            : (null, null);
    }

    private static string? GetJsonString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
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

    private sealed record ProductDetails(string? Name, string? Price, string? Currency);

    private sealed record WatchedProduct(
        string ChatId,
        string ProductUrl,
        string? ProductName,
        string? ProductPrice)
    {
        public Dictionary<string, AttributeValue> Key => new()
        {
            ["chatId"] = new() { S = ChatId },
            ["productUrl"] = new() { S = ProductUrl }
        };

        public static WatchedProduct? FromItem(IReadOnlyDictionary<string, AttributeValue> item)
        {
            var chatId = GetString(item, "chatId");
            var productUrl = GetString(item, "productUrl");
            return chatId is null || productUrl is null
                ? null
                : new WatchedProduct(
                    chatId,
                    productUrl,
                    GetString(item, "productName"),
                    GetString(item, "productPrice"));
        }

        private static string? GetString(IReadOnlyDictionary<string, AttributeValue> item, string attributeName)
        {
            if (!item.TryGetValue(attributeName, out var value) || value is null || value.NULL == true)
            {
                return null;
            }

            return value.S;
        }
    }
}
