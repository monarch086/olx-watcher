using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using OlxWatcher.Shared.Dtos;

namespace OlxWatcher.Shared.DynamoDb;

public sealed class WatchedProductRepository
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    public WatchedProductRepository(IAmazonDynamoDB dynamoDb, string tableName)
    {
        _dynamoDb = dynamoDb;
        _tableName = tableName;
    }

    public async Task<IReadOnlyList<WatchedProductDto>> GetByChatIdAsync(string chatId, CancellationToken cancellationToken = default)
    {
        var products = new List<WatchedProductDto>();
        Dictionary<string, AttributeValue>? lastEvaluatedKey = null;
        do
        {
            var response = await _dynamoDb.QueryAsync(new QueryRequest
            {
                TableName = _tableName,
                KeyConditionExpression = "chatId = :chatId",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":chatId"] = new() { S = chatId }
                },
                ExclusiveStartKey = lastEvaluatedKey
            }, cancellationToken);
            products.AddRange(response.Items.Select(WatchedProductDynamoMapper.ToWatchedProduct).OfType<WatchedProductDto>());
            lastEvaluatedKey = response.LastEvaluatedKey;
        }
        while (lastEvaluatedKey is { Count: > 0 });

        return products;
    }

    public async Task<IReadOnlyList<WatchedProductDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var products = new List<WatchedProductDto>();
        Dictionary<string, AttributeValue>? lastEvaluatedKey = null;
        do
        {
            var response = await _dynamoDb.ScanAsync(new ScanRequest
            {
                TableName = _tableName,
                ExclusiveStartKey = lastEvaluatedKey
            }, cancellationToken);
            products.AddRange(response.Items.Select(WatchedProductDynamoMapper.ToWatchedProduct).OfType<WatchedProductDto>());
            lastEvaluatedKey = response.LastEvaluatedKey;
        }
        while (lastEvaluatedKey is { Count: > 0 });

        return products;
    }

    public Task AddAsync(WatchedProductDto product, CancellationToken cancellationToken = default) =>
        _dynamoDb.PutItemAsync(new PutItemRequest
        {
            TableName = _tableName,
            ConditionExpression = "attribute_not_exists(chatId) AND attribute_not_exists(productUrl)",
            Item = new Dictionary<string, AttributeValue>
            {
                ["chatId"] = new() { S = product.ChatId },
                ["productUrl"] = new() { S = product.ProductUrl },
                ["productId"] = NullableString(product.ProductId),
                ["addedAt"] = new() { S = (product.AddedAt ?? DateTimeOffset.UtcNow).ToString("O") },
                ["productName"] = NullableString(product.ProductName),
                ["productPrice"] = NullableString(product.ProductPrice),
                ["isActive"] = product.IsActive is null ? new AttributeValue { NULL = true } : new AttributeValue { BOOL = product.IsActive.Value }
            }
        }, cancellationToken);

    public Task UpdateFromOlxAsync(WatchedProductDto product, OlxProductDetailsDto actual, DateTimeOffset checkedAt, CancellationToken cancellationToken = default)
    {
        var assignments = new List<string> { "isActive = :isActive", "lastCheckedAt = :lastCheckedAt" };
        var values = new Dictionary<string, AttributeValue>
        {
            [":isActive"] = new() { BOOL = true },
            [":lastCheckedAt"] = new() { S = checkedAt.ToString("O") }
        };
        if (actual.Name is not null)
        {
            assignments.Add("productName = :productName");
            values[":productName"] = new() { S = actual.Name };
        }
        if (actual.Price is not null)
        {
            assignments.Add("productPrice = :productPrice");
            values[":productPrice"] = new() { S = actual.Price };
        }

        return _dynamoDb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _tableName,
            Key = WatchedProductDynamoMapper.CreateKey(product),
            UpdateExpression = $"SET {string.Join(", ", assignments)}",
            ExpressionAttributeValues = values
        }, cancellationToken);
    }

    public Task UpdateActivityAsync(WatchedProductDto product, bool isActive, DateTimeOffset checkedAt, CancellationToken cancellationToken = default) =>
        _dynamoDb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _tableName,
            Key = WatchedProductDynamoMapper.CreateKey(product),
            UpdateExpression = "SET isActive = :isActive, lastCheckedAt = :lastCheckedAt",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":isActive"] = new() { BOOL = isActive },
                [":lastCheckedAt"] = new() { S = checkedAt.ToString("O") }
            }
        }, cancellationToken);

    private static AttributeValue NullableString(string? value) => value is null
        ? new AttributeValue { NULL = true }
        : new AttributeValue { S = value };
}
