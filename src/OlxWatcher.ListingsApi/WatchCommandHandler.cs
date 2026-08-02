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
using OlxWatcher.Shared.Telegram;

namespace OlxWatcher.ListingsApi;

public sealed class WatchCommandHandler
{
    private const string HelpMessage = "Надішліть /watch і URL товару з OLX або ID оголошення, щоб почати відстеження. Також можна просто надіслати URL або ID. Використовуйте /list, щоб переглянути товари.";
    private const int MaxProductsPerUser = 20;
    private static readonly HttpClient TelegramClient = new();
    private static readonly HttpClient OlxPageClient = CreateOlxPageClient();
    private readonly WatchedProductRepository _watchedProducts;
    private readonly OlxProductClient _olxProductClient;
    private readonly TelegramBotClient _telegramBotClient;

    public WatchCommandHandler() : this(new AmazonDynamoDBClient())
    {
    }

    internal WatchCommandHandler(IAmazonDynamoDB dynamoDb)
    {
        _watchedProducts = new WatchedProductRepository(dynamoDb, RequiredEnvironmentVariable("WATCHED_PRODUCTS_TABLE"));
        _olxProductClient = new OlxProductClient(OlxPageClient);
        _telegramBotClient = new TelegramBotClient(TelegramClient);
    }

    public async Task<APIGatewayHttpApiV2ProxyResponse> ProcessWebhookAsync(
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
            await SendErrorNotificationAsync($"Обробка Telegram update {update?.UpdateId}", exception, context.Logger);
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

        var watchedProducts = await _watchedProducts.GetByChatIdAsync(chatId);
        if (watchedProducts.Any(watchedProduct => string.Equals(watchedProduct.ProductId, product.Id, StringComparison.Ordinal)))
        {
            logger.LogInformation($"Product ID {product.Id} is already watched for Telegram chat {chatId}.");
            return "Ви вже відстежуєте це оголошення.";
        }

        if (watchedProducts.Count >= MaxProductsPerUser)
        {
            logger.LogInformation($"Telegram chat {chatId} has reached the watched-products limit.");
            return $"Ви можете відстежувати не більше {MaxProductsPerUser} оголошень.";
        }

        try
        {
            await _watchedProducts.AddAsync(new WatchedProductDto
            {
                ChatId = chatId,
                ProductUrl = product.Url,
                ProductId = product.Id,
                AddedAt = DateTimeOffset.UtcNow,
                ProductName = product.Name,
                ProductPrice = product.Price,
                IsActive = true
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
        await TelegramServiceNotifier.SendWatchAddedNotificationSafelyAsync(
            TelegramClient,
            "ListingsApi",
            chatId,
            productName,
            productPrice,
            product.Url,
            logger);
        return $"Відстежую:\n{productName}\nЦіна: {productPrice}\n{product.Url}";
    }

    private async Task<string> ListAsync(string chatId, ILambdaLogger logger)
    {
        var products = await _watchedProducts.GetByChatIdAsync(chatId);

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

    private async Task SendTelegramMessageAsync(string chatId, string text, ILambdaLogger logger)
    {
        var token = RequiredEnvironmentVariable("TELEGRAM_BOT_TOKEN");
        await _telegramBotClient.SendMessageAsync(token, chatId, text);
        logger.LogInformation($"Telegram sendMessage completed for chat {chatId}.");
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

    private async Task<OlxProductReference?> ResolveOlxProductAsync(string? value, ILambdaLogger logger)
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

    private async Task<OlxProductDetailsDto?> TryGetProductDetailsFromPageAsync(string productUrl, ILambdaLogger logger)
    {
        try
        {
            var product = await _olxProductClient.GetProductDetailsAsync(productUrl);
            logger.LogInformation($"OLX product lookup completed. Product metadata found: {product is not null}.");
            return product;
        }
        catch (HttpRequestException exception)
        {
            logger.LogInformation($"OLX product lookup failed: {exception.Message}");
            await SendErrorNotificationAsync("Отримання даних оголошення OLX", exception, logger);
            return null;
        }
        catch (TaskCanceledException exception)
        {
            logger.LogInformation($"OLX product lookup timed out: {exception.Message}");
            await SendErrorNotificationAsync("Отримання даних оголошення OLX", exception, logger);
            return null;
        }
    }

    private static HttpClient CreateOlxPageClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OlxWatcher/1.0");
        return client;
    }

    private static string RequiredEnvironmentVariable(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Required environment variable {name} is not set.");

    private static Task SendErrorNotificationAsync(string errorContext, Exception exception, ILambdaLogger logger) =>
        TelegramServiceNotifier.SendErrorNotificationSafelyAsync(
            TelegramClient,
            "ListingsApi",
            errorContext,
            exception,
            logger);

    private static APIGatewayHttpApiV2ProxyResponse Response(HttpStatusCode statusCode) => new()
    {
        StatusCode = (int)statusCode
    };

    private sealed record OlxProductReference(string Id, string Url, string? Name, string? Price);

}
