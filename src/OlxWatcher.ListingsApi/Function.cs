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
        var parts = text.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].Split('@', 2)[0].ToLowerInvariant();
        logger.LogInformation($"Received Telegram command {command} for chat {chatId}.");

        return command switch
        {
            "/watch" => await WatchAsync(chatId, parts.ElementAtOrDefault(1), logger),
            "/list" => await ListAsync(chatId, logger),
            "/start" or "/help" => "Надішліть /watch і URL товару з OLX або ID оголошення, щоб почати відстеження. Використовуйте /list, щоб переглянути товари.",
            _ => null
        };
    }

    private async Task<string> WatchAsync(string chatId, string? urlText, ILambdaLogger logger)
    {
        var product = await ResolveOlxProductAsync(urlText, logger);
        if (product is null)
        {
            return "Не вдалося визначити ID оголошення. Надішліть коректний URL з OLX або числовий ID оголошення.";
        }

        await _dynamoDb.PutItemAsync(new PutItemRequest
        {
            TableName = RequiredEnvironmentVariable("WATCHED_PRODUCTS_TABLE"),
            Item = new Dictionary<string, AttributeValue>
            {
                ["chatId"] = new() { S = chatId },
                ["productUrl"] = new() { S = product.Url },
                ["productId"] = new() { S = product.Id },
                ["addedAt"] = new() { S = DateTimeOffset.UtcNow.ToString("O") },
                ["productName"] = new() { NULL = true },
                ["productPrice"] = new() { NULL = true },
                ["isActive"] = new() { NULL = true }
            }
        });

        logger.LogInformation($"Saved a watched product for Telegram chat {chatId}.");
        return $"Відстежую:\n{product.Url}";
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

        return FormatWatchedProducts(products);
    }

    private static string FormatWatchedProducts(IReadOnlyList<WatchedProductDto> products)
    {
        var responseText = "Відстежувані товари:\n" + string.Join(
            "\n\n",
            products.Select((product, index) =>
                $"{index + 1}. {product.ProductName ?? "Без назви"}\n"
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
            return new OlxProductReference(reference, $"https://www.olx.ua/d/uk/{reference}");
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
        var productId = await GetProductIdFromPageAsync(productUrl, logger);
        return productId is null ? null : new OlxProductReference(productId, productUrl);
    }

    private static async Task<string?> GetProductIdFromPageAsync(string productUrl, ILambdaLogger logger)
    {
        using var response = await OlxPageClient.GetAsync(productUrl);
        logger.LogInformation($"OLX product-ID lookup returned HTTP {(int)response.StatusCode}.");
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return OlxProductPageParser.Parse(await response.Content.ReadAsStringAsync())?.ProductId;
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

    private static APIGatewayHttpApiV2ProxyResponse Response(HttpStatusCode statusCode) => new()
    {
        StatusCode = (int)statusCode
    };

    private sealed record OlxProductReference(string Id, string Url);

}
