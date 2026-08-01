using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace OlxWatcher.ListingsApi;

public sealed class Function
{
    private static readonly HttpClient TelegramClient = new();
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
            "/start" or "/help" => "Send /watch followed by an OLX product URL to start watching it. Use /list to see watched products.",
            _ => null
        };
    }

    private async Task<string> WatchAsync(string chatId, string? urlText, ILambdaLogger logger)
    {
        if (!IsOlxUrl(urlText, out var productUrl))
        {
            return "Usage: /watch https://www.olx.../product-url";
        }

        await _dynamoDb.PutItemAsync(new PutItemRequest
        {
            TableName = RequiredEnvironmentVariable("WATCHED_PRODUCTS_TABLE"),
            Item = new Dictionary<string, AttributeValue>
            {
                ["chatId"] = new() { S = chatId },
                ["productUrl"] = new() { S = productUrl },
                ["addedAt"] = new() { S = DateTimeOffset.UtcNow.ToString("O") },
                ["productName"] = new() { NULL = true },
                ["productPrice"] = new() { NULL = true },
                ["isActive"] = new() { NULL = true }
            }
        });

        logger.LogInformation($"Saved a watched product for Telegram chat {chatId}.");
        return $"Watching:\n{productUrl}";
    }

    private async Task<string> ListAsync(string chatId, ILambdaLogger logger)
    {
        var response = await _dynamoDb.QueryAsync(new QueryRequest
        {
            TableName = RequiredEnvironmentVariable("WATCHED_PRODUCTS_TABLE"),
            KeyConditionExpression = "chatId = :chatId",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":chatId"] = new() { S = chatId }
            }
        });

        var urls = response.Items
            .Select(item => item["productUrl"].S)
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .ToList();

        logger.LogInformation($"Retrieved {urls.Count} watched products for Telegram chat {chatId}.");

        if (urls.Count == 0)
        {
            return "You are not watching any products yet. Use /watch <OLX URL>.";
        }

        return FormatWatchedProducts(urls);
    }

    private static string FormatWatchedProducts(IReadOnlyList<string> urls)
    {
        var responseText = "Watched products:\n" + string.Join('\n', urls.Select((url, index) => $"{index + 1}. {url}"));
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

    private static bool IsOlxUrl(string? value, out string productUrl)
    {
        productUrl = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var host = uri.Host;
        if (!host.StartsWith("olx.", StringComparison.OrdinalIgnoreCase)
            && !host.StartsWith("www.olx.", StringComparison.OrdinalIgnoreCase)
            && !host.Contains(".olx.", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".olx", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        productUrl = builder.Uri.AbsoluteUri;
        return true;
    }

    private static string RequiredEnvironmentVariable(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Required environment variable {name} is not set.");

    private static APIGatewayHttpApiV2ProxyResponse Response(HttpStatusCode statusCode) => new()
    {
        StatusCode = (int)statusCode
    };

    private sealed class TelegramUpdate
    {
        [JsonPropertyName("update_id")]
        public long UpdateId { get; init; }

        [JsonPropertyName("message")]
        public TelegramMessage? Message { get; init; }
    }

    private sealed class TelegramMessage
    {
        [JsonPropertyName("chat")]
        public TelegramChat? Chat { get; init; }

        [JsonPropertyName("text")]
        public string? Text { get; init; }
    }

    private sealed class TelegramChat
    {
        [JsonPropertyName("id")]
        public long Id { get; init; }
    }
}
