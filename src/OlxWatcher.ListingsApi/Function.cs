using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using OlxWatcher.ListingsApi.Dtos;
using OlxWatcher.Shared;
using OlxWatcher.Shared.DynamoDb;
using OlxWatcher.Shared.Dtos;
using OlxWatcher.Shared.Olx;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace OlxWatcher.ListingsApi;

public sealed class Function
{
    private const string HelpMessage = "Надішліть /watch і URL товару з OLX або ID оголошення, щоб почати відстеження. Також можна просто надіслати URL або ID. Використовуйте /list, щоб переглянути товари.";
    private const int MaxActiveProductsPerUser = 20;
    private static readonly HttpClient TelegramClient = new();
    private static readonly HttpClient OlxPageClient = CreateOlxPageClient();
    private readonly IAmazonDynamoDB _dynamoDb;

    public Function() : this(new AmazonDynamoDBClient())
    {
    }

    internal Function(IAmazonDynamoDB dynamoDb) => _dynamoDb = dynamoDb;

    public async Task<APIGatewayHttpApiV2ProxyResponse> FunctionHandler(
        APIGatewayHttpApiV2ProxyRequest request,
        ILambdaContext context)
    {
        context.Logger.LogInformation("Received Telegram webhook request.");
        if (!HasValidWebhookSecret(request))
        {
            context.Logger.LogInformation("Rejected Telegram webhook request because the secret header did not match.");
            return Response(HttpStatusCode.Unauthorized);
        }

        TelegramUpdate? update;
        try
        {
            update = JsonSerializer.Deserialize<TelegramUpdate>(request.Body ?? string.Empty);
        }
        catch (JsonException)
        {
            context.Logger.LogInformation("Ignoring Telegram webhook request with an invalid JSON body.");
            return Response(HttpStatusCode.OK);
        }

        var message = update?.Message;
        if (message?.Chat is null || string.IsNullOrWhiteSpace(message.Text))
        {
            context.Logger.LogInformation($"Ignoring Telegram update {update?.UpdateId}: no text message was present.");
            return Response(HttpStatusCode.OK);
        }

        var chatId = message.Chat.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            context.Logger.LogInformation($"Processing Telegram update {update?.UpdateId} for chat {chatId}.");
            var reply = await ProcessCommandAsync(chatId, message.Text, context.Logger);
            if (reply is not null)
            {
                context.Logger.LogInformation($"Sending Telegram reply for update {update?.UpdateId} to chat {chatId}.");
                await SendTelegramMessageAsync(chatId, reply, context.Logger);
            }
            else
            {
                context.Logger.LogInformation($"Ignoring unsupported command in Telegram update {update?.UpdateId}.");
            }
        }
        catch (Exception exception)
        {
            context.Logger.LogError(exception, "Unable to process Telegram update.");
            return Response(HttpStatusCode.InternalServerError);
        }

        return Response(HttpStatusCode.OK);
    }

    private async Task<string?> ProcessCommandAsync(string chatId, string text, ILambdaLogger logger)
    {
        var trimmedText = text.Trim();
        var parts = trimmedText.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].Split('@', 2)[0].ToLowerInvariant();
        logger.LogInformation($"Received Telegram command {command} for chat {chatId}.");

        return command switch
        {
            "/watch" => await WatchAsync(chatId, parts.ElementAtOrDefault(1), logger),
            "/list" => await ListAsync(chatId, logger),
            "/start" or "/help" => HelpMessage,
            _ when IsProductReference(trimmedText) => await WatchAsync(chatId, trimmedText, logger),
            _ => HelpMessage
        };
    }

    private static bool IsProductReference(string value) =>
        OlxProductPageParser.IsValidProductId(value)
        || Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    private async Task<string> WatchAsync(string chatId, string? urlText, ILambdaLogger logger)
    {
        var product = await ResolveOlxProductAsync(urlText, logger);
        if (product is null)
        {
            return "Не вдалося визначити ID оголошення. Надішліть коректний URL з OLX або числовий ID оголошення.";
        }

        if (await IsProductAlreadyWatchedAsync(chatId, product.Id))
        {
            logger.LogInformation($"Product ID {product.Id} is already watched for Telegram chat {chatId}.");
            return "Ви вже відстежуєте це оголошення.";
        }

        if (await GetActiveProductCountAsync(chatId) >= MaxActiveProductsPerUser)
        {
            logger.LogInformation($"Telegram chat {chatId} has reached the active watched-products limit.");
            return $"Ви можете відстежувати не більше {MaxActiveProductsPerUser} активних оголошень.";
        }

        try
        {
            await _dynamoDb.PutItemAsync(new PutItemRequest
            {
                TableName = RequiredEnvironmentVariable("WATCHED_PRODUCTS_TABLE"),
                ConditionExpression = "attribute_not_exists(chatId) AND attribute_not_exists(productUrl)",
                Item = new Dictionary<string, AttributeValue>
                {
                    ["chatId"] = new() { S = chatId },
                    ["productUrl"] = new() { S = product.Url },
                    ["productId"] = new() { S = product.Id },
                    ["addedAt"] = new() { S = DateTimeOffset.UtcNow.ToString("O") },
                    ["productName"] = NullableString(product.Name),
                    ["productPrice"] = NullableString(product.Price),
                    ["isActive"] = new() { NULL = true }
                }
            });
        }
        catch (ConditionalCheckFailedException)
        {
            logger.LogInformation($"Product ID {product.Id} was added concurrently for Telegram chat {chatId}.");
            return "Ви вже відстежуєте це оголошення.";
        }

        logger.LogInformation($"Saved a watched product for Telegram chat {chatId}. Name and price resolved: {product.Name is not null}/{product.Price is not null}.");
        var productName = product.Name ?? "Без назви";
        var productPrice = product.Price is null ? "не вказано" : PriceFormatter.FormatUah(product.Price);
        return $"Відстежую:\n{productName}\nЦіна: {productPrice}\n{product.Url}";
    }

    private async Task<bool> IsProductAlreadyWatchedAsync(string chatId, string productId)
    {
        Dictionary<string, AttributeValue>? lastEvaluatedKey = null;
        do
        {
            var response = await _dynamoDb.QueryAsync(new QueryRequest
            {
                TableName = RequiredEnvironmentVariable("WATCHED_PRODUCTS_TABLE"),
                KeyConditionExpression = "chatId = :chatId",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":chatId"] = new() { S = chatId }
                },
                ExclusiveStartKey = lastEvaluatedKey
            });

            if (response.Items
                .Select(WatchedProductDynamoMapper.ToWatchedProduct)
                .OfType<WatchedProductDto>()
                .Any(watchedProduct => string.Equals(watchedProduct.ProductId, productId, StringComparison.Ordinal)))
            {
                return true;
            }

            lastEvaluatedKey = response.LastEvaluatedKey;
        }
        while (lastEvaluatedKey is { Count: > 0 });

        return false;
    }

    private async Task<int> GetActiveProductCountAsync(string chatId)
    {
        Dictionary<string, AttributeValue>? lastEvaluatedKey = null;
        var count = 0;
        do
        {
            var response = await _dynamoDb.QueryAsync(new QueryRequest
            {
                TableName = RequiredEnvironmentVariable("WATCHED_PRODUCTS_TABLE"),
                KeyConditionExpression = "chatId = :chatId",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":chatId"] = new() { S = chatId }
                },
                ExclusiveStartKey = lastEvaluatedKey
            });

            count += response.Items
                .Select(WatchedProductDynamoMapper.ToWatchedProduct)
                .OfType<WatchedProductDto>()
                .Count(product => product.IsActive is not false);
            lastEvaluatedKey = response.LastEvaluatedKey;
        }
        while (lastEvaluatedKey is { Count: > 0 });

        return count;
    }

    private async Task<string> ListAsync(string chatId, ILambdaLogger logger)
    {
        var products = new List<WatchedProductDto>();
        Dictionary<string, AttributeValue>? lastEvaluatedKey = null;

        do
        {
            var response = await _dynamoDb.QueryAsync(new QueryRequest
            {
                TableName = RequiredEnvironmentVariable("WATCHED_PRODUCTS_TABLE"),
                KeyConditionExpression = "chatId = :chatId",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":chatId"] = new() { S = chatId }
                },
                ExclusiveStartKey = lastEvaluatedKey
            });

            products.AddRange(response.Items
                .Select(WatchedProductDynamoMapper.ToWatchedProduct)
                .OfType<WatchedProductDto>());
            lastEvaluatedKey = response.LastEvaluatedKey;
        }
        while (lastEvaluatedKey is { Count: > 0 });

        logger.LogInformation($"Retrieved {products.Count} watched products for Telegram chat {chatId}.");

        if (products.Count == 0)
        {
            return "Ви ще не відстежуєте жодного товару. Скористайтеся /watch <URL з OLX>.";
        }

        return FormatWatchedProducts(products
            .OrderBy(product => product.AddedAt)
            .ToList());
    }

    private static string FormatWatchedProducts(IReadOnlyList<WatchedProductDto> products)
    {
        var responseText = "Відстежувані товари:\n" + string.Join(
            "\n\n",
            products.Select((product, index) =>
                $"{index + 1}. {product.ProductName ?? "Без назви"}\n"
                + (product.IsActive is false ? "⚠️ Неактивне\n" : string.Empty)
                + $"Ціна: {(product.ProductPrice is null ? "не вказано" : PriceFormatter.FormatUah(product.ProductPrice))}\n"
                + product.ProductUrl));
        return responseText.Length <= 4096 ? responseText : responseText[..4093] + "...";
    }

    private static async Task SendTelegramMessageAsync(string chatId, string text, ILambdaLogger logger)
    {
        var token = RequiredEnvironmentVariable("TELEGRAM_BOT_TOKEN");
        using var response = await TelegramClient.PostAsJsonAsync(
            $"https://api.telegram.org/bot{token}/sendMessage",
            new { chat_id = chatId, text });
        logger.LogInformation($"Telegram sendMessage returned HTTP {(int)response.StatusCode} for chat {chatId}.");
        response.EnsureSuccessStatusCode();
    }

    private static bool HasValidWebhookSecret(APIGatewayHttpApiV2ProxyRequest request)
    {
        var expectedSecret = Environment.GetEnvironmentVariable("TELEGRAM_WEBHOOK_SECRET");
        if (string.IsNullOrEmpty(expectedSecret))
        {
            return true;
        }

        var suppliedSecret = request.Headers?
            .FirstOrDefault(header => string.Equals(
                header.Key,
                "x-telegram-bot-api-secret-token",
                StringComparison.OrdinalIgnoreCase))
            .Value;

        return string.Equals(suppliedSecret, expectedSecret, StringComparison.Ordinal);
    }

    private static async Task<OlxProductReference?> ResolveOlxProductAsync(string? value, ILambdaLogger logger)
    {
        var reference = value?.Trim();
        if (string.IsNullOrEmpty(reference))
        {
            return null;
        }

        if (OlxProductPageParser.IsValidProductId(reference))
        {
            var idProductUrl = $"https://www.olx.ua/d/uk/{reference}";
            var idProductDetails = await TryGetProductDetailsFromPageAsync(idProductUrl, logger);

            if (string.IsNullOrEmpty(idProductDetails?.Name) && string.IsNullOrEmpty(idProductDetails?.Price))
            {
                return null;
            }

            return new OlxProductReference(
                idProductDetails?.ProductId ?? reference,
                idProductUrl,
                idProductDetails?.Name,
                idProductDetails?.Price);
        }

        if (!Uri.TryCreate(reference, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        var host = uri.Host;
        if (!host.StartsWith("olx.", StringComparison.OrdinalIgnoreCase)
            && !host.StartsWith("www.olx.", StringComparison.OrdinalIgnoreCase)
            && !host.Contains(".olx.", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".olx", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        var productUrl = builder.Uri.AbsoluteUri;
        var productDetails = await TryGetProductDetailsFromPageAsync(productUrl, logger);
        return productDetails?.ProductId is null
            ? null
            : new OlxProductReference(productDetails.ProductId, productUrl, productDetails.Name, productDetails.Price);
    }

    private static async Task<OlxProductDetailsDto?> TryGetProductDetailsFromPageAsync(string productUrl, ILambdaLogger logger)
    {
        try
        {
            using var response = await OlxPageClient.GetAsync(productUrl);
            logger.LogInformation($"OLX product lookup returned HTTP {(int)response.StatusCode}.");
            return response.IsSuccessStatusCode
                ? OlxProductPageParser.Parse(await response.Content.ReadAsStringAsync())
                : null;
        }
        catch (HttpRequestException exception)
        {
            logger.LogInformation($"OLX product lookup failed: {exception.Message}");
            return null;
        }
        catch (TaskCanceledException exception)
        {
            logger.LogInformation($"OLX product lookup timed out: {exception.Message}");
            return null;
        }
    }

    private static AttributeValue NullableString(string? value) => value is null
        ? new AttributeValue { NULL = true }
        : new AttributeValue { S = value };

    private static HttpClient CreateOlxPageClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OlxWatcher/1.0");
        return client;
    }

    private static string RequiredEnvironmentVariable(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Required environment variable {name} is not set.");

    private static APIGatewayHttpApiV2ProxyResponse Response(HttpStatusCode statusCode) => new()
    {
        StatusCode = (int)statusCode
    };

    private sealed record OlxProductReference(string Id, string Url, string? Name, string? Price);

}
