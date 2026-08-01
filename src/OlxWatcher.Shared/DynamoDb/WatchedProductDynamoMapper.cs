using Amazon.DynamoDBv2.Model;
using OlxWatcher.Shared.Dtos;

namespace OlxWatcher.Shared.DynamoDb;

public static class WatchedProductDynamoMapper
{
    public static WatchedProductDto? ToWatchedProduct(IReadOnlyDictionary<string, AttributeValue> item)
    {
        var chatId = GetString(item, "chatId");
        var productUrl = GetString(item, "productUrl");
        return chatId is null || productUrl is null
            ? null
            : new WatchedProductDto
            {
                ChatId = chatId,
                ProductUrl = productUrl,
                ProductId = GetString(item, "productId"),
                ProductName = GetString(item, "productName"),
                ProductPrice = GetString(item, "productPrice"),
                IsActive = GetBool(item, "isActive")
            };
    }

    public static Dictionary<string, AttributeValue> CreateKey(WatchedProductDto product) => new()
    {
        ["chatId"] = new() { S = product.ChatId },
        ["productUrl"] = new() { S = product.ProductUrl }
    };

    private static string? GetString(IReadOnlyDictionary<string, AttributeValue> item, string attributeName)
    {
        if (!item.TryGetValue(attributeName, out var value) || value is null || value.NULL == true)
        {
            return null;
        }

        return value.S;
    }

    private static bool? GetBool(IReadOnlyDictionary<string, AttributeValue> item, string attributeName)
    {
        if (!item.TryGetValue(attributeName, out var value) || value is null || value.NULL == true)
        {
            return null;
        }

        return value.BOOL;
    }
}
